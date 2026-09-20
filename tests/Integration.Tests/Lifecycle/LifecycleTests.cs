using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Fallback;
using CodeVirtualize.Core.Lifecycle;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Snapshots;
using CodeVirtualize.Core.Storage;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Integration.Tests.Lifecycle;

internal static class LifecycleTests
{
    private const int FixedSeed = 20260920;

    public static void Run()
    {
        SessionSnapshotPreservesDirtyStartAndSessionIsolation();
        ReconciliationDetectsMissedHooksRenameAndBranchSwitch();
        SemanticChangeInvalidatesAllProjects();
        ConcurrentReaderAndUpdatePreserveFreshness();
        BoundedFallbackRepairsAStaleSource();
        FailedSessionCaptureDoesNotDamageAnotherSession();
    }

    private static void SessionSnapshotPreservesDirtyStartAndSessionIsolation()
    {
        using var fixture = new LifecycleFixture();
        var dirty = $"namespace Fixture; public class Dirty {{ public int Value => {new Random(FixedSeed).Next(10, 99)}; }}";
        fixture.Write("Dirty.cs", dirty);
        var service = new CSharpLifecycleService();
        var first = service.StartSession(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-a"));
        var second = service.StartSession(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-b"));

        File.Delete(Path.Combine(fixture.WorkspacePath, "Dirty.cs"));
        var snapshots = new SessionSnapshotStore();
        Assert(Encoding.UTF8.GetString(snapshots.ReadSource(fixture.StorePath, "session-a", "Dirty.cs")) == dirty,
            "A deleted dirty-start file must remain readable from the immutable session baseline.");
        Assert(Encoding.UTF8.GetString(snapshots.ReadSource(fixture.StorePath, "session-b", "Dirty.cs")) == dirty,
            "Each session must retain its own baseline source bytes.");
        Assert(first.Snapshot.InputFingerprint == second.Snapshot.InputFingerprint,
            "Sessions started from the same stable input must record the same input fingerprint.");

        snapshots.Close(fixture.StorePath, "session-a");
        Assert(!snapshots.IsPinned(fixture.StorePath, "session-a"), "Closing a session must release only its pin.");
        Assert(snapshots.IsPinned(fixture.StorePath, "session-b"), "Closing one session must not release another session pin.");
        Assert(Encoding.UTF8.GetString(snapshots.ReadSource(fixture.StorePath, "session-b", "Dirty.cs")) == dirty,
            "Closing another session must not delete this session's baseline.");
    }

    private static void ReconciliationDetectsMissedHooksRenameAndBranchSwitch()
    {
        using var fixture = new LifecycleFixture();
        fixture.Write("First.cs", "namespace Fixture; public class First { public int Read() => 1; }");
        fixture.Write("Second.cs", "namespace Fixture; public class Second { public int Read() => 2; }");
        fixture.Write("Stable.cs", "namespace Fixture; public class Stable { public int Read() => 4; }");
        var service = new CSharpLifecycleService();
        _ = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "session-update"));

        File.Move(Path.Combine(fixture.WorkspacePath, "First.cs"), Path.Combine(fixture.WorkspacePath, "Renamed.cs"));
        fixture.Write("Second.cs", "namespace Fixture; public class Second { public int Read() => 20; }");
        fixture.Write("Missed.cs", "namespace Fixture; public class Missed { public int Read() => 3; }");

        var update = service.Update(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-update",
            ["Second.cs"],
            WriterWait: TimeSpan.FromSeconds(1)));
        Assert(update.Reconciliation.Changes.Any(change => change.Kind == WorkspaceChangeKind.Renamed &&
                                                           change.PreviousPath == "First.cs" &&
                                                           change.Path == "Renamed.cs"),
            "Content-preserving moves must reconcile as rename.");
        Assert(update.Reconciliation.Changes.Any(change => change.Kind == WorkspaceChangeKind.Modified && change.Path == "Second.cs"),
            "Changed files must reconcile as modified.");
        Assert(update.Reconciliation.Changes.Any(change => change.Kind == WorkspaceChangeKind.Added && change.Path == "Missed.cs"),
            "Unreported files must be discovered by inventory reconciliation.");
        Assert(update.Reconciliation.UnreportedPaths.Contains("Missed.cs", StringComparer.Ordinal),
            "A missed hook path must be explicit.");
        Assert(update.Fallback.Attempted && update.Repair.Status == RepairStatus.Succeeded,
            "Missed hooks must report fallback and successful repair.");
        Assert(update.Reconciliation.InvalidatedProjects.Count != 0,
            "Changed source must invalidate its owning project.");
        Assert(!update.UsedFullRebuild && update.ReparsedFileCount == 3 && update.ReusedSymbolCount > 0,
            "Syntax-only incremental update must reparse changed/new paths and reuse unchanged symbols.");

