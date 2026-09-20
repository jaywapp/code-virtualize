using System.Diagnostics;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Fallback;

public static class FallbackErrorCodes
{
    public const string BudgetExceeded = "BUDGET_EXCEEDED";
    public const string RepairFailed = "REPAIR_FAILED";
}

public sealed record FallbackBudget(int MaxRepairAttempts, TimeSpan Timeout)
{
    public void Validate()
    {
        if (MaxRepairAttempts is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRepairAttempts), "Max repair attempts must be between one and eight.");
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Fallback timeout must be between zero and one minute.");
        }
    }
}

public sealed class BoundedResolutionFallback
{
    private readonly SourceResolver resolver;

    public BoundedResolutionFallback(SourceResolver? resolver = null)
    {
        this.resolver = resolver ?? new SourceResolver();
    }

    public ResponseContract Resolve(
        ResolveRequest request,
        Func<CancellationToken, bool> repair,
        FallbackBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(repair);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();

        var initial = resolver.Resolve(request);
        if (!RequiresRepair(initial))
        {
            return initial;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget.Timeout);
        var stopwatch = Stopwatch.StartNew();
        string? failure = null;
        for (var attempt = 0; attempt < budget.MaxRepairAttempts; attempt++)
        {
            if (timeout.IsCancellationRequested || stopwatch.Elapsed >= budget.Timeout)
            {
                failure = FallbackErrorCodes.BudgetExceeded;
                break;
            }

            try
            {
                if (!repair(timeout.Token))
                {
                    failure = FallbackErrorCodes.RepairFailed;
                    continue;
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                failure = FallbackErrorCodes.BudgetExceeded;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or StorageException)
            {
                failure = FallbackErrorCodes.RepairFailed;
                continue;
            }

            if (timeout.IsCancellationRequested || stopwatch.Elapsed >= budget.Timeout)
            {
                failure = FallbackErrorCodes.BudgetExceeded;
                break;
            }

            var retried = resolver.Resolve(request);
            if (!RequiresRepair(retried))
            {
                return WithState(retried, RepairStatus.Succeeded, "source-freshness-repair");
            }

            failure = FallbackErrorCodes.RepairFailed;
        }

        var errorCode = failure ?? FallbackErrorCodes.RepairFailed;
        var message = errorCode == FallbackErrorCodes.BudgetExceeded
            ? "Bounded source repair exceeded its attempt or time budget."
            : "Bounded source repair did not produce a fresh generation.";
        var failed = initial with
        {
            Fallback = new FallbackContract(true, "source-freshness-mismatch"),
            Repair = new RepairContract(RepairStatus.Failed, errorCode.ToLowerInvariant().Replace('_', '-')),
            Errors = initial.Errors.Concat([
                new ErrorContract(errorCode, message, errorCode == FallbackErrorCodes.BudgetExceeded,
                    new Dictionary<string, string>
                    {
                        ["maxAttempts"] = budget.MaxRepairAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["timeoutMs"] = ((long)budget.Timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    })
            ]).ToArray()
        };
        failed.Validate();
        return failed;
    }

    private static bool RequiresRepair(ResponseContract response) =>
        response.Errors.Any(error => error.Code is ResolutionErrorCodes.SourceStale or ResolutionErrorCodes.FileMissing);

    private static ResponseContract WithState(ResponseContract response, RepairStatus status, string reason)
    {
        var updated = response with
        {
            Fallback = new FallbackContract(true, "source-freshness-mismatch"),
            Repair = new RepairContract(status, reason)
        };
        updated.Validate();
        return updated;
    }
}