using System.Text.RegularExpressions;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;

namespace CodeVirtualize.Core.Diff;

/// <summary>
/// Lazily resolves source for one side of a diff, reading only the immutable snapshot bytes the diff was
/// computed from (never the current workspace file). This is how added/deleted symbols (reported as
/// <c>header_only</c> evidence) and any symbol reported with truncated line-hunk evidence get their full
/// text on demand, and how DIFF-03 (a deleted symbol's base source) is served without pre-materializing it.
/// </summary>
public sealed class DiffSourceResolver
{
    private static readonly Regex Sha256Pattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    public ResponseContract Resolve(DiffSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Budget.Validate();
        if (request.ContextLines < 0 || request.DeclarationIndex is < 0)
        {
            throw new ArgumentException("Invalid declaration or context selector.");
        }

        if (request.IfNoneMatch is not null && !Sha256Pattern.IsMatch(request.IfNoneMatch))
        {
            throw new ArgumentException("ifNoneMatch must use the sha256:<64 lowercase hex characters> format.");
        }

        request.Baseline.Validate();

        var expectedSnapshotId = request.Side == DiffSide.Base ? request.Baseline.BaseId : request.Baseline.TargetId;
        if (!string.Equals(request.Snapshot.SnapshotId, expectedSnapshotId, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot,
                "The supplied snapshot does not match the requested baseline side.");
        }

        var baseFingerprint = request.Side == DiffSide.Base ? request.Snapshot.InputFingerprint : request.PeerInputFingerprint;
        var targetFingerprint = request.Side == DiffSide.Target ? request.Snapshot.InputFingerprint : request.PeerInputFingerprint;
        var recomputed = DiffDigests.SelectionKey(request.Baseline, baseFingerprint, targetFingerprint);
        if (!string.Equals(recomputed, request.SelectionKey, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.StaleResponse,
                "A newer diff request or baseline mode superseded this selection key.");
        }

        var symbol = request.Snapshot.Symbols.SingleOrDefault(item => item.SymbolId == request.SymbolId);
        if (symbol is null)
        {
            return NotFound(request);
        }

        var candidates = symbol.Declarations.Select((value, index) => (value, index))
            .Where(item => request.DeclarationIndex is null || item.index == request.DeclarationIndex)
            .ToArray();
        if (candidates.Length == 0)
        {
            return NotFound(request);
        }
        if (candidates.Length > 1)
        {
            return Error(request, ResolutionErrorCodes.AmbiguousDeclaration,
                "Multiple declarations match; select --declaration.");
        }

        var selected = candidates[0];
        if (selected.value.DocumentKind != DocumentKind.Source || selected.value.Location.Path is null)
        {
            return Error(request, ResolutionErrorCodes.UnsupportedDocument, "Virtual documents cannot be resolved from a snapshot.");
        }

        var sources = DiffSnapshotReader.DecodeSources(request.Snapshot);
        // Extract validates digest and span against the declared location (throwing DiffException on
        // mismatch) before Range/Budget operate on the whole decoded file text, the same way SourceResolver does.
        _ = DiffSnapshotReader.Extract(selected.value.Location, sources);
        var fullText = sources[DiffSnapshotReader.NormalizePath(selected.value.Location.Path)].Text;
        var span = selected.value.Location.Span;
        if (!SourceResolver.SpanValid(fullText, span))
        {
            return Error(request, ResolutionErrorCodes.SpanInvalid, "The snapshot UTF-16 span is invalid.");
        }

        var range = SourceResolver.Range(fullText, span, request.Part, request.ContextLines);
        var computed = SourceResolver.Budget(fullText, range.start, range.length, request.Budget);
        var source = request.IfNoneMatch is not null && string.Equals(request.IfNoneMatch, computed.ContentHash, StringComparison.Ordinal)
            ? computed with { Content = string.Empty, Usage = new SourceUsageContract(0, 0), NotModified = true }
            : computed;

        var coverage = source.Truncated
            ? request.Snapshot.Coverage with
            {
                Level = CoverageLevel.Partial,
                Truncated = true,
                Limitations = request.Snapshot.Coverage.Limitations.Append("source-budget-truncated").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            }
            : request.Snapshot.Coverage;
        var status = source.Truncated || coverage.Level != CoverageLevel.CompleteWithinScope ? ResponseStatus.Partial : ResponseStatus.Ok;
        var metadata = new DiffResolveResult(symbol.SymbolId, symbol.ProjectId, symbol.QualifiedName,
            request.Part.ToString().ToLowerInvariant(), selected.index, selected.value.Location.Path, selected.value.Location.ContentHash,
            request.Side, request.Snapshot.SnapshotId);
        var response = new ResponseContract(
            Id(request.RequestId), request.Snapshot.SnapshotId, status,
            new FreshnessContract(FreshnessState.Verified, FreshnessState.NotApplicable), coverage,
            [ContractJson.ToElement(metadata)], source.Truncated, null, new FallbackContract(false, null),
            new RepairContract(RepairStatus.NotNeeded, null), [], source, request.Baseline);
        response.Validate();
        return response;
    }

    private static ResponseContract NotFound(DiffSourceRequest request)
    {
        var response = new ResponseContract(
            Id(request.RequestId), request.Snapshot.SnapshotId, ResponseStatus.NotFound,
            new FreshnessContract(FreshnessState.NotApplicable, FreshnessState.NotApplicable), request.Snapshot.Coverage,
            [], false, null, new FallbackContract(false, null), new RepairContract(RepairStatus.NotRequested, null), [], null, request.Baseline);
        response.Validate();
        return response;
    }

    private static ResponseContract Error(DiffSourceRequest request, string code, string message)
    {
        var coverage = request.Snapshot.Coverage with
        {
            Level = CoverageLevel.Partial,
            Limitations = request.Snapshot.Coverage.Limitations.Append(code.ToLowerInvariant().Replace('_', '-'))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
        var response = new ResponseContract(
            Id(request.RequestId), null, ResponseStatus.Error,
            new FreshnessContract(FreshnessState.NotApplicable, FreshnessState.NotApplicable), coverage,
            [], false, null, new FallbackContract(false, null), new RepairContract(RepairStatus.NotRequested, null),
            [new ErrorContract(code, message, false, new Dictionary<string, string>())], null, request.Baseline);
        response.Validate();
        return response;
    }

    private static string Id(string? value) => value ?? $"req_{Guid.NewGuid():N}";
}
