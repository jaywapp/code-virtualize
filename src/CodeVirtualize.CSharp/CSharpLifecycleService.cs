using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Lifecycle;
using CodeVirtualize.Core.Snapshots;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.CSharp;

public sealed record CSharpSessionStartRequest(
    string WorkspacePath,
    string StorePath,
    string SessionId,
    AnalysisMode Mode = AnalysisMode.SyntaxOnly,
    string Configuration = "Debug",
    TimeSpan? WriterWait = null);

public sealed record CSharpSessionStartResult(
    ManifestContract Manifest,
    SessionSnapshotManifest Snapshot,
    bool Published);

public sealed record CSharpUpdateRequest(
    string WorkspacePath,
    string StorePath,
    string? SessionId = null,
    IReadOnlyList<string>? ChangedPathHints = null,
    AnalysisMode Mode = AnalysisMode.SyntaxOnly,
    string Configuration = "Debug",
    TimeSpan? WriterWait = null);

public sealed record CSharpUpdateResult(
    ManifestContract Manifest,
    string PreviousGenerationId,
    bool Published,
    int ReusedSymbolCount,
    int ReparsedFileCount,
    bool UsedFullRebuild,
    WorkspaceReconciliation Reconciliation,
    FallbackContract Fallback,
    RepairContract Repair);

public sealed class CSharpLifecycleService
{
    private readonly IWorkspaceTrustPolicy trustPolicy;
    private readonly SessionSnapshotStore snapshots;

    public CSharpLifecycleService(
        IWorkspaceTrustPolicy? trustPolicy = null,
        SessionSnapshotStore? snapshots = null)
    {
        this.trustPolicy = trustPolicy ?? DenyAllWorkspaceTrustPolicy.Instance;
        this.snapshots = snapshots ?? new SessionSnapshotStore();
    }

    public CSharpSessionStartResult StartSession(CSharpSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var build = new CSharpIndexBuilder(trustPolicy).Build(new CSharpBuildRequest(
            request.WorkspacePath,
            request.StorePath,
            request.Mode,
            request.Configuration,
            request.WriterWait));
        var manifest = build.Manifest;
        var snapshot = snapshots.Capture(new SessionSnapshotRequest(
            request.WorkspacePath,
            request.StorePath,
            request.SessionId,
            manifest.GenerationId,
            manifest.AnalysisKey,
            manifest.InputFingerprint,
            manifest.Files.Select(file => new SessionSnapshotSource(
                file.Path,
                file.ContentHash,
                file.Encoding,
                file.ProjectIds)).ToArray(),
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["adapterVersion"] = manifest.AdapterVersion,
                ["analysisKey"] = manifest.AnalysisKey,
                ["analysisMode"] = request.Mode == AnalysisMode.TrustedSemantic ? "trusted_semantic" : "syntax_only",
                ["configuration"] = request.Configuration,
                ["workspaceKey"] = manifest.WorkspaceKey
            }));
        return new CSharpSessionStartResult(manifest, snapshot, build.Published);
    }

    public CSharpUpdateResult Update(CSharpUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId is not null)
        {
            _ = snapshots.Open(request.StorePath, request.SessionId);
        }

        ManifestContract previous;
        IReadOnlyList<SymbolContract> previousSymbols;
        using (var reader = new GenerationStore(request.StorePath).OpenCurrent())
        {
            previous = reader.Manifest;
            var symbolShard = previous.Shards.Single(item => item.Kind == "symbol");
            previousSymbols = reader.ReadShardRecords(symbolShard.Path)
                .Select(ContractJson.Deserialize<SymbolContract>)
                .ToArray();
        }

        var hints = NormalizeHints(request.WorkspacePath, request.ChangedPathHints);
        var incremental = new CSharpIncrementalIndexBuilder(trustPolicy).Build(
            new CSharpBuildRequest(
                request.WorkspacePath,
                request.StorePath,
                request.Mode,
                request.Configuration,
                request.WriterWait),
            previous,
            previousSymbols);
        var build = incremental.Build;
        var reconciliation = WorkspaceReconciler.Compare(previous, build.Manifest, hints);
        var missedHint = reconciliation.UnreportedPaths.Count != 0;
        var fallback = missedHint
            ? new FallbackContract(true, "inventory-reconciliation-detected-unreported-changes")
            : new FallbackContract(false, null);
        var repair = reconciliation.HasChanges
            ? new RepairContract(RepairStatus.Succeeded,
                build.Published ? "immutable-generation-published" : "generation-already-current")
            : new RepairContract(RepairStatus.NotNeeded, null);
        return new CSharpUpdateResult(
            build.Manifest,
            previous.GenerationId,
            build.Published,
            incremental.ReusedSymbolCount,
            incremental.ReparsedFileCount,
            incremental.UsedFullRebuild,
            reconciliation,
            fallback,
            repair);
    }

    private static IReadOnlyList<string> NormalizeHints(string workspacePath, IReadOnlyList<string>? hints)
    {
        if (hints is null || hints.Count == 0)
        {
            return Array.Empty<string>();
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = workspace + Path.DirectorySeparatorChar;
        var result = new List<string>(hints.Count);
        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                throw new ArgumentException("Changed path hints must not be empty.");
            }

            if (!Path.IsPathRooted(hint))
            {
                result.Add(hint.Replace('\\', '/'));
                continue;
            }

            var fullPath = Path.GetFullPath(hint);
            if (!fullPath.StartsWith(prefix, comparison))
            {
                throw new ArgumentException("Changed path hint escapes the workspace.");
            }

            result.Add(Path.GetRelativePath(workspace, fullPath).Replace('\\', '/'));
        }

        return result.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }
}