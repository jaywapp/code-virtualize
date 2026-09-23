using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Resolution;

public sealed class SourceResolver
{
    private static readonly System.Text.RegularExpressions.Regex Sha256Pattern =
        new("^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ResponseContract Resolve(ResolveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Budget.Validate();
        if (request.ContextLines < 0 || request.DeclarationIndex is < 0) throw new ArgumentException("Invalid declaration or context selector.");
        if (request.IfNoneMatch is not null && !Sha256Pattern.IsMatch(request.IfNoneMatch))
            throw new ArgumentException("ifNoneMatch must use the sha256:<64 lowercase hex characters> format.");
        var store = new GenerationStore(request.StorePath);
        using var reader = request.GenerationId is null ? store.OpenCurrent() : store.Open(request.GenerationId);
        var manifest = reader.Manifest;

        var shard = manifest.Shards.SingleOrDefault(x => x.Kind == "symbol")
            ?? throw new StorageException(StorageErrorCodes.CorruptGeneration, "Symbol shard is missing.");
        var symbol = reader.ReadShardRecords(shard.Path).Select(ContractJson.Deserialize<SymbolContract>)
            .SingleOrDefault(x => x.SymbolId == request.SymbolId);
        if (symbol is null) return NotFound(manifest, request.RequestId);

        var candidates = symbol.Declarations.Select((value, index) => (value, index))
            .Where(x => request.DeclarationIndex is null || x.index == request.DeclarationIndex)
            .Where(x => request.DeclarationPath is null || PathEqual(x.value.Location.Path, request.DeclarationPath)).ToArray();
        if (candidates.Length == 0) return NotFound(manifest, request.RequestId);
        if (candidates.Length > 1) return Error(manifest, request.RequestId, ResolutionErrorCodes.AmbiguousDeclaration,
            "Multiple declarations match; select --path or --declaration.", FreshnessState.NotApplicable);
        var selected = candidates[0];
        if (selected.value.DocumentKind != DocumentKind.Source || selected.value.Location.Path is null)
            return Error(manifest, request.RequestId, ResolutionErrorCodes.UnsupportedDocument, "Virtual documents cannot be resolved from disk.", FreshnessState.NotApplicable);

        string path;
        try { path = WorkspacePath(request.WorkspacePath, selected.value.Location.Path); }
        catch (ArgumentException e) { return Error(manifest, request.RequestId, ResolutionErrorCodes.PathOutsideWorkspace, e.Message, FreshnessState.NotApplicable); }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { return Error(manifest, request.RequestId, ResolutionErrorCodes.FileMissing, "Indexed source file is missing.", FreshnessState.Stale); }

        var hash = Digest(bytes);
        var file = manifest.Files.SingleOrDefault(x => x.FileId == selected.value.Location.FileId);
        if (file is null || hash != file.ContentHash || hash != selected.value.Location.ContentHash)
            return Error(manifest, request.RequestId, ResolutionErrorCodes.SourceStale, "Source digest differs from the indexed generation.", FreshnessState.Stale);
        string text;
        try { text = Decode(bytes, file.Encoding); }
        catch (DecoderFallbackException)
        { return Error(manifest, request.RequestId, ResolutionErrorCodes.EncodingInvalid, "Source encoding differs from the manifest.", FreshnessState.Stale); }
        var indexed = selected.value.Location.Span;
        if (!SpanValid(text, indexed)) return Error(manifest, request.RequestId, ResolutionErrorCodes.SpanInvalid,
            "Indexed UTF-16 span is invalid for verified source.", FreshnessState.Stale);

        var range = Range(text, indexed, request.Part, request.ContextLines);
        var computed = Budget(text, range.start, range.length, request.Budget);
        var source = request.IfNoneMatch is not null && string.Equals(request.IfNoneMatch, computed.ContentHash, StringComparison.Ordinal)
            ? computed with { Content = string.Empty, Usage = new SourceUsageContract(0, 0), NotModified = true }
            : computed;
        var coverage = source.Truncated ? Partial(manifest.Coverage, "source-budget-truncated", true) : manifest.Coverage;
        var status = source.Truncated || coverage.Level != CoverageLevel.CompleteWithinScope ? ResponseStatus.Partial : ResponseStatus.Ok;
        var metadata = new ResolveResult(symbol.SymbolId, symbol.ProjectId, symbol.QualifiedName,
            request.Part.ToString().ToLowerInvariant(), selected.index, selected.value.Location.Path, hash);
        var response = new ResponseContract(Id(request.RequestId), manifest.GenerationId, status,
            new FreshnessContract(FreshnessState.Verified, FreshnessState.Unknown), coverage,
            [ContractJson.ToElement(metadata)], source.Truncated, null, new(false, null),
            new(RepairStatus.NotNeeded, null), [], source, null);
        response.Validate();
        return response;
    }

