using System.Security.Cryptography;
using CodeVirtualize.Core.Contracts;

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

public sealed record SymbolDiffRequest(
    BaselineContract Baseline,
    SymbolDiffSnapshot Base,
    SymbolDiffSnapshot Target,
    bool DetectRenameCandidates = true);

public sealed record SymbolDiffChange(
    DiffKind Kind,
    SymbolContract? BaseSymbol,
    SymbolContract? TargetSymbol,
    string? BaseSource,
    string? TargetSource,
    string? BaseRemark,
    string? TargetRemark,
    DiffEntryContract Contract);

public sealed record SymbolDiffResult(
    string SelectionKey,
    DiffContract Contract,
    IReadOnlyList<SymbolDiffChange> Changes);

internal static class DiffDigests
{
    public static string Bytes(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    public static string Utf8(string value) => Bytes(System.Text.Encoding.UTF8.GetBytes(value));
}