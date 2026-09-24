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
    /// <paramref name="span"/>, excluding any line whose number falls within an excluded range. Used both
    /// for a leaf symbol's own body/signature/remark text and for a container type's self text (with its
    /// direct members' line ranges excluded).
    /// </summary>
    internal static IReadOnlyList<LineHunkBuilder.SourceLine> FullLines(
        string text, TextSpanContract span, IReadOnlyCollection<(int StartLine, int EndLine)>? excluded = null)
    {
        var starts = SourceResolver.LineStarts(text);
        var result = new List<LineHunkBuilder.SourceLine>();
        for (var line = span.StartLine; line <= span.EndLine; line++)
        {
            if (excluded is not null && excluded.Any(range => line >= range.StartLine && line <= range.EndLine))
            {
                continue;
            }

            var start = starts[line - 1];
            var end = line < starts.Count ? starts[line] : text.Length;
            result.Add(new LineHunkBuilder.SourceLine(line, StripEol(text[start..end])));
        }

        return result;
    }

    /// <summary>
    /// All logical lines of a fully decoded file, with real (1-based) line numbers, independent of any
    /// symbol's declaration span. Used by the changed-line coverage safety net (N1) to whole-file diff a
    /// changed source document and find lines that no reported entry's declaration range covers.
    /// </summary>
    internal static IReadOnlyList<LineHunkBuilder.SourceLine> AllLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var totalLines = SourceResolver.Line(text, text.Length);
        return FullLines(text, new TextSpanContract(0, text.Length, 1, totalLines), null);
    }

    /// <summary>Header-only lines: the declaration span text up to (excluding) its first '{' or '=&gt;', as raw span text (no full-line leading indentation).</summary>
    internal static IReadOnlyList<LineHunkBuilder.SourceLine> HeaderLines(string text, TextSpanContract span)
    {
        var (start, length) = SourceResolver.Range(text, span, SourcePart.Header, 0);
        var header = text.Substring(start, length);
        var result = new List<LineHunkBuilder.SourceLine>();
        var line = SourceResolver.Line(text, start);
        foreach (var segment in header.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            result.Add(new LineHunkBuilder.SourceLine(line, segment));
            line++;
        }

        return result;
    }

    internal static int LineAt(string text, int offset) => SourceResolver.Line(text, offset);

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
