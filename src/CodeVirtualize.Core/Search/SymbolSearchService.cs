using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Search;

public sealed record SymbolSearchQuery(
    string? Query = null,
    string? Exact = null,
    string? QualifiedName = null,
    string? Name = null,
    string? Kind = null,
    string? Accessibility = null,
    string? ProjectId = null,
    string? Path = null,
    int Limit = 50,
    string? Cursor = null);

public sealed class SymbolSearchService
{
    public ResponseContract Search(string storePath, SymbolSearchQuery query, string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be between 1 and 1000.");
        }

        using var reader = new GenerationStore(storePath).OpenCurrent();
        var manifest = reader.Manifest;
        var fingerprint = QueryFingerprint(query);
        int offset;
        try
        {
            offset = query.Cursor is null
                ? 0
                : PageCursorCodec.Decode(query.Cursor, manifest.GenerationId, fingerprint).Offset;
        }
        catch (ContractValidationException exception)
        {
            return Error(manifest, requestId, exception.ErrorCode, exception.Message);
        }

        var symbolShard = manifest.Shards.SingleOrDefault(shard => string.Equals(shard.Kind, "symbol", StringComparison.Ordinal));
        if (symbolShard is null)
        {
            return Error(manifest, requestId, "CORRUPT_GENERATION", "The current generation has no symbol shard.");
        }

        var matches = reader.ReadShardRecords(symbolShard.Path)
            .Select(ContractJson.Deserialize<SymbolContract>)
            .Where(symbol => Matches(symbol, query))
            .OrderBy(symbol => Rank(symbol, query.Query))
            .ThenBy(symbol => symbol.ProjectId, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Declarations[0].Location.Path, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Declarations[0].Location.Span.StartLine)
            .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToArray();

        if (offset > matches.Length)
        {
            return Error(manifest, requestId, "CURSOR_INVALID", "Cursor offset is outside the result set.");
        }

        var page = matches.Skip(offset).Take(query.Limit).ToArray();
        var nextOffset = offset + page.Length;
        var truncated = nextOffset < matches.Length;
        var coverage = truncated ? TruncatedCoverage(manifest.Coverage) : manifest.Coverage;
        var status = page.Length == 0
            ? ResponseStatus.NotFound
            : coverage.Level == CoverageLevel.CompleteWithinScope ? ResponseStatus.Ok : ResponseStatus.Partial;
        if (truncated)
        {
            status = ResponseStatus.Partial;
        }

        var response = new ResponseContract(
            requestId ?? $"req_{Guid.NewGuid():N}",
            manifest.GenerationId,
            status,
            new FreshnessContract(FreshnessState.Verified, FreshnessState.Unknown),
            coverage,
            page.Select(ContractJson.ToElement).ToArray(),
            truncated,
            truncated ? PageCursorCodec.Encode(new PageCursorPayload(manifest.GenerationId, fingerprint, nextOffset)) : null,
            new FallbackContract(false, null),
            new RepairContract(RepairStatus.NotRequested, null),
            [],
            null,
            null);
        response.Validate();
        return response;
    }

    private static bool Matches(SymbolContract symbol, SymbolSearchQuery query)
    {
        if (!string.IsNullOrEmpty(query.Query) && Rank(symbol, query.Query) == int.MaxValue) return false;
        if (!string.IsNullOrEmpty(query.Exact) &&
            !string.Equals(symbol.Name, query.Exact, StringComparison.Ordinal) &&
            !string.Equals(symbol.QualifiedName, query.Exact, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(query.QualifiedName) && !string.Equals(symbol.QualifiedName, query.QualifiedName, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(query.Name) && !string.Equals(symbol.Name, query.Name, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(query.Kind) && !string.Equals(symbol.Kind, query.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(query.Accessibility) && !string.Equals(symbol.Accessibility, query.Accessibility, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(query.ProjectId) && !string.Equals(symbol.ProjectId, query.ProjectId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(query.Path) && !symbol.Declarations.Any(declaration =>
                string.Equals(declaration.Location.Path?.Replace('\\', '/'), query.Path.Replace('\\', '/'), StringComparison.Ordinal))) return false;
        return true;
    }

    private static int Rank(SymbolContract symbol, string? query)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        if (string.Equals(symbol.QualifiedName, query, StringComparison.Ordinal)) return 0;
        if (string.Equals(symbol.Name, query, StringComparison.Ordinal)) return 1;
        if (symbol.Name.StartsWith(query, StringComparison.Ordinal) || symbol.QualifiedName.StartsWith(query, StringComparison.Ordinal)) return 2;
        if (symbol.Name.Contains(query, StringComparison.Ordinal) || symbol.QualifiedName.Contains(query, StringComparison.Ordinal)) return 3;
        return int.MaxValue;
    }

    private static string QueryFingerprint(SymbolSearchQuery query)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query.Query?.Trim());
            writer.WriteString("exact", query.Exact?.Trim());
            writer.WriteString("qualifiedName", query.QualifiedName?.Trim());
            writer.WriteString("name", query.Name?.Trim());
            writer.WriteString("kind", query.Kind?.Trim().ToLowerInvariant());
            writer.WriteString("accessibility", query.Accessibility?.Trim().ToLowerInvariant());
            writer.WriteString("projectId", query.ProjectId?.Trim());
            writer.WriteString("path", query.Path?.Trim().Replace('\\', '/'));
            writer.WriteNumber("limit", query.Limit);
            writer.WriteEndObject();
        }

        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))}";
    }

    private static CoverageContract TruncatedCoverage(CoverageContract coverage) => coverage with
    {
        Level = CoverageLevel.Partial,
        Truncated = true,
        Limitations = coverage.Limitations.Append("result-limit-truncated").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    };

    private static ResponseContract Error(ManifestContract manifest, string? requestId, string code, string message)
    {
        var response = new ResponseContract(
            requestId ?? $"req_{Guid.NewGuid():N}",
            manifest.GenerationId,
            ResponseStatus.Error,
            new FreshnessContract(FreshnessState.NotApplicable, FreshnessState.Unknown),
            manifest.Coverage,
            [],
            false,
            null,
            new FallbackContract(false, null),
            new RepairContract(RepairStatus.NotRequested, null),
            [new ErrorContract(code, message, false, new Dictionary<string, string>())],
            null,
            null);
        response.Validate();
        return response;
    }
}