    internal static string Digest(ReadOnlySpan<byte> bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    internal static string WorkspacePath(string rootPath, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Source path must be workspace-relative.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison)) throw new ArgumentException("Source path escapes workspace root.");

        EnsureNoReparsePoint(root);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) EnsureNoReparsePoint(current);
        }

        return path;
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Source path crosses a workspace reparse point.");
        }
    }

    internal static string Decode(byte[] bytes, string name)
    {
        Encoding encoding = name.ToLowerInvariant() switch
        {
            "utf-8" or "utf-8-bom" => new UTF8Encoding(false, true),
            "utf-16" or "utf-16le" => new UnicodeEncoding(false, false, true),
            "utf-16be" => new UnicodeEncoding(true, false, true),
            "utf-32" or "utf-32le" => new UTF32Encoding(false, false, true),
            "utf-32be" => new UTF32Encoding(true, false, true),
            _ => throw new DecoderFallbackException("Unsupported indexed encoding.")
        };
        var bom = name.Equals("utf-8-bom", StringComparison.OrdinalIgnoreCase) ? new byte[] { 0xEF, 0xBB, 0xBF } : encoding.GetPreamble();
        var offset = bom.Length > 0 && bytes.AsSpan().StartsWith(bom) ? bom.Length : 0;
        if (name.Equals("utf-8-bom", StringComparison.OrdinalIgnoreCase) && offset == 0) throw new DecoderFallbackException("BOM missing.");
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    internal static bool SpanValid(string text, TextSpanContract span) => span.Start >= 0 && span.Length >= 0 &&
        span.Start <= text.Length - span.Length && Line(text, span.Start) == span.StartLine && Line(text, span.Start + span.Length) == span.EndLine;

    internal static (int start, int length) Range(string text, TextSpanContract span, SourcePart part, int context)
    {
        if (part == SourcePart.Context)
        {
            var starts = LineStarts(text); var first = Math.Max(1, span.StartLine - context); var last = Math.Min(starts.Count, span.EndLine + context);
            var start = starts[first - 1]; var end = last < starts.Count ? starts[last] : text.Length; return (start, end - start);
        }
        if (part == SourcePart.Declaration)
        {
            return (span.Start, span.Length);
        }
        var declaration = text.AsSpan(span.Start, span.Length); var brace = declaration.IndexOf('{');
        var arrow = declaration.IndexOf("=>".AsSpan(), StringComparison.Ordinal);
        if (part == SourcePart.Header)
        {
            var length = brace >= 0 ? brace : arrow >= 0 ? arrow : declaration.Length;
            while (length > 0 && char.IsWhiteSpace(declaration[length - 1])) length--;
            return (span.Start, length);
        }
        if (brace >= 0)
        {
            var close = declaration.LastIndexOf('}'); var start = span.Start + brace + 1;
            var end = close > brace ? span.Start + close : span.Start + span.Length; return (start, end - start);
        }
        if (arrow >= 0)
        {
            var start = span.Start + arrow + 2; var length = span.Length - arrow - 2;
            if (length > 0 && text[start + length - 1] == ';') length--; return (start, length);
        }
        return (span.Start, span.Length);
    }

    internal static SourceSliceContract Budget(string text, int start, int length, SourceBudgetContract budget)
    {
        var full = text.Substring(start, length); var bytes = Encoding.UTF8.GetByteCount(full); var lines = Lines(full);
        if (bytes <= budget.MaxBytes && lines <= budget.MaxLines) return Slice(text, start, full, budget, false, BudgetExhaustion.None);
        var accepted = 0;
        for (var i = 0; i < full.Length;)
        {
            var width = char.IsHighSurrogate(full[i]) && i + 1 < full.Length && char.IsLowSurrogate(full[i + 1]) ? 2 : 1;
            var candidate = full[..(i + width)];
            if (Encoding.UTF8.GetByteCount(candidate) > budget.MaxBytes || Lines(candidate) > budget.MaxLines) break;
            accepted = i + width; i += width;
        }
        var exhausted = bytes > budget.MaxBytes && lines > budget.MaxLines ? BudgetExhaustion.BytesAndLines :
            bytes > budget.MaxBytes ? BudgetExhaustion.Bytes : BudgetExhaustion.Lines;
        return Slice(text, start, full[..accepted], budget, true, exhausted);
    }

    internal static SourceSliceContract Slice(string text, int start, string content, SourceBudgetContract budget, bool truncated, BudgetExhaustion exhausted) =>
        new(content, new(start, content.Length, Line(text, start), Line(text, start + content.Length)), budget,
            new(Encoding.UTF8.GetByteCount(content), Lines(content)), truncated, exhausted, Digest(Encoding.UTF8.GetBytes(content)), false);

    internal static int Lines(string text) { if (text.Length == 0) return 0; var n = 1; for (var i = 0; i < text.Length; i++)
        { if (text[i] == '\r') { n++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; } else if (text[i] == '\n') n++; } return n; }
    internal static int Line(string text, int position) { var n = 1; for (var i = 0; i < Math.Min(position, text.Length); i++)
        { if (text[i] == '\r') { n++; if (i + 1 < position && text[i + 1] == '\n') i++; } else if (text[i] == '\n') n++; } return n; }
    internal static List<int> LineStarts(string text) { var result = new List<int> { 0 }; for (var i = 0; i < text.Length; i++)
        { if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; result.Add(i + 1); } else if (text[i] == '\n') result.Add(i + 1); } return result; }
    private static bool PathEqual(string? a, string? b) => string.Equals(a?.Replace('\\', '/'), b?.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static CoverageContract Partial(CoverageContract c, string reason, bool truncated = false) => c with
        { Level = CoverageLevel.Partial, Truncated = c.Truncated || truncated, Limitations = c.Limitations.Append(reason).Distinct().Order().ToArray() };
    private static ResponseContract NotFound(ManifestContract m, string? id) { var r = new ResponseContract(Id(id), m.GenerationId, ResponseStatus.NotFound,
        new(FreshnessState.NotApplicable, FreshnessState.Unknown), m.Coverage, [], false, null, new(false, null), new(RepairStatus.NotRequested, null), [], null, null); r.Validate(); return r; }
    private static ResponseContract Error(ManifestContract m, string? id, string code, string message, FreshnessState freshness)
    { var r = new ResponseContract(Id(id), m.GenerationId, ResponseStatus.Error, new(freshness, FreshnessState.Unknown), Partial(m.Coverage, code.ToLowerInvariant().Replace('_', '-')),
        [], false, null, new(false, null), new(RepairStatus.NotRequested, null), [new(code, message, false, new Dictionary<string, string>())], null, null); r.Validate(); return r; }
    private static string Id(string? value) => value ?? $"req_{Guid.NewGuid():N}";
}
