using System.Security.Cryptography;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;

namespace CodeVirtualize.Core.Diff;

public static class DiffErrorCodes
{
    public const string BaseRequired = "BASE_REQUIRED";
    public const string InvalidSnapshot = "DIFF_SNAPSHOT_INVALID";
    public const string RevisionNotFound = "VCS_REVISION_NOT_FOUND";
    public const string SessionBaseMissing = "SESSION_BASE_MISSING";
    public const string SourceMissing = "SOURCE_FILE_MISSING";
    public const string SourceStale = "SOURCE_STALE";
    public const string StaleResponse = "STALE_DIFF_RESPONSE";
    public const string VcsCommandFailed = "VCS_COMMAND_FAILED";
    public const string VcsNotRepository = "VCS_NOT_REPOSITORY";
}

public sealed class DiffException : Exception
{
    public DiffException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    public DiffException(string errorCode, string message, Exception innerException)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}

public sealed record DiffSourceDocument(
    string Path,
    byte[] Bytes,
    string Encoding,
    string ContentHash);

public sealed record DiffRemarkSnapshot(
    string SymbolId,
    LocationContract Location);

public sealed record SymbolDiffSnapshot(
    string SnapshotId,
    string InputFingerprint,
    DateTimeOffset CapturedAt,
    CoverageContract Coverage,
    IReadOnlyList<SymbolContract> Symbols,
    IReadOnlyList<DiffSourceDocument> Sources,
    IReadOnlyList<DiffRemarkSnapshot> Remarks);

public sealed record DiffEvidenceOptions(
    int ContextLines = 1,
    int MaxHunkBytesPerEntry = 8192,
    int MaxEvidenceBytesTotal = 65536,
    int MaxLinesForLineDiff = 20000)
{
    public static DiffEvidenceOptions Default { get; } = new();

    public void Validate()
    {
        if (ContextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ContextLines), "Context lines must be non-negative.");
        }

        if (MaxHunkBytesPerEntry <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHunkBytesPerEntry), "The per-entry evidence budget must be positive.");
        }

        if (MaxEvidenceBytesTotal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEvidenceBytesTotal), "The total evidence budget must be positive.");
        }

        if (MaxLinesForLineDiff <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLinesForLineDiff), "The line-diff line cap must be positive.");
        }
    }
}

public sealed record SymbolDiffRequest(
    BaselineContract Baseline,
    SymbolDiffSnapshot Base,
    SymbolDiffSnapshot Target,
    bool DetectRenameCandidates = true,
    DiffEvidenceOptions? Evidence = null);

public sealed record SymbolDiffChange(
    DiffKind Kind,
    SymbolContract? BaseSymbol,
    SymbolContract? TargetSymbol,
    DiffEntryContract Contract);

public sealed record SymbolDiffResult(
    string SelectionKey,
    DiffContract Contract,
    IReadOnlyList<SymbolDiffChange> Changes);

public enum DiffSide { Base, Target }

public sealed record DiffSourceRequest(
    string SelectionKey,
    BaselineContract Baseline,
    SymbolDiffSnapshot Snapshot,
    string PeerInputFingerprint,
    DiffSide Side,
    string SymbolId,
    SourcePart Part,
    SourceBudgetContract Budget,
    int? DeclarationIndex = null,
    int ContextLines = 0,
    string? IfNoneMatch = null,
    string? RequestId = null);

public sealed record DiffResolveResult(
    string SymbolId,
    string ProjectId,
    string QualifiedName,
    string Part,
    int DeclarationIndex,
    string Path,
    string ContentHash,
    DiffSide Side,
    string SnapshotId);

internal static class DiffDigests
{
    public static string Bytes(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    public static string Utf8(string value) => Bytes(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// The opaque key returned as <see cref="SymbolDiffResult.SelectionKey"/>, binding a diff response to
    /// its exact baseline and both immutable snapshot fingerprints. Recomputing it from the current
    /// baseline and snapshot(s) detects a mode switch or a superseded diff before any lazily resolved
    /// source is returned.
    /// </summary>
    public static string SelectionKey(BaselineContract baseline, string baseInputFingerprint, string targetInputFingerprint) =>
        Utf8(string.Join("\n",
            baseline.Kind,
            baseline.Provider,
            baseline.BaseId,
            baseline.TargetId,
            baseline.SessionId ?? string.Empty,
            baseInputFingerprint,
            targetInputFingerprint));
}