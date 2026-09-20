namespace CodeVirtualize.Core.Contracts;

public enum ReferenceKind
{
    Static,
    LexicalCandidate
}

public sealed record ReferenceContract(
    string TargetSymbolId,
    string? SourceSymbolId,
    LocationContract Location,
    ReferenceKind Kind,
    string Provenance,
    string AnalysisKey,
    CoverageContract Coverage,
    IReadOnlyList<string> Limitations,
    string Schema = ContractSchemas.Reference,
    int SchemaVersion = ContractVersions.Current) : IVersionedContract
{
    public void Validate()
    {
        ContractGuard.Version(this, ContractSchemas.Reference);
        ContractGuard.SymbolId(TargetSymbolId, nameof(TargetSymbolId));
        if (SourceSymbolId is not null)
        {
            ContractGuard.SymbolId(SourceSymbolId, nameof(SourceSymbolId));
        }

        Location.Validate();
        ContractGuard.Defined(Kind, nameof(Kind));
        ContractGuard.Required(Provenance, nameof(Provenance));
        ContractGuard.Required(AnalysisKey, nameof(AnalysisKey));
        Coverage.Validate();
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));
        if (Limitations.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must contain non-empty values");
        }

        if (Kind == ReferenceKind.LexicalCandidate && Limitations.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must explain that a lexical candidate is not a confirmed static reference");
        }
    }
}
