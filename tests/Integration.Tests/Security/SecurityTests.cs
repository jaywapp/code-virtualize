using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Integration.Tests.Security;

internal static class SecurityTests
{
    private const string Secret = "TASK012_SECRET_TOKEN_DO_NOT_LOG_7f4c";

    public static void Run()
    {
        TraversalAndRootedSourcePathsAreRejected();
        JunctionAndSymlinkEscapesAreRejectedOrExplicitlySkipped();
        ProcessArgumentsAreLiteralAndMalformedOptionsFailAsInput();
        SecretAndSourceDoNotLeakOnFailure();
        UntrustedGeneratorSentinelNeverExecutesInProcessCli();
    }

    private static void TraversalAndRootedSourcePathsAreRejected()
    {
        using var fixture = new SecurityFixture("traversal");
        var outside = Path.Combine(fixture.RootPath, "outside.cs");
        File.WriteAllText(outside, "public sealed class Outside { }", new UTF8Encoding(false));
        AssertRejectedWithoutSource(fixture.Resolve(fixture.PublishSymbol("..\\outside.cs", outside, "TraversalTarget")),
            "A traversal source path");
        AssertRejectedWithoutSource(fixture.Resolve(fixture.PublishSymbol(outside, outside, "RootedTarget")),
            "A rooted source path");
    }

    private static void JunctionAndSymlinkEscapesAreRejectedOrExplicitlySkipped()
    {
        using var fixture = new SecurityFixture("reparse");
        var outside = Path.Combine(fixture.RootPath, "outside");
        Directory.CreateDirectory(outside);
        var outsideSource = Path.Combine(outside, "Secret.cs");
        File.WriteAllText(outsideSource, $"public sealed class Escaped {{ public string Value => \"{Secret}\"; }}", new UTF8Encoding(false));

        var junction = Path.Combine(fixture.WorkspacePath, "junction");
        if (TryCreateJunction(junction, outside, out var junctionReason))
        {
            try
            {
                AssertRejectedWithoutSource(fixture.Resolve(fixture.PublishSymbol("junction/Secret.cs", outsideSource, "JunctionTarget")),
                    "A junction escape");
            }
            finally
            {
                Directory.Delete(junction);
            }
        }
        else
        {
            Console.WriteLine($"SKIP junction security fixture: {junctionReason}");
        }

        var symlink = Path.Combine(fixture.WorkspacePath, "symlink");
        if (TryCreateDirectorySymlink(symlink, outside, out var symlinkReason))
        {
            try
            {
                AssertRejectedWithoutSource(fixture.Resolve(fixture.PublishSymbol("symlink/Secret.cs", outsideSource, "SymlinkTarget")),
                    "A symlink escape");
            }
            finally
            {
                Directory.Delete(symlink);
            }
        }
        else
        {
            Console.WriteLine($"SKIP symlink security fixture: {symlinkReason}");
        }
    }

    private static void ProcessArgumentsAreLiteralAndMalformedOptionsFailAsInput()
    {
        using var fixture = new SecurityFixture("arguments & literal");
        fixture.Write("Literal.cs", "namespace SecurityFixture; public sealed class LiteralArgument { }");
        var injectedMarker = Path.Combine(fixture.RootPath, "injected.txt");
        var build = RunCli([
            "cv-build", "--workspace", fixture.WorkspacePath, "--store", fixture.StorePath,
            "--format", "json", ";", "echo", Secret, ">", injectedMarker
        ]);
        Assert(build.ExitCode == 3, "Syntax-only process build with literal metacharacter arguments must complete with partial coverage.");
        _ = ContractJson.Deserialize<ManifestContract>(build.Stdout);
        Assert(!File.Exists(injectedMarker), "CLI arguments must never be interpreted by a command shell.");
        Assert(!build.Stdout.Contains(Secret, StringComparison.Ordinal) && !build.Stderr.Contains(Secret, StringComparison.Ordinal),
            "Ignored literal arguments must not be echoed into output or diagnostics.");

        var malformed = RunCli(["cv-build", "--workspace", "--format", "json"]);
        Assert(malformed.ExitCode == 2, "An option whose value is another option must be rejected as invalid input.");
        Assert(string.IsNullOrWhiteSpace(malformed.Stdout), "Malformed CLI input must not emit a misleading JSON payload.");

        var malformedAnalyze = RunCli(["analyze", "--workspace", "--trust-workspace", fixture.WorkspacePath]);
        Assert(malformedAnalyze.ExitCode == 2 && string.IsNullOrWhiteSpace(malformedAnalyze.Stdout),
            "Malformed analyze options must use the same input-error boundary without a process crash.");
    }

    private static void SecretAndSourceDoNotLeakOnFailure()
    {
        using var fixture = new SecurityFixture("secret-log");
        fixture.Write("Secret.cs", $"namespace SecurityFixture; public sealed class SecretHolder {{ private const string Token = \"{Secret}\"; }}");
        var build = RunCli(["cv-build", "--workspace", fixture.WorkspacePath, "--store", fixture.StorePath, "--format", "json"]);
        Assert(build.ExitCode == 3, "Secret fixture build must complete in syntax-only partial mode.");
        var manifest = ContractJson.Deserialize<ManifestContract>(build.Stdout);
        fixture.Write("Secret.cs", "namespace SecurityFixture; public sealed class SecretHolder { private const string Token = \"changed\"; }");

        var validate = RunCli([
            "cv-validate", "--workspace", fixture.WorkspacePath, "--store", fixture.StorePath,
            "--generation", manifest.GenerationId, "--format", "json"
        ]);
        Assert(validate.ExitCode == 3, "Stale validation must use the explicit partial/stale exit code.");
        var response = ContractJson.Deserialize<ResponseContract>(validate.Stdout);
        Assert(response.Status == ResponseStatus.Partial && response.Errors.Any(error => error.Code == ResolutionErrorCodes.SourceStale),
            "Stale validation JSON must expose SOURCE_STALE without source content.");
        Assert(!validate.Stdout.Contains(Secret, StringComparison.Ordinal) && !validate.Stderr.Contains(Secret, StringComparison.Ordinal),
            "Failure JSON and stderr must not contain indexed secret source text.");
    }

