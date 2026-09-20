using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Resolution;

public enum SourcePart { Header, Body, Context }

public static class ResolutionErrorCodes
{
    public const string AmbiguousDeclaration = "AMBIGUOUS_DECLARATION";
    public const string EncodingInvalid = "SOURCE_ENCODING_INVALID";
    public const string FileMissing = "SOURCE_FILE_MISSING";
    public const string GenerationNotCurrent = "GENERATION_NOT_CURRENT";
    public const string PathOutsideWorkspace = "PATH_OUTSIDE_WORKSPACE";
    public const string SourceStale = "SOURCE_STALE";
    public const string SpanInvalid = "SOURCE_SPAN_INVALID";
    public const string SymbolNotFound = "SYMBOL_NOT_FOUND";
    public const string UnsupportedDocument = "UNSUPPORTED_DOCUMENT";
}

public sealed record ResolveRequest(string WorkspacePath, string StorePath, string SymbolId, SourcePart Part,
    SourceBudgetContract Budget, string? GenerationId = null, string? DeclarationPath = null,
    int? DeclarationIndex = null, int ContextLines = 2, string? RequestId = null);

public sealed record ResolveResult(string SymbolId, string ProjectId, string QualifiedName, string Part,
    int DeclarationIndex, string Path, string ContentHash);

public sealed record ValidationIssue(string Path, string Code, string Message);
public sealed record ValidationResult(int TotalFiles, int VerifiedFiles, int StaleFiles, int MissingFiles,
    int InvalidFiles, IReadOnlyList<ValidationIssue> Issues);
public sealed record InspectionResult(string GenerationId, string WorkspaceKey, string AnalysisKey,
    DateTimeOffset CreatedAt, string State, int FileCount, int ProjectCount, int ShardCount,
    IReadOnlyList<ManifestProjectContract> Projects, IReadOnlyList<ManifestShardContract> Shards);
