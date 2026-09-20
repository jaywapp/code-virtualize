using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Diff;

public sealed class DiffRequestCoordinator
{
    private long epoch;

    public Task<SymbolDiffResult> CompareAsync(
        SymbolDiffRequest request,
        SymbolDiffService service,
        CancellationToken cancellationToken = default) =>
        CompareAsync(request, (current, _) => Task.FromResult(service.Compare(current)), cancellationToken);

    public async Task<SymbolDiffResult> CompareAsync(
        SymbolDiffRequest request,
        Func<SymbolDiffRequest, CancellationToken, Task<SymbolDiffResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        var requestEpoch = Interlocked.Increment(ref epoch);
        var result = await operation(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref epoch) != requestEpoch || result.Contract.Baseline.Kind != request.Baseline.Kind)
        {
            throw new DiffException(
                DiffErrorCodes.StaleResponse,
                "A newer diff request or baseline mode superseded this response.");
        }
        return result;
    }

    public void Invalidate() => Interlocked.Increment(ref epoch);
}