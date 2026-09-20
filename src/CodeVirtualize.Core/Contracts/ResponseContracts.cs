using System.Text.Json;

namespace CodeVirtualize.Core.Contracts;

public enum ResponseStatus
{
    Ok,
    Partial,
    NotFound,
    Error
}

public enum RepairStatus
{
    NotRequested,
    NotNeeded,
    Succeeded,
    Failed
}

public sealed record FallbackContract(bool Attempted, string? Reason) : IContractValidatable
{
    public void Validate()
    {
        if (Attempted == string.IsNullOrWhiteSpace(Reason))
        {
            throw ContractGuard.Invalid(nameof(Reason), "must be present exactly when fallback was attempted");
        }
    }
}

public sealed record RepairContract(RepairStatus Status, string? Reason) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Defined(Status, nameof(Status));
        if (Status == RepairStatus.Failed && string.IsNullOrWhiteSpace(Reason))
        {
            throw ContractGuard.Invalid(nameof(Reason), "must explain a failed repair");
        }
    }
}

public sealed record ErrorContract(
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, string> Details) : IContractValidatable
{
    public void Validate()
    {
        ContractGuard.Required(Code, nameof(Code));
        ContractGuard.Required(Message, nameof(Message));
        if (Details.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null))
        {
            throw ContractGuard.Invalid(nameof(Details), "must use non-empty keys and non-null values");
        }
    }
}

public sealed record ResponseContract(
    string RequestId,
    string? GenerationId,
    ResponseStatus Status,
    FreshnessContract Freshness,
    CoverageContract Coverage,
    IReadOnlyList<JsonElement> Results,
    bool Truncated,
    string? NextCursor,
    FallbackContract Fallback,
    RepairContract Repair,
    IReadOnlyList<ErrorContract> Errors,
    SourceSliceContract? Source,
    BaselineContract? Baseline,
    string Schema = ContractSchemas.Response,
    int SchemaVersion = ContractVersions.Current) : IVersionedContract
{
    public void Validate()
    {
        ContractGuard.Version(this, ContractSchemas.Response);
        ContractGuard.Required(RequestId, nameof(RequestId));
        ContractGuard.Defined(Status, nameof(Status));
        Freshness.Validate();
        Coverage.Validate();
        Fallback.Validate();
        Repair.Validate();
        ContractGuard.NoNullItems(Errors, nameof(Errors));
        foreach (var error in Errors)
        {
            error.Validate();
        }

        Source?.Validate();
        Baseline?.Validate();

        if (Status != ResponseStatus.Error)
        {
            ContractGuard.Required(GenerationId, nameof(GenerationId));
        }

        if (Status == ResponseStatus.Error && Errors.Count == 0)
        {
            throw ContractGuard.Invalid(nameof(Errors), "must contain at least one error for error status");
        }

        if (Status is ResponseStatus.Ok or ResponseStatus.NotFound && Errors.Count != 0)
        {
            throw ContractGuard.Invalid(nameof(Errors), "must be empty for ok and not_found statuses");
        }

        if (Status == ResponseStatus.NotFound && Results.Count != 0)
        {
            throw ContractGuard.Invalid(nameof(Results), "must be empty for not_found status");
        }

        if (Truncated && Status != ResponseStatus.Partial)
        {
            throw ContractGuard.Invalid(nameof(Status), "must be partial when results are truncated");
        }

        if (!Truncated && NextCursor is not null)
        {
            throw ContractGuard.Invalid(nameof(NextCursor), "can be present only when results are truncated");
        }

        if (Truncated && !Coverage.Truncated)
        {
            throw ContractGuard.Invalid(nameof(Coverage), "must also report truncated coverage");
        }

        if (Status == ResponseStatus.Ok && Coverage.Level != CoverageLevel.CompleteWithinScope)
        {
            throw ContractGuard.Invalid(nameof(Status), "requires complete coverage within the declared scope");
        }

        if (Status == ResponseStatus.Partial && Coverage.Level == CoverageLevel.CompleteWithinScope && !Truncated)
        {
            throw ContractGuard.Invalid(nameof(Status), "requires partial coverage, errors, or truncation");
        }
    }
}
