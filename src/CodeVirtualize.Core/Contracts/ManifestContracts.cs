namespace CodeVirtualize.Core.Contracts;

public enum GenerationState
{
    Valid,
    Partial,
    Failed
}

public enum ProjectLoadStatus
{
    Analyzed,
    Excluded,
    Failed,
    Unknown
}

public enum ContractAnalysisLevel
{
    SyntaxOnly,
    Semantic,
    Unknown
}

public sealed record ManifestFileContract(
    string FileId,
    string Path,
    string ContentHash,
    long ByteLength,
    string Encoding,
    string Newline,
    IReadOnlyList<string> ProjectIds) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(FileId, nameof(FileId));
        ContractGuard.Required(Path, nameof(Path));
        ContractGuard.Sha256(ContentHash, nameof(ContentHash));
        if (ByteLength < 0)
        {
            throw ContractGuard.Invalid(nameof(ByteLength), "must be non-negative");
        }

        ContractGuard.Required(Encoding, nameof(Encoding));
        ContractGuard.Required(Newline, nameof(Newline));
        ContractGuard.NoNullItems(ProjectIds, nameof(ProjectIds));
        if (ProjectIds.Count == 0 || ProjectIds.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(ProjectIds), "must contain at least one non-empty project ID");
        }
    }
}

public sealed record ManifestProjectContract(
    string ProjectId,
    string? TargetFramework,
    string Configuration,
    IReadOnlyList<string> Defines,
    string ReferencesFingerprint,
    ProjectLoadStatus LoadStatus,
    ContractAnalysisLevel AnalysisLevel,
    IReadOnlyList<string> Limitations) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(ProjectId, nameof(ProjectId));
        ContractGuard.Required(Configuration, nameof(Configuration));
        ContractGuard.Sha256(ReferencesFingerprint, nameof(ReferencesFingerprint));
        ContractGuard.Defined(LoadStatus, nameof(LoadStatus));
        ContractGuard.Defined(AnalysisLevel, nameof(AnalysisLevel));
        ContractGuard.NoNullItems(Defines, nameof(Defines));
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));
        if (Defines.Any(string.IsNullOrWhiteSpace) || Limitations.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must contain non-empty values");
        }

        if (LoadStatus != ProjectLoadStatus.Analyzed && Limitations.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must explain why a project was not analyzed");
        }
    }
}

public sealed record ManifestShardContract(
    string Kind,
    string Path,
    string ContentHash,
    long ByteLength,
    int RecordCount) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(Kind, nameof(Kind));
        ContractGuard.Required(Path, nameof(Path));
        ContractGuard.Sha256(ContentHash, nameof(ContentHash));
        if (ByteLength < 0)
        {
            throw ContractGuard.Invalid(nameof(ByteLength), "must be non-negative");
        }

        ContractGuard.NonNegative(RecordCount, nameof(RecordCount));
    }
}

public sealed record ManifestContract(
    string GenerationId,
    string WorkspaceKey,
    string AnalysisKey,
    DateTimeOffset CreatedAt,
    string AdapterVersion,
    string InputFingerprint,
    GenerationState State,
    IReadOnlyList<ManifestFileContract> Files,
    IReadOnlyList<ManifestProjectContract> Projects,
    IReadOnlyList<ManifestShardContract> Shards,
    CoverageContract Coverage,
    string Schema = ContractSchemas.Manifest,
    int SchemaVersion = ContractVersions.Current) : IVersionedContract
{
    public void Validate()
    {
        ContractGuard.Version(this, ContractSchemas.Manifest);
        ContractGuard.Required(GenerationId, nameof(GenerationId));
        ContractGuard.Required(WorkspaceKey, nameof(WorkspaceKey));
        ContractGuard.Required(AnalysisKey, nameof(AnalysisKey));
        ContractGuard.Required(AdapterVersion, nameof(AdapterVersion));
        ContractGuard.Sha256(InputFingerprint, nameof(InputFingerprint));
        ContractGuard.Defined(State, nameof(State));
        ContractGuard.NoNullItems(Files, nameof(Files));
        ContractGuard.NoNullItems(Projects, nameof(Projects));
        ContractGuard.NoNullItems(Shards, nameof(Shards));
        foreach (var file in Files)
        {
            file.Validate();
        }

        foreach (var project in Projects)
        {
            project.Validate();
        }

        foreach (var shard in Shards)
        {
            shard.Validate();
        }

        Coverage.Validate();
        if (State == GenerationState.Valid && Coverage.Level != CoverageLevel.CompleteWithinScope)
        {
            throw ContractGuard.Invalid(nameof(State), "can be valid only with complete coverage within the declared scope");
        }
    }
}
