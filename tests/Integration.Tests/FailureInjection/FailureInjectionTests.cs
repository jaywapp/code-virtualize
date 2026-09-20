using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Fallback;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Integration.Tests.FailureInjection;

internal static class FailureInjectionTests
{
    public static void Run()
    {
        CorruptRangeNeverReturnsSource();
        MissedUpdateAndRenameMatchIndependentSource();
        UnchangedSyntaxFailureRemainsInCoverage();
        TimeoutAndCancellationNeverReturnStaleSource();
        RunFixtureVerifier();
    }

    private static void CorruptRangeNeverReturnsSource()
    {
        using var fixture = new VerificationFixture("corrupt-range");
        fixture.Write("Fixture.cs", "namespace Verification; public sealed class RangeTarget { public int Read() => 7; }");
        var build = fixture.Build();
        var symbol = build.Symbols.Single(item => item.Name == "Read");
        var declaration = symbol.Declarations.Single();
        var corruptSymbol = symbol with
        {
            Declarations = [declaration with
            {
                Location = declaration.Location with
                {
                    Span = declaration.Location.Span with { Start = 1_000_000, StartLine = 1, EndLine = 1 }
                }
            }]
        };
        var corruptGeneration = build.Manifest with
        {
            GenerationId = $"gen_corrupt_range_{Guid.NewGuid():N}",
            Shards = []
        };
        new GenerationStore(fixture.StorePath).Publish(new GenerationPublishRequest(
            corruptGeneration,
            [new GenerationShardWriteRequest("symbol", "symbols.jsonl", ContractSchemas.Symbol, [ContractJson.Serialize(corruptSymbol)])]));

        var response = new SourceResolver().Resolve(new ResolveRequest(
            fixture.WorkspacePath,
            fixture.StorePath,
            symbol.SymbolId,
            SourcePart.Body,
            new SourceBudgetContract(4096, 40),
            corruptGeneration.GenerationId));

        Assert(response.Source is null, "A corrupt indexed range must never return source bytes.");
        Assert(response.Status == ResponseStatus.Error && response.Errors.Any(error => error.Code == ResolutionErrorCodes.SpanInvalid),
            "A corrupt indexed range must return SOURCE_SPAN_INVALID explicitly.");
    }