        AssertMatchesIndependentFullBuild(fixture, update.Manifest);

        fixture.WriteProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><DefineConstants>BRANCH_SWITCH</DefineConstants></PropertyGroup></Project>");
        File.Delete(Path.Combine(fixture.WorkspacePath, "Renamed.cs"));
        File.Delete(Path.Combine(fixture.WorkspacePath, "Missed.cs"));
        fixture.Write("Branch.cs", "namespace Fixture; public class Branch { public string Name => \"next\"; }");
        var switched = service.Update(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-update",
            Array.Empty<string>(),
            WriterWait: TimeSpan.FromSeconds(1)));
        Assert(switched.Reconciliation.AnalysisConfigurationChanged,
            "Branch project configuration changes must invalidate the analysis key.");
        Assert(switched.UsedFullRebuild,
            "Semantic configuration changes must use a full rebuild instead of reusing stale binding inputs.");
        Assert(switched.Reconciliation.Changes.Any(change => change.Kind == WorkspaceChangeKind.Deleted) &&
               switched.Reconciliation.Changes.Any(change => change.Kind == WorkspaceChangeKind.Added),
            "Branch switch inventory changes must be reconciled without hook hints.");
        Assert(switched.Reconciliation.InvalidatedProjects.Select(item => item.ProjectId)
                   .Order(StringComparer.Ordinal)
                   .SequenceEqual(switched.Manifest.Projects.Select(item => item.ProjectId).Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "A semantic configuration change must conservatively invalidate every project.");
        AssertMatchesIndependentFullBuild(fixture, switched.Manifest);
    }

    private static void SemanticChangeInvalidatesAllProjects()
    {
        using var fixture = new LifecycleFixture();
        fixture.Write("Root.cs", "namespace Fixture; public class Root { public int Read() => 1; }");
        fixture.Write("Other/Other.cs", "namespace Other; public class OtherType { public int Read() => 2; }");
        fixture.Write("Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        var policy = new CodeVirtualize.Core.Analysis.ExplicitWorkspaceTrustPolicy([fixture.WorkspacePath]);
        var service = new CSharpLifecycleService(policy);
        var started = service.StartSession(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "semantic-session",
            CodeVirtualize.Core.Analysis.AnalysisMode.TrustedSemantic));
        Assert(started.Manifest.Projects.Count >= 2 &&
               started.Manifest.Projects.All(project => project.AnalysisLevel == ContractAnalysisLevel.Semantic),
            "The semantic invalidation fixture must use explicitly trusted semantic analysis.");

        fixture.Write("Root.cs", "namespace Fixture; public class Root { public long Read() => 1; }");
        var update = service.Update(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "semantic-session",
            ["Root.cs"],
            CodeVirtualize.Core.Analysis.AnalysisMode.TrustedSemantic,
            WriterWait: TimeSpan.FromSeconds(1)));
        Assert(update.UsedFullRebuild,
            "Trusted semantic source changes must not reuse syntax-only incremental results.");
        Assert(update.Reconciliation.InvalidatedProjects.Select(item => item.ProjectId)
                   .Order(StringComparer.Ordinal)
                   .SequenceEqual(update.Manifest.Projects.Select(item => item.ProjectId).Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "A semantic source change must conservatively invalidate every potential dependent project.");
    }

    private static void ConcurrentReaderAndUpdatePreserveFreshness()
    {
        using var fixture = new LifecycleFixture();
        fixture.Write("Concurrent.cs", "namespace Fixture; public class Concurrent { public string Read() => \"old\"; }");
        var service = new CSharpLifecycleService();
        var first = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "session-reader"));
        _ = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "session-writer"));

        var store = new GenerationStore(fixture.StorePath);
        using var pinnedReader = store.Open(first.Manifest.GenerationId);
        var shard = pinnedReader.Manifest.Shards.Single(item => item.Kind == "symbol");
        var oldRecords = pinnedReader.ReadShardRecords(shard.Path);
        var symbol = oldRecords.Select(ContractJson.Deserialize<SymbolContract>).Single(item => item.Name == "Read");

        fixture.Write("Concurrent.cs", "namespace Fixture; public class Concurrent { public string Read() => \"new\"; }");
        var task = Task.Run(() => service.Update(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            "session-writer",
            ["Concurrent.cs"],
            WriterWait: TimeSpan.FromSeconds(1))));
        Assert(pinnedReader.ReadShardRecords(shard.Path).SequenceEqual(oldRecords, StringComparer.Ordinal),
            "A reader lease must keep the selected immutable generation readable during update.");
        var updated = task.GetAwaiter().GetResult();
        Assert(updated.Manifest.GenerationId != first.Manifest.GenerationId,
            "The writer must publish a new generation instead of mutating the reader generation.");

        var fresh = new SourceResolver().Resolve(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            symbol.SymbolId,
            SourcePart.Body,
            new SourceBudgetContract(4096, 40),
            updated.Manifest.GenerationId));
        Assert(fresh.Source is not null && fresh.Source.Content.Contains("\"new\"", StringComparison.Ordinal),
            "The new generation must resolve current source bytes.");

        var stale = new SourceResolver().Resolve(new(
            fixture.WorkspacePath,
            fixture.StorePath,
            symbol.SymbolId,
            SourcePart.Body,
            new SourceBudgetContract(4096, 40),
            first.Manifest.GenerationId));
        Assert(stale.Source is null && stale.Errors.Any(error => error.Code == ResolutionErrorCodes.SourceStale),
            "An old pinned generation must remain readable as metadata but must never return stale workspace source.");
        var snapshots = new SessionSnapshotStore();
        Assert(snapshots.IsPinned(fixture.StorePath, "session-reader") && snapshots.IsPinned(fixture.StorePath, "session-writer"),
            "Concurrent sessions must keep independent persistent pins.");
    }

    private static void BoundedFallbackRepairsAStaleSource()
    {
        using var fixture = new LifecycleFixture();
        fixture.Write("Repair.cs", "namespace Fixture; public class Repair { public string Read() => \"before\"; }");
        var service = new CSharpLifecycleService();
        var started = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "session-repair"));
        using var reader = new GenerationStore(fixture.StorePath).OpenCurrent();
        var symbol = reader.ReadShardRecords(reader.Manifest.Shards.Single(item => item.Kind == "symbol").Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .Single(item => item.Name == "Read");

        fixture.Write("Repair.cs", "namespace Fixture; public class Repair { public string Read() => \"after\"; }");
        var result = new BoundedResolutionFallback().Resolve(
            new ResolveRequest(
                fixture.WorkspacePath,
                fixture.StorePath,
                symbol.SymbolId,
                SourcePart.Body,
                new SourceBudgetContract(4096, 40)),
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = service.Update(new(
                    fixture.WorkspacePath,
                    fixture.StorePath,
                    "session-repair",
                    Array.Empty<string>(),
                    WriterWait: TimeSpan.FromSeconds(1)));
                return true;
            },
            new FallbackBudget(1, TimeSpan.FromSeconds(3)));

        Assert(result.Source is not null && result.Source.Content.Contains("\"after\"", StringComparison.Ordinal),
            "Bounded repair must retry against the fresh generation.");
        Assert(result.Fallback.Attempted && result.Repair.Status == RepairStatus.Succeeded,
            "Fallback and repair state must be explicit after a stale-source repair.");
        Assert(result.GenerationId != started.Manifest.GenerationId,
            "Repair must publish and resolve against a new generation.");
    }

    private static void FailedSessionCaptureDoesNotDamageAnotherSession()
    {
        using var fixture = new LifecycleFixture();
        fixture.Write("Stable.cs", "namespace Fixture; public class Stable { }");
        var service = new CSharpLifecycleService();
        var stable = service.StartSession(new(fixture.WorkspacePath, fixture.StorePath, "stable-session"));
        var missingPath = Path.Combine(fixture.WorkspacePath, "Missing.cs");
        var missingHash = $"sha256:{new string('0', 64)}";
        var snapshots = new SessionSnapshotStore();
        try
        {
            snapshots.Capture(new SessionSnapshotRequest(
                fixture.WorkspacePath,
                fixture.StorePath,
                "crashed-session",
                stable.Manifest.GenerationId,
                stable.Manifest.AnalysisKey,
                stable.Manifest.InputFingerprint,
                [new SessionSnapshotSource("Missing.cs", missingHash, "utf-8", [stable.Manifest.Projects[0].ProjectId])],
                new Dictionary<string, string>(),
                1));
            throw new InvalidOperationException("Expected session capture failure.");
        }
        catch (SnapshotException exception) when (exception.ErrorCode == SnapshotErrorCodes.SourceMissing)
        {
        }

        Assert(!Directory.Exists(Path.Combine(fixture.StorePath, "sessions", "crashed-session")),
            "A failed capture must not publish a partial session directory.");
        Assert(!snapshots.IsPinned(fixture.StorePath, "crashed-session"),
            "A failed capture must not publish a session pin.");
        Assert(snapshots.IsPinned(fixture.StorePath, "stable-session"),
            "A failed session capture must not remove another session pin.");
    }

    private static void AssertMatchesIndependentFullBuild(LifecycleFixture fixture, ManifestContract incremental)
    {
        var fullStore = Path.Combine(fixture.RootPath, $"independent-full-{Guid.NewGuid():N}");
        var full = new CSharpIndexBuilder().Build(new CSharpBuildRequest(fixture.WorkspacePath, fullStore));
        var incrementalFiles = incremental.Files.Select(file => $"{file.Path}|{file.ContentHash}|{string.Join(",", file.ProjectIds.Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal).ToArray();
        var fullFiles = full.Manifest.Files.Select(file => $"{file.Path}|{file.ContentHash}|{string.Join(",", file.ProjectIds.Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert(incrementalFiles.SequenceEqual(fullFiles, StringComparer.Ordinal),
            "Incremental reconciliation and an independent full build must have identical file inventory.");
        Assert(incremental.AnalysisKey == full.Manifest.AnalysisKey &&
               incremental.InputFingerprint == full.Manifest.InputFingerprint &&
               ContractJson.ToElement(incremental.Coverage).GetRawText() ==
               ContractJson.ToElement(full.Manifest.Coverage).GetRawText() &&
               ContractJson.ToElement(incremental.Projects).GetRawText() ==
               ContractJson.ToElement(full.Manifest.Projects).GetRawText(),
            "Incremental reconciliation and an independent full build must have identical semantic inputs, coverage, and projects.");

        using var incrementalReader = new GenerationStore(fixture.StorePath).Open(incremental.GenerationId);
        using var fullReader = new GenerationStore(fullStore).OpenCurrent();
        var incrementalSymbols = incrementalReader.ReadShardRecords(incrementalReader.Manifest.Shards.Single(item => item.Kind == "symbol").Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .Select(SymbolSignature)
            .Order(StringComparer.Ordinal).ToArray();
        var fullSymbols = fullReader.ReadShardRecords(fullReader.Manifest.Shards.Single(item => item.Kind == "symbol").Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .Select(SymbolSignature)
            .Order(StringComparer.Ordinal).ToArray();
        Assert(incrementalSymbols.SequenceEqual(fullSymbols, StringComparer.Ordinal),
            "Incremental reconciliation and an independent full build must have identical symbol results.");
    }

    private static string SymbolSignature(SymbolContract symbol) =>
        $"{symbol.SymbolId}|{symbol.ProjectId}|{symbol.QualifiedName}|{symbol.Signature}|" +
        string.Join(",", symbol.Declarations.Select(declaration =>
            $"{declaration.Location.Path}:{declaration.Location.ContentHash}:{declaration.Location.Span.Start}:{declaration.Location.Span.Length}")
            .Order(StringComparer.Ordinal));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        public LifecycleFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"code-virtualize-lifecycle-{Guid.NewGuid():N}");
            WorkspacePath = Path.Combine(RootPath, "workspace");
            StorePath = Path.Combine(WorkspacePath, ".code-virtualize");
            Directory.CreateDirectory(WorkspacePath);
            WriteProject("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
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

        public void WriteProject(string content) =>
            File.WriteAllText(Path.Combine(WorkspacePath, "Fixture.csproj"), content, new UTF8Encoding(false));

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}