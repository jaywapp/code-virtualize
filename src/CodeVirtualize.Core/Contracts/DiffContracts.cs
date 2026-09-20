namespace CodeVirtualize.Core.Contracts;

public enum DiffKind
{
    Added,
    Deleted,
    Renamed,
    RenameCandidate,
    SignatureChanged,
    BodyChanged,
    RemarkChanged,
    FormattingOnly
}

public sealed record DiffEvidenceContract(
    string Kind,
    string? TextualHunk,
    string? BaseContentHash,
    string? TargetContentHash) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(Kind, nameof(Kind));
        if (TextualHunk is null && BaseContentHash is null && TargetContentHash is null)
        {
            throw ContractGuard.Invalid(nameof(TextualHunk), "requires at least one evidence field");
        }

        if (BaseContentHash is not null)
        {
            ContractGuard.Sha256(BaseContentHash, nameof(BaseContentHash));
        }

        if (TargetContentHash is not null)
        {
            ContractGuard.Sha256(TargetContentHash, nameof(TargetContentHash));
        }
    }
}

public sealed record DiffEntryContract(
    DiffKind Kind,
    string? BaseSymbolId,
    string? TargetSymbolId,
    IReadOnlyList<LocationContract> BaseLocations,
    IReadOnlyList<LocationContract> TargetLocations,
    IReadOnlyList<DiffEvidenceContract> Evidence,
    decimal MatchConfidence) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Defined(Kind, nameof(Kind));
        if (BaseSymbolId is not null)
        {
            ContractGuard.SymbolId(BaseSymbolId, nameof(BaseSymbolId));
        }

        if (TargetSymbolId is not null)
        {
            ContractGuard.SymbolId(TargetSymbolId, nameof(TargetSymbolId));
        }

        if (BaseSymbolId is null && TargetSymbolId is null)
        {
            throw ContractGuard.Invalid(nameof(BaseSymbolId), "requires a base or target symbol ID");
        }

        if (Kind == DiffKind.Added && (BaseSymbolId is not null || TargetSymbolId is null))
        {
            throw ContractGuard.Invalid(nameof(Kind), "requires only a target symbol for an added entry");
        }

        if (Kind == DiffKind.Deleted && (BaseSymbolId is null || TargetSymbolId is not null))
        {
            throw ContractGuard.Invalid(nameof(Kind), "requires only a base symbol for a deleted entry");
        }

        ContractGuard.NoNullItems(BaseLocations, nameof(BaseLocations));
        ContractGuard.NoNullItems(TargetLocations, nameof(TargetLocations));
        ContractGuard.NoNullItems(Evidence, nameof(Evidence));
        foreach (var location in BaseLocations.Concat(TargetLocations))
        {
            location.Validate();
        }

        foreach (var evidence in Evidence)
        {
            evidence.Validate();
        }

        if (Evidence.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Evidence), "must contain textual or fingerprint evidence");
        }

        if (MatchConfidence is < 0 or > 1)
        {
            throw ContractGuard.Invalid(nameof(MatchConfidence), "must be between 0 and 1 inclusive");
        }
    }
}

public sealed record DiffContract(
    BaselineContract Baseline,
    IReadOnlyList<DiffEntryContract> Entries,
    CoverageContract Coverage,
    IReadOnlyList<string> Limitations,
    bool Truncated,
    string? NextCursor,
    string Schema = ContractSchemas.Diff,
    int SchemaVersion = ContractVersions.Current) : IVersionedContract
{
    public void Validate()
    {
        ContractGuard.Version(this, ContractSchemas.Diff);
        Baseline.Validate();
        ContractGuard.NoNullItems(Entries, nameof(Entries));
        foreach (var entry in Entries)
        {
            entry.Validate();
        }

        Coverage.Validate();
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));
        if (Limitations.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must contain non-empty values");
        }

        if (!Truncated && NextCursor is not null)
        {
            throw ContractGuard.Invalid(nameof(NextCursor), "can be present only for a truncated diff");
        }

        if (Truncated && !Coverage.Truncated)
        {
            throw ContractGuard.Invalid(nameof(Coverage), "must also report truncated coverage");
        }
    }
}
