using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Remarks;

public enum RemarkKind
{
    LineComment,
    BlockComment,
    XmlDocumentation
}

public sealed record RemarkQuery(
    string WorkspacePath,
    string StorePath,
    string SymbolId,
    string? GenerationId = null,
    string? RequestId = null);

public sealed record ResolvedRemark(
    string SymbolId,
    RemarkKind Kind,
    LocationContract Location,
    string RemarkHash,
    string Text) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.SymbolId(SymbolId, nameof(SymbolId));
        ContractGuard.Defined(Kind, nameof(Kind));
        Location.Validate();
        ContractGuard.Sha256(RemarkHash, nameof(RemarkHash));
        ArgumentNullException.ThrowIfNull(Text);
        if (Location.Span.Length != Text.Length)
        {
            throw ContractGuard.Invalid(nameof(Text), "must match the returned UTF-16 span length");
        }
    }
}

public sealed record RemarkResponse(
    string RequestId,
    string GenerationId,
    ResponseStatus Status,
    FreshnessContract Freshness,
    CoverageContract Coverage,
    IReadOnlyList<ResolvedRemark> Remarks,
    IReadOnlyList<ErrorContract> Errors) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(RequestId, nameof(RequestId));
        ContractGuard.Required(GenerationId, nameof(GenerationId));
        ContractGuard.Defined(Status, nameof(Status));
        Freshness.Validate();
        Coverage.Validate();
        ContractGuard.NoNullItems(Remarks, nameof(Remarks));
        ContractGuard.NoNullItems(Errors, nameof(Errors));
        foreach (var remark in Remarks) remark.Validate();
        foreach (var error in Errors) error.Validate();
        if (Status == ResponseStatus.Error && Errors.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Errors), "must explain an error response");
        }

        if (Status != ResponseStatus.Error && Errors.Count != 0)
        {
            throw ContractGuard.Invalid(nameof(Errors), "must be empty outside an error response");
        }

        if (Status == ResponseStatus.NotFound && Remarks.Count != 0)
        {
            throw ContractGuard.Invalid(nameof(Remarks), "must be empty for not_found status");
        }
    }
}