    private static void MissedUpdateAndRenameMatchIndependentSource()
    {
        using var fixture = new VerificationFixture("missed-update");
        fixture.Write("Original.cs", "namespace Verification; public sealed class BeforeRename { public string Read() => \"before\"; }");
        var service = new CSharpLifecycleService();
        _ = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "session-update"));

        fixture.Write("Missed.cs", "namespace Verification; public sealed class AddedWithoutHook { public string Read() => \"added\"; }");
        var missed = service.Update(new CSharpUpdateRequest(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-update",
            ["Original.cs"],
            WriterWait: TimeSpan.FromSeconds(1)));
        Assert(missed.Reconciliation.Changes.Any(change =>
                change.Kind == CodeVirtualize.Core.Lifecycle.WorkspaceChangeKind.Added && change.Path == "Missed.cs"),
            "Inventory reconciliation must find a file omitted from update hints.");
        Assert(missed.Fallback.Attempted && missed.Repair.Status == RepairStatus.Succeeded,
            "A missed update hint must be visible as fallback and repair.");
        AssertGenerationMatchesSource(fixture, missed.Manifest.GenerationId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BeforeRename"] = "Original.cs",
                ["AddedWithoutHook"] = "Missed.cs"
            });

        File.Move(Path.Combine(fixture.WorkspacePath, "Original.cs"), Path.Combine(fixture.WorkspacePath, "Renamed.cs"));
        var renamed = service.Update(new CSharpUpdateRequest(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-update",
            Array.Empty<string>(),
            WriterWait: TimeSpan.FromSeconds(1)));
        Assert(renamed.Reconciliation.Changes.Any(change =>
                change.Kind == CodeVirtualize.Core.Lifecycle.WorkspaceChangeKind.Renamed &&
                change.PreviousPath == "Original.cs" && change.Path == "Renamed.cs"),
            "A content-preserving rename must be reported as rename.");
        AssertGenerationMatchesSource(fixture, renamed.Manifest.GenerationId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BeforeRename"] = "Renamed.cs",
                ["AddedWithoutHook"] = "Missed.cs"
            });
    }

    private static void UnchangedSyntaxFailureRemainsInCoverage()
    {
        using var fixture = new VerificationFixture("coverage-regression");
        fixture.Write("Broken.cs", "namespace Verification; public sealed class Broken { public void Missing( }");
        fixture.Write("Good.cs", "namespace Verification; public sealed class Good { public int Value => 1; }");
        var service = new CSharpLifecycleService();
        var started = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "coverage-session"));
        Assert(started.Manifest.Coverage.FailedFiles == 1, "The full build fixture must contain one failed source file.");

        fixture.Write("Good.cs", "namespace Verification; public sealed class Good { public int Value => 2; }");
        var updated = service.Update(new CSharpUpdateRequest(
            fixture.WorkspacePath,
            fixture.StorePath,
            "coverage-session",
            ["Good.cs"],
            WriterWait: TimeSpan.FromSeconds(1)));

        Assert(updated.Manifest.Coverage.FailedFiles == 1,
            "Incremental reuse must not silently drop an unchanged failed file from coverage.");
        Assert(updated.Manifest.Coverage.Limitations.Contains("syntax-errors:Broken.cs", StringComparer.Ordinal),
            "Incremental coverage must preserve the failed file limitation.");
    }

    private static void TimeoutAndCancellationNeverReturnStaleSource()
    {
        using var fixture = new VerificationFixture("timeout-cancellation");
        fixture.Write("Target.cs", "namespace Verification; public sealed class Target { public string Read() => \"old\"; }");
        var build = fixture.Build();
        var symbol = build.Symbols.Single(item => item.Name == "Read");
        fixture.Write("Target.cs", "namespace Verification; public sealed class Target { public string Read() => \"new\"; }");
        var request = new ResolveRequest(
            fixture.WorkspacePath,
            fixture.StorePath,
            symbol.SymbolId,
            SourcePart.Body,
            new SourceBudgetContract(4096, 40));

        var timeout = new BoundedResolutionFallback().Resolve(
            request,
            token =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                token.ThrowIfCancellationRequested();
                return false;
            },
            new FallbackBudget(1, TimeSpan.FromMilliseconds(50)));
        AssertBudgetFailureWithoutSource(timeout, "A timed-out repair");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = new BoundedResolutionFallback().Resolve(
            request,
            _ => throw new InvalidOperationException("A pre-cancelled repair callback must not run."),
            new FallbackBudget(1, TimeSpan.FromSeconds(1)),
            cancelled.Token);
        AssertBudgetFailureWithoutSource(cancellation, "A cancelled repair");
    }

    public static void RunFixtureVerifier()
    {
        using var fixture = new VerificationFixture("independent-json");
        const string source = "namespace Verification;\r\npublic sealed class Beacon\r\n{\r\n    public string Signal() => \"fixture-expected\";\r\n}\r\n";
        fixture.Write("Beacon.cs", source);
        var build = fixture.Build();
        var manifest = ContractJson.Deserialize<ManifestContract>(ContractJson.Serialize(build.Manifest));
        var expectedHash = Digest(Encoding.UTF8.GetBytes(source));
        var file = manifest.Files.Single(item => item.Path == "Beacon.cs");
        Assert(file.ContentHash == expectedHash, "Manifest JSON hash must match independently read fixture bytes.");

        var symbol = ContractJson.Deserialize<SymbolContract>(ContractJson.Serialize(build.Symbols.Single(item => item.Name == "Signal")));
        var declaration = symbol.Declarations.Single();
        var expectedStart = source.IndexOf("public string Signal", StringComparison.Ordinal);
        Assert(declaration.Location.Span.Start == expectedStart,
            "Symbol JSON UTF-16 start must match the independent source string position.");
        Assert(source.AsSpan(declaration.Location.Span.Start, declaration.Location.Span.Length)
                .Contains("fixture-expected".AsSpan(), StringComparison.Ordinal),
            "The independent source range selected by symbol JSON must contain the expected fixture body.");
    }

    private static void AssertGenerationMatchesSource(
        VerificationFixture fixture,
        string generationId,
        IReadOnlyDictionary<string, string> expectedTypes)
    {
        using var reader = new GenerationStore(fixture.StorePath).Open(generationId);
        var symbols = reader.ReadShardRecords(reader.Manifest.Shards.Single(item => item.Kind == "symbol").Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .Where(symbol => symbol.Kind is "class" or "record")
            .ToArray();
        foreach (var expected in expectedTypes)
        {
            var symbol = symbols.Single(item => item.Name == expected.Key);
            var declaration = symbol.Declarations.Single();
            var source = File.ReadAllText(Path.Combine(fixture.WorkspacePath, expected.Value), Encoding.UTF8);
            Assert(declaration.Location.Path == expected.Value,
                $"{expected.Key} must point at the independently expected source path.");
            Assert(declaration.Location.ContentHash == Digest(Encoding.UTF8.GetBytes(source)),
                $"{expected.Key} must carry the independently computed source digest.");
            Assert(declaration.Location.Span.Start >= 0 && declaration.Location.Span.Start + declaration.Location.Span.Length <= source.Length,
                $"{expected.Key} must carry an in-range UTF-16 declaration span.");
            Assert(source.AsSpan(declaration.Location.Span.Start, declaration.Location.Span.Length)
                    .Contains(expected.Key.AsSpan(), StringComparison.Ordinal),
                $"{expected.Key} JSON range must select its declaration from source.");
        }
    }

    private static void AssertBudgetFailureWithoutSource(ResponseContract response, string scenario)
    {
        Assert(response.Source is null, $"{scenario} must not return stale source.");
        Assert(response.Fallback.Attempted && response.Repair.Status == RepairStatus.Failed,
            $"{scenario} must report attempted fallback and failed repair.");
        Assert(response.Errors.Any(error => error.Code == FallbackErrorCodes.BudgetExceeded),
            $"{scenario} must return BUDGET_EXCEEDED.");
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class VerificationFixture : IDisposable
    {
        public VerificationFixture(string name)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"code-virtualize-task012-{name}-{Guid.NewGuid():N}");
            WorkspacePath = Path.Combine(RootPath, "workspace");
            StorePath = Path.Combine(WorkspacePath, ".code-virtualize");
            Directory.CreateDirectory(WorkspacePath);
            Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        }

        public string RootPath { get; }
        public string WorkspacePath { get; }
        public string StorePath { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(WorkspacePath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        public CSharpBuildResult Build() => new CSharpIndexBuilder().Build(new CSharpBuildRequest(WorkspacePath, StorePath));

        public void Dispose()
        {
            if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
        }
    }
}