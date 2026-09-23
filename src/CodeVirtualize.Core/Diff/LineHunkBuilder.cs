using System.Text;

namespace CodeVirtualize.Core.Diff;

/// <summary>
/// Builds unified diff hunks that use real file line numbers (rather than symbol-relative numbering)
/// from an ordered pair of line sequences. Uses the classic Myers O(ND) algorithm to compute the edit
/// script, then groups changes into hunks separated by more than <c>2 * contextLines</c> unchanged lines,
/// mirroring conventional unified-diff hunk splitting. A hunk never claims a contiguous line range across
/// a gap in the underlying <see cref="SourceLine.LineNumber"/> sequence (as happens for a container's
/// self text, where direct members' line ranges are excluded) — such a gap always forces a hunk break.
/// </summary>
internal static class LineHunkBuilder
{
    internal readonly record struct SourceLine(int LineNumber, string Text);

    internal enum EditKind { Equal, Delete, Insert }

    internal readonly record struct Edit(EditKind Kind, int BaseIndex, int TargetIndex);

    /// <summary>
    /// <paramref name="Text"/> is null in two distinct cases distinguished by <paramref name="HasChanges"/>:
    /// both false means the two sides are identical (no evidence needed); both true (Text null,
    /// HasChanges true) means a change exists but no hunk could be produced within the caller's budget or
    /// edit-distance cap (Truncated is then always true) — the caller must downgrade to fingerprint-only
    /// evidence rather than treat null as "no change".
    /// </summary>
    internal sealed record HunkResult(string? Text, int OmittedBaseLines, int OmittedTargetLines, bool Truncated, bool HasChanges);

    /// <summary>
    /// Builds a whole-symbol single-sided hunk for an added or deleted declaration header. No diff is
    /// computed; every supplied line is rendered with a uniform +/- prefix.
    /// </summary>
    internal static string BuildHeaderOnlyHunk(IReadOnlyList<SourceLine> lines, string? basePath, string? targetPath, bool added)
    {
        var builder = new StringBuilder();
        builder.Append("--- ").Append(added ? "/dev/null" : $"a/{basePath}").Append('\n');
        builder.Append("+++ ").Append(added ? $"b/{targetPath}" : "/dev/null").Append('\n');
        var anchor = lines.Count > 0 ? lines[0].LineNumber : 0;
        var count = lines.Count;
        builder.Append("@@ -").Append(added ? "0,0" : $"{anchor},{count}")
            .Append(" +").Append(added ? $"{anchor},{count}" : "0,0").Append(" @@\n");
        foreach (var line in lines)
        {
            builder.Append(added ? '+' : '-').Append(line.Text).Append('\n');
        }

        return builder.ToString();
    }

    internal static HunkResult Build(
        IReadOnlyList<SourceLine> baseLines,
        IReadOnlyList<SourceLine> targetLines,
        string? basePath,
        string? targetPath,
        int contextLines,
        int maxHunkBytes,
        int maxEditDistance)
    {
        var edits = Diff(baseLines.Select(line => line.Text).ToArray(), targetLines.Select(line => line.Text).ToArray(), maxEditDistance);
        if (edits is null)
        {
            // The edit distance exceeded the cap before an answer could be computed (or safely stored).
            // Rather than risk unbounded memory on a near-total rewrite, the caller downgrades to
            // fingerprint-only evidence; there is no meaningful line count to report as "omitted" here
            // since we never finished computing which lines correspond across the two sides.
            return new HunkResult(null, baseLines.Count, targetLines.Count, true, true);
        }

        var changeMask = edits.Select(edit => edit.Kind != EditKind.Equal).ToArray();
        var changeIndices = new List<int>();
        for (var i = 0; i < changeMask.Length; i++)
        {
            if (changeMask[i])
            {
                changeIndices.Add(i);
            }
        }

        if (changeIndices.Count == 0)
        {
            return new HunkResult(null, 0, 0, false, false);
        }

        var breaks = LineNumberBreaks(edits, baseLines, targetLines);
        var groups = new List<(int Start, int End)>();
        foreach (var (segmentStart, segmentEnd) in Segments(edits.Count, breaks))
        {
            var segmentChangeIndices = changeIndices.Where(index => index >= segmentStart && index <= segmentEnd).ToList();
            if (segmentChangeIndices.Count == 0)
            {
                continue;
            }

            foreach (var cluster in ClusterChangeIndices(segmentChangeIndices, contextLines))
            {
                groups.Add((Math.Max(segmentStart, cluster.First - contextLines), Math.Min(segmentEnd, cluster.Last + contextLines)));
            }
        }

        var truncated = false;
        while (groups.Count > 0)
        {
            var text = Render(edits, baseLines, targetLines, basePath, targetPath, groups);
            if (Encoding.UTF8.GetByteCount(text) <= maxHunkBytes)
            {
                var (omittedBase, omittedTarget) = CountOmitted(edits, groups);
                return new HunkResult(text, omittedBase, omittedTarget, truncated, true);
            }

            truncated = true;
            var lastGroup = groups[^1];
            if (lastGroup.Start >= lastGroup.End)
            {
                groups.RemoveAt(groups.Count - 1);
            }
            else
            {
                groups[^1] = (lastGroup.Start, lastGroup.End - 1);
            }
        }

        // Not even a single line (with its mandatory ---/+++/@@ headers) fits the per-entry budget — for
        // example a one-line declaration whose entire text is a single very long string literal. Report
        // that no hunk could be produced rather than emitting an empty string, which would fail contract
        // validation (line_hunks evidence requires non-empty textualHunk); the caller downgrades to
        // fingerprint-only evidence instead.
        return new HunkResult(null, baseLines.Count, targetLines.Count, true, true);
    }

    /// <summary>
    /// Indices (other than 0) where <see cref="SourceLine.LineNumber"/> stops being consecutive on
    /// whichever side(s) the edit at that index touches, relative to the most recently touched line on
    /// that side. A hunk must never span such an index, since the file lines on either side of it are not
    /// actually adjacent (this happens for a container's self text, where direct members' line ranges are
    /// excluded, leaving gaps such as lines 3, 4, 8).
    /// </summary>
    private static HashSet<int> LineNumberBreaks(
        IReadOnlyList<Edit> edits, IReadOnlyList<SourceLine> baseLines, IReadOnlyList<SourceLine> targetLines)
    {
        var breaks = new HashSet<int>();
        int? lastBase = null;
        int? lastTarget = null;
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var broke = false;
            if (edit.Kind != EditKind.Insert)
            {
                var line = baseLines[edit.BaseIndex].LineNumber;
                if (lastBase is int previous && line != previous + 1)
                {
                    broke = true;
                }
                lastBase = line;
            }

            if (edit.Kind != EditKind.Delete)
            {
                var line = targetLines[edit.TargetIndex].LineNumber;
                if (lastTarget is int previous && line != previous + 1)
                {
                    broke = true;
                }
                lastTarget = line;
            }

            if (i > 0 && broke)
            {
                breaks.Add(i);
            }
        }

        return breaks;
    }

    private static IEnumerable<(int Start, int End)> Segments(int count, IReadOnlySet<int> breaks)
    {
        var start = 0;
        for (var i = 1; i < count; i++)
        {
            if (breaks.Contains(i))
            {
                yield return (start, i - 1);
                start = i;
            }
        }

        yield return (start, count - 1);
    }

    private static List<(int First, int Last)> ClusterChangeIndices(IReadOnlyList<int> changeIndices, int contextLines)
    {
        var clusters = new List<(int First, int Last)>();
        var first = changeIndices[0];
        var last = changeIndices[0];
        for (var i = 1; i < changeIndices.Count; i++)
        {
            var index = changeIndices[i];
            if (index - last - 1 <= 2 * contextLines)
            {
                last = index;
            }
            else
            {
                clusters.Add((first, last));
                first = index;
                last = index;
            }
        }
        clusters.Add((first, last));
        return clusters;
    }

    private static (int OmittedBase, int OmittedTarget) CountOmitted(
        IReadOnlyList<Edit> edits, IReadOnlyList<(int Start, int End)> groups)
    {
        var included = new bool[edits.Count];
        foreach (var (start, end) in groups)
        {
            for (var i = start; i <= end; i++)
            {
                included[i] = true;
            }
        }

        var omittedBase = 0;
        var omittedTarget = 0;
        for (var i = 0; i < edits.Count; i++)
        {
            if (included[i])
            {
                continue;
            }

            if (edits[i].Kind != EditKind.Insert)
            {
                omittedBase++;
            }

            if (edits[i].Kind != EditKind.Delete)
            {
                omittedTarget++;
            }
        }

        return (omittedBase, omittedTarget);
    }

    private static string Render(
        IReadOnlyList<Edit> edits,
        IReadOnlyList<SourceLine> baseLines,
        IReadOnlyList<SourceLine> targetLines,
        string? basePath,
        string? targetPath,
        IReadOnlyList<(int Start, int End)> groups)
    {
        var builder = new StringBuilder();
        builder.Append("--- ").Append(basePath is null ? "/dev/null" : $"a/{basePath}").Append('\n');
        builder.Append("+++ ").Append(targetPath is null ? "/dev/null" : $"b/{targetPath}").Append('\n');
        foreach (var (start, end) in groups)
        {
            var baseCount = 0;
            var targetCount = 0;
            var baseStart = 0;
            var targetStart = 0;
            var baseFound = false;
            var targetFound = false;
            for (var i = start; i <= end; i++)
            {
                var edit = edits[i];
                if (edit.Kind != EditKind.Insert)
                {
                    baseCount++;
                    if (!baseFound)
                    {
                        baseStart = baseLines[edit.BaseIndex].LineNumber;
                        baseFound = true;
                    }
                }

                if (edit.Kind != EditKind.Delete)
                {
                    targetCount++;
                    if (!targetFound)
                    {
                        targetStart = targetLines[edit.TargetIndex].LineNumber;
                        targetFound = true;
                    }
                }
            }

            // A group with no base (or target) line at all is a pure insertion (or deletion): the
            // conventional unified-diff anchor for a zero count is the line immediately preceding the
            // change on that side, not 0 (0 is reserved for "at the very start of the file").
            if (!baseFound)
            {
                baseStart = PrecedingLine(edits, baseLines, start, EditKind.Insert, edit => edit.BaseIndex);
            }

            if (!targetFound)
            {
                targetStart = PrecedingLine(edits, targetLines, start, EditKind.Delete, edit => edit.TargetIndex);
            }

            builder.Append("@@ -").Append(baseStart).Append(',').Append(baseCount)
                .Append(" +").Append(targetStart).Append(',').Append(targetCount).Append(" @@\n");
            for (var i = start; i <= end; i++)
            {
                var edit = edits[i];
                switch (edit.Kind)
                {
                    case EditKind.Equal:
                        builder.Append(' ').Append(baseLines[edit.BaseIndex].Text).Append('\n');
                        break;
                    case EditKind.Delete:
                        builder.Append('-').Append(baseLines[edit.BaseIndex].Text).Append('\n');
                        break;
                    default:
                        builder.Append('+').Append(targetLines[edit.TargetIndex].Text).Append('\n');
                        break;
                }
            }
        }

        return builder.ToString();
    }

    private static int PrecedingLine(
        IReadOnlyList<Edit> edits, IReadOnlyList<SourceLine> lines, int beforeIndex, EditKind skip, Func<Edit, int> index)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            if (edits[i].Kind != skip)
            {
                return lines[index(edits[i])].LineNumber;
            }
        }

        return 0;
    }

    /// <summary>
    /// Hard ceiling on the total number of ints the Myers trace may retain across all "d" levels
    /// (sum of (2d+1) for d = 0..D), independent of how large the input sequences are. Without this, an
    /// input where the edit distance D approaches N+M (a near-total rewrite, not merely a large file) can
    /// make the trace grow like O(D^2) even with the compact per-level storage below — a legitimate large
    /// symbol at the MaxLinesForLineDiff cap can still reach multi-gigabyte trace memory. (D+1)^2 &lt;=
    /// this ceiling, so trace memory is bounded to roughly this many ints (~16 MB) regardless of input size.
    /// </summary>
    private const int MaxTraceInts = 4_000_000;

    /// <summary>
    /// Classic Myers O(ND) diff (Myers 1986), following the widely reproduced trace/backtrack formulation,
    /// with two bounds so a pathological or near-total-rewrite input degrades gracefully instead of
    /// exhausting memory: <paramref name="maxEditDistance"/> caps the edit distance D directly, and
    /// <see cref="MaxTraceInts"/> caps total retained trace storage regardless of D or input size. Each
    /// trace level stores only its own 2d+1 window (not the full 2*(N+M)+1 array), so for the common case
    /// where D is small relative to N+M, memory is close to O(D^2) rather than O((N+M)*D).
    /// Returns null if either bound is hit before the algorithm converges — the caller must downgrade to
    /// fingerprint-only evidence in that case. Otherwise returns the edit script in display order (deletes
    /// before inserts within a change run).
    /// </summary>
    internal static IReadOnlyList<Edit>? Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEditDistance)
    {
        var n = a.Count;
        var m = b.Count;
        var max = n + m;
        if (max == 0)
        {
            return [];
        }

        var cap = Math.Min(max, Math.Max(0, maxEditDistance));
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();
        var traceInts = 0;
        for (var d = 0; d <= max; d++)
        {
            if (d > cap)
            {
                return null;
            }

            var windowSize = 2 * d + 1;
            traceInts += windowSize;
            if (traceInts > MaxTraceInts)
            {
                return null;
            }

            var level = new int[windowSize];
            Array.Copy(v, max - d, level, 0, windowSize);
            trace.Add(level);

            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]))
                {
                    x = v[k + 1 + max];
                }
                else
                {
                    x = v[k - 1 + max] + 1;
                }

                var y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[k + max] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, n, m);
                }
            }
        }

        throw new InvalidOperationException("Myers diff failed to converge.");
    }

    private static IReadOnlyList<Edit> Backtrack(List<int[]> trace, int n, int m)
    {
        var x = n;
        var y = m;
        var edits = new List<Edit>();
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var level = trace[d];
            // prevK can legitimately land one step outside this level's own [-d, d] window (e.g. at d=0,
            // k=-d=0 short-circuits to prevK=k+1=1). That diagonal is guaranteed untouched as of this
            // snapshot (it is first written no earlier than iteration d+1), so it defaults to 0 exactly as
            // it would have in a full, non-compacted trace array.
            int Get(int k) => k < -d || k > d ? 0 : level[k + d];

            var k = x - y;
            var prevK = k == -d || (k != d && Get(k - 1) < Get(k + 1)) ? k + 1 : k - 1;
            var prevX = Get(prevK);
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                edits.Add(new Edit(EditKind.Equal, x - 1, y - 1));
                x--;
                y--;
            }

            if (d > 0)
            {
                edits.Add(x == prevX ? new Edit(EditKind.Insert, -1, y - 1) : new Edit(EditKind.Delete, x - 1, -1));
            }

            x = prevX;
            y = prevY;
        }

        edits.Reverse();
        return edits;
    }
}
