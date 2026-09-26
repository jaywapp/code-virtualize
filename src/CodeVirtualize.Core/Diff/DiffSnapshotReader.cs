using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;

namespace CodeVirtualize.Core.Diff;

/// <summary>
/// Decodes and reads snapshot source bytes for both <see cref="SymbolDiffService"/> (building diff
/// evidence) and <see cref="DiffSourceResolver"/> (lazily resolving requested source on demand). Both
/// share the same digest-verification and span-extraction rules so that a lazily resolved slice can
/// never disagree with what the diff itself computed.
/// </summary>
internal static class DiffSnapshotReader
{
    internal sealed record DecodedSource(DiffSourceDocument Document, string Text);

    internal static IReadOnlyDictionary<string, DecodedSource> DecodeSources(SymbolDiffSnapshot snapshot)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return snapshot.Sources.ToDictionary(source => NormalizePath(source.Path), Decode, pathComparer);
    }

    internal static DecodedSource Decode(DiffSourceDocument document)
    {
        if (!string.Equals(DiffDigests.Bytes(document.Bytes), document.ContentHash, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.SourceStale, $"Snapshot source '{document.Path}' failed digest validation.");
        }

        try
        {
            var bytes = document.Bytes.AsSpan();
            string text;
            if (string.Equals(document.Encoding, "utf-8-bom", StringComparison.OrdinalIgnoreCase))
            {
                if (bytes.Length < 3 || !bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
                {
                    throw new DecoderFallbackException("UTF-8 BOM is missing.");
                }
                text = new UTF8Encoding(false, true).GetString(bytes[3..]);
            }
            else
            {
                var encoding = Encoding.GetEncoding(
                    document.Encoding,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                text = encoding.GetString(document.Bytes);
            }
            return new DecodedSource(document, text);
        }
        catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"Snapshot source '{document.Path}' has invalid encoding.", exception);
        }
    }

    internal static string Extract(LocationContract location, IReadOnlyDictionary<string, DecodedSource> sources)
    {
        if (location.Path is null || !sources.TryGetValue(NormalizePath(location.Path), out var source))
        {
            throw new DiffException(DiffErrorCodes.SourceMissing, $"Snapshot source '{location.Path}' is missing.");
        }

        if (!string.Equals(location.ContentHash, source.Document.ContentHash, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.SourceStale, $"Snapshot source '{location.Path}' does not match the indexed digest.");
        }

        var span = location.Span;
        if (span.Start > source.Text.Length || span.Length > source.Text.Length - span.Start)
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"UTF-16 span for '{location.Path}' is outside the validated source.");
        }

        var actualStartLine = LineAt(source.Text, span.Start);
        var actualEndLine = LineAt(source.Text, span.Start + span.Length);
        if (actualStartLine != span.StartLine || actualEndLine != span.EndLine)
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"Line range for '{location.Path}' does not match its UTF-16 span.");
        }

        return source.Text.Substring(span.Start, span.Length);
    }

    /// <summary>
    /// Returns the full source lines (with real file line numbers, including leading indentation) for
    /// <paramref name="span"/>. Used for a leaf symbol's own body/signature/remark text.
    /// </summary>
    internal static IReadOnlyList<LineHunkBuilder.SourceLine> FullLines(string text, TextSpanContract span)
    {
        var starts = SourceResolver.LineStarts(text);
        var result = new List<LineHunkBuilder.SourceLine>();
        for (var line = span.StartLine; line <= span.EndLine; line++)
        {
            var start = starts[line - 1];
            var end = line < starts.Count ? starts[line] : text.Length;
            result.Add(new LineHunkBuilder.SourceLine(line, StripEol(text[start..end])));
        }

        return result;
    }

    /// <summary>
    /// Header-only lines: the whole source lines that the declaration's header (its span up to, excluding,
    /// the first '{' or '=&gt;') touches. Whole lines, so an added or removed member's evidence also shows the
    /// text on its line outside its span (a field declarator's modifiers, type, attribute and trailing
    /// comment), which rule (b') leaves out of the container's comparison.
    /// </summary>
    internal static IReadOnlyList<LineHunkBuilder.SourceLine> HeaderLines(string text, TextSpanContract span)
    {
        var (start, length) = SourceResolver.Range(text, span, SourcePart.Header, 0);
        var lastLine = Math.Max(span.StartLine, SourceResolver.Line(text, start + Math.Max(0, length - 1)));
        return FullLines(text, new TextSpanContract(start, length, span.StartLine, lastLine));
    }

    internal static int LineAt(string text, int offset) => SourceResolver.Line(text, offset);

    internal static List<int> LineStarts(string text) => SourceResolver.LineStarts(text);

    /// <summary>End offset of <paramref name="line"/>'s content, excluding its line terminator.</summary>
    internal static int LineContentEnd(string text, IReadOnlyList<int> starts, int line)
    {
        var end = line < starts.Count ? starts[line] : text.Length;
        if (end > starts[line - 1] && text[end - 1] == '\n')
        {
            end--;
        }

        if (end > starts[line - 1] && text[end - 1] == '\r')
        {
            end--;
        }

        return end;
    }

    private static string StripEol(string line)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return line[..^2];
        }

        if (line.Length > 0 && (line[^1] == '\n' || line[^1] == '\r'))
        {
            return line[..^1];
        }

        return line;
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/');
}