    private static void UntrustedGeneratorSentinelNeverExecutesInProcessCli()
    {
        var workspace = Path.Combine(Directory.GetCurrentDirectory(), "tests", "Integration.Tests", "Fixtures", "UntrustedWorkspace");
        var sentinel = Path.Combine(workspace, "generator-or-build-sentinel.txt");
        if (File.Exists(sentinel)) File.Delete(sentinel);
        var store = Path.Combine(Path.GetTempPath(), $"code-virtualize-task012-sentinel-{Guid.NewGuid():N}");
        try
        {
            var result = RunCli(["cv-build", "--workspace", workspace, "--store", store, "--format", "json"]);
            Assert(result.ExitCode == 3, "Untrusted syntax-only CLI build must report partial coverage.");
            var manifest = ContractJson.Deserialize<ManifestContract>(result.Stdout);
            Assert(manifest.Projects.All(project => project.AnalysisLevel != ContractAnalysisLevel.Semantic),
                "Untrusted CLI build must not claim semantic analysis.");
            Assert(!File.Exists(sentinel), "Untrusted CLI build must not run Directory.Build.targets or a generator sentinel.");
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
            if (File.Exists(sentinel)) File.Delete(sentinel);
        }
    }

    private static CliResult RunCli(IReadOnlyList<string> arguments)
    {
        var cli = Path.Combine(Directory.GetCurrentDirectory(), "src", "CodeVirtualize.Cli", "bin", "Release", "net9.0", "CodeVirtualize.Cli.dll");
        if (!File.Exists(cli)) throw new InvalidOperationException($"Release CLI artifact is missing: {cli}");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the CLI process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("CLI process exceeded the 30 second test budget.");
        }

        return new CliResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static bool TryCreateJunction(string link, string target, out string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "Windows junctions are unavailable on this platform.";
            return false;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start junction fixture process.");
        process.WaitForExit();
        reason = process.StandardError.ReadToEnd().Trim();
        return process.ExitCode == 0;
    }

    private static bool TryCreateDirectorySymlink(string link, string target, out string reason)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            reason = exception.GetType().Name;
            return false;
        }
    }

    private static void AssertRejectedWithoutSource(ResponseContract response, string scenario)
    {
        Assert(response.Source is null, $"{scenario} must not return source bytes.");
        Assert(response.Status == ResponseStatus.Error && response.Errors.Any(error => error.Code == ResolutionErrorCodes.PathOutsideWorkspace),
            $"{scenario} must return PATH_OUTSIDE_WORKSPACE.");
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class SecurityFixture : IDisposable
    {
        public SecurityFixture(string name)
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

        public string PublishSymbol(string indexedPath, string actualPath, string name)
        {
            var bytes = File.ReadAllBytes(actualPath);
            var hash = Digest(bytes);
            var generationId = $"gen_security_{Guid.NewGuid():N}";
            var fileId = $"file_{Guid.NewGuid():N}";
            var projectId = "proj_security";
            var analysisKey = $"sha256:{new string('a', 64)}";
            var identity = new SymbolIdentityContract(projectId, analysisKey, "class", $"SecurityFixture.{name}", 0, [], null);
            var symbolId = DeterministicSymbolId.Create(identity);
            var declaration = new DeclarationContract(
                symbolId,
                new LocationContract(fileId, hash, new TextSpanContract(0, Math.Min(bytes.Length, 8), 1, 1), indexedPath, null),
                DocumentKind.Source);
            var symbol = new SymbolContract(
                symbolId, projectId, analysisKey, "class", name, $"SecurityFixture.{name}", $"class {name}", "public",
                null, 0, IdentityQuality.Syntactic, [], null, [declaration], ["security-fixture"]);
            var manifest = new ManifestContract(
                generationId,
                "workspace-security",
                analysisKey,
                DateTimeOffset.UtcNow,
                CSharpIndexBuilder.AdapterVersion,
                $"sha256:{new string('b', 64)}",
                GenerationState.Partial,
                [new ManifestFileContract(fileId, indexedPath, hash, bytes.LongLength, "utf-8", "none", [projectId])],
                [new ManifestProjectContract(projectId, "net9.0", "Debug", [], $"sha256:{new string('c', 64)}", ProjectLoadStatus.Analyzed, ContractAnalysisLevel.SyntaxOnly, ["security-fixture"])],
                [],
                new CoverageContract("security-fixture", CoverageLevel.Partial, 1, 0, 0, 0, [], ["security-fixture"], false));
            new GenerationStore(StorePath).Publish(new GenerationPublishRequest(
                manifest,
                [new GenerationShardWriteRequest("symbol", "symbols.jsonl", ContractSchemas.Symbol, [ContractJson.Serialize(symbol)])]));
            return symbolId;
        }

        public ResponseContract Resolve(string symbolId) => new SourceResolver().Resolve(new ResolveRequest(
            WorkspacePath,
            StorePath,
            symbolId,
            SourcePart.Context,
            new SourceBudgetContract(4096, 40)));

        public void Dispose()
        {
            if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
        }
    }
}