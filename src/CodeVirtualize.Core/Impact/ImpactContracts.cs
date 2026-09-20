using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Impact;

public sealed record ImpactBudget(
    int MaxDepth = 1,
    int MaxResults = 200,
    int MaxSourceFiles = 500,
    int PageSize = 100) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.NonNegative(MaxDepth, nameof(MaxDepth));
        ContractGuard.Positive(MaxResults, nameof(MaxResults));
        ContractGuard.Positive(MaxSourceFiles, nameof(MaxSourceFiles));
        ContractGuard.Positive(PageSize, nameof(PageSize));
        if (PageSize > MaxResults)
        {
            throw ContractGuard.Invalid(nameof(PageSize), "must not exceed max results");
        }
    }
}

public sealed record ImpactQuery(
    string WorkspacePath,
    string StorePath,
    IReadOnlyList<string> TargetSymbolIds,
    ImpactBudget Budget,
    bool IncludeLexicalCandidates = true,
    string? GenerationId = null,
    string? Cursor = null,
    string? RequestId = null) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(WorkspacePath, nameof(WorkspacePath));
        ContractGuard.Required(StorePath, nameof(StorePath));
        ContractGuard.NoNullItems(TargetSymbolIds, nameof(TargetSymbolIds));
        if (TargetSymbolIds.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(TargetSymbolIds), "must contain at least one symbol ID");
        }

        foreach (var symbolId in TargetSymbolIds) ContractGuard.SymbolId(symbolId, nameof(TargetSymbolIds));
        Budget.Validate();
    }
}

public sealed record ImpactMatch(
    string TargetSymbolId,
    string? SourceSymbolId,
    LocationContract Location,
    ReferenceKind Kind,
    string Provenance,
    int Depth,
    IReadOnlyList<string> Limitations) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.SymbolId(TargetSymbolId, nameof(TargetSymbolId));
        if (SourceSymbolId is not null) ContractGuard.SymbolId(SourceSymbolId, nameof(SourceSymbolId));
        Location.Validate();
        ContractGuard.Defined(Kind, nameof(Kind));
        ContractGuard.Required(Provenance, nameof(Provenance));
        ContractGuard.NonNegative(Depth, nameof(Depth));
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));
        if (Limitations.Any(string.IsNullOrWhiteSpace))
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must contain non-empty values");
        }

        if (Kind == ReferenceKind.LexicalCandidate && Limitations.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Limitations), "must explain that the candidate is not a static reference");
        }
    }
}

public sealed record ImpactBudgetUsage(
    int ExpandedDepth,
    int AvailableResults,
    int ReturnedResults,
    int ScannedSourceFiles) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.NonNegative(ExpandedDepth, nameof(ExpandedDepth));
        ContractGuard.NonNegative(AvailableResults, nameof(AvailableResults));
        ContractGuard.NonNegative(ReturnedResults, nameof(ReturnedResults));
        ContractGuard.NonNegative(ScannedSourceFiles, nameof(ScannedSourceFiles));
    }
}

public sealed record ImpactResponse(
    string RequestId,
    string GenerationId,
    ResponseStatus Status,
    FreshnessContract Freshness,
    CoverageContract Coverage,
    IReadOnlyList<ImpactMatch> Results,
    bool Truncated,
    string? NextCursor,
    ImpactBudget Budget,
    ImpactBudgetUsage Usage,
    IReadOnlyList<string> ExhaustedBudgets,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<ErrorContract> Errors) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(RequestId, nameof(RequestId));
        ContractGuard.Required(GenerationId, nameof(GenerationId));
        ContractGuard.Defined(Status, nameof(Status));
        Freshness.Validate();
        Coverage.Validate();
        Budget.Validate();
        Usage.Validate();
        ContractGuard.NoNullItems(Results, nameof(Results));
        ContractGuard.NoNullItems(ExhaustedBudgets, nameof(ExhaustedBudgets));
        ContractGuard.NoNullItems(Limitations, nameof(Limitations));
        ContractGuard.NoNullItems(Errors, nameof(Errors));
        foreach (var result in Results) result.Validate();
        foreach (var error in Errors) error.Validate();
        if (Status == ResponseStatus.Error && Errors.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Errors), "must explain an error response");
        }

        if (Status != ResponseStatus.Error && Errors.Count != 0)
        {
            throw ContractGuard.Invalid(nameof(Errors), "must be empty outside an error response");
        }

        if (Status == ResponseStatus.NotFound && Results.Count != 0)
        {
            throw ContractGuard.Invalid(nameof(Results), "must be empty for not_found status");
        }

        if (Truncated != Coverage.Truncated || Truncated != (ExhaustedBudgets.Count != 0))
        {
            throw ContractGuard.Invalid(nameof(Truncated), "must match coverage and exhausted budget reporting");
        }
    }
}
