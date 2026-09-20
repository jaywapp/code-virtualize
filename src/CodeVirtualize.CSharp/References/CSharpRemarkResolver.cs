using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Remarks;
using CodeVirtualize.Core.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeVirtualize.CSharp.References;

public sealed class CSharpRemarkResolver
{
    public RemarkResponse Resolve(RemarkQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var requestId = query.RequestId ?? $"req_{Guid.NewGuid():N}";
        var store = new GenerationStore(query.StorePath);
        using var reader = query.GenerationId is null ? store.OpenCurrent() : store.Open(query.GenerationId);
        var manifest = reader.Manifest;
        var shard = manifest.Shards.SingleOrDefault(item => item.Kind == "symbol")
            ?? throw new StorageException(StorageErrorCodes.CorruptGeneration, "Symbol shard is missing.");
        var symbol = reader.ReadShardRecords(shard.Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .SingleOrDefault(item => string.Equals(item.SymbolId, query.SymbolId, StringComparison.Ordinal));
        if (symbol is null)
        {
            return CreateResponse(requestId, manifest, ResponseStatus.NotFound, FreshnessState.NotApplicable, [], []);
        }

        var verified = new List<(DeclarationContract Declaration, VerifiedSource Source)>();
        foreach (var declaration in symbol.Declarations
                     .OrderBy(item => item.Location.Path, StringComparer.Ordinal)
                     .ThenBy(item => item.Location.Span.Start))
        {
            if (declaration.DocumentKind != DocumentKind.Source || declaration.Location.Path is null)
            {
                continue;
            }

            var file = manifest.Files.SingleOrDefault(item => item.FileId == declaration.Location.FileId);
            if (file is null)
            {
                return Error(requestId, manifest, "SOURCE_STALE", "Declaration file is absent from the selected generation.", FreshnessState.Stale);
            }

            var result = VerifiedSourceReader.Read(query.WorkspacePath, file);
            if (result.Status != SourceVerificationStatus.Verified || result.Source is null)
            {
                return Error(
                    requestId,
                    manifest,
                    result.ErrorCode ?? "SOURCE_READ_FAILED",
                    result.ErrorMessage ?? "Source verification failed.",
                    result.Status == SourceVerificationStatus.Stale ? FreshnessState.Stale : FreshnessState.Unknown);
            }

            if (!string.Equals(result.Source.ContentHash, declaration.Location.ContentHash, StringComparison.Ordinal))
            {
                return Error(requestId, manifest, "SOURCE_STALE", "Declaration digest differs from current verified source.", FreshnessState.Stale);
            }

            verified.Add((declaration, result.Source));
        }

        var remarks = new List<ResolvedRemark>();
        foreach (var item in verified)
        {
            var tree = CSharpSyntaxTree.ParseText(item.Source.Text, path: item.Declaration.Location.Path!);
            var root = tree.GetRoot();
            var node = root.DescendantNodes()
                .SingleOrDefault(candidate => candidate.SpanStart == item.Declaration.Location.Span.Start &&
                                              candidate.Span.Length == item.Declaration.Location.Span.Length);
            if (node is null)
            {
                return Error(requestId, manifest, "SOURCE_STALE", "Verified source no longer contains the indexed declaration span.", FreshnessState.Stale);
            }

            foreach (var trivia in node.GetFirstToken(includeZeroWidth: true).LeadingTrivia)
            {
                var kind = Kind(trivia);
                if (kind is null)
                {
                    continue;
                }

                var span = trivia.FullSpan;
                var text = item.Source.Text.Substring(span.Start, span.Length);
                var lineSpan = tree.GetLineSpan(span).Span;
                var location = new LocationContract(
                    item.Declaration.Location.FileId,
                    item.Source.ContentHash,
                    new TextSpanContract(
                        span.Start,
                        span.Length,
                        lineSpan.Start.Line + 1,
                        lineSpan.End.Line + 1),
                    item.Declaration.Location.Path!);
                remarks.Add(new ResolvedRemark(
                    symbol.SymbolId,
                    kind.Value,
                    location,
                    Digest(text),
                    text));
            }
        }

        var ordered = remarks
            .DistinctBy(item => (item.Location.FileId, item.Location.Span.Start, item.Location.Span.Length))
            .OrderBy(item => item.Location.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Location.Span.Start)
            .ToArray();
        var status = ordered.Length == 0
            ? ResponseStatus.NotFound
            : manifest.Coverage.Level == CoverageLevel.CompleteWithinScope ? ResponseStatus.Ok : ResponseStatus.Partial;
        return CreateResponse(requestId, manifest, status, FreshnessState.Verified, ordered, []);
    }

    private static RemarkKind? Kind(SyntaxTrivia trivia)
    {
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) return RemarkKind.LineComment;
        if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) return RemarkKind.BlockComment;
        if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) return RemarkKind.XmlDocumentation;
        return null;
    }

    private static string Digest(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static RemarkResponse Error(
        string requestId,
        ManifestContract manifest,
        string code,
        string message,
        FreshnessState freshness) => CreateResponse(
            requestId,
            manifest,
            ResponseStatus.Error,
            freshness,
            [],
            [new ErrorContract(code, message, false, new Dictionary<string, string>())]);

    private static RemarkResponse CreateResponse(
        string requestId,
        ManifestContract manifest,
        ResponseStatus status,
        FreshnessState freshness,
        IReadOnlyList<ResolvedRemark> remarks,
        IReadOnlyList<ErrorContract> errors)
    {
        var coverage = status == ResponseStatus.Error
            ? Partial(manifest.Coverage, errors[0].Code.ToLowerInvariant().Replace('_', '-'))
            : manifest.Coverage;
        var response = new RemarkResponse(
            requestId,
            manifest.GenerationId,
            status,
            new FreshnessContract(freshness, FreshnessState.Unknown),
            coverage,
            remarks,
            errors);
        response.Validate();
        return response;
    }

    private static CoverageContract Partial(CoverageContract coverage, string limitation) => coverage with
    {
        Level = CoverageLevel.Partial,
        Limitations = coverage.Limitations.Append(limitation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    };
}
