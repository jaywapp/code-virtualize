using System.Text;

namespace CodeVirtualize.Core.Diff;

/// <summary>
/// Builds unified diff hunks that use real file line numbers (rather than symbol-relative numbering)
/// from an ordered pair of line sequences. Uses the classic Myers O(ND) algorithm to compute the edit
/// script, then groups changes into hunks separated by more than <c>2 * contextLines</c> unchanged lines,
/// mirroring conventional unified-diff hunk splitting.
/// </summary>
internal static class LineHunkBuilder
{
    internal readonly record struct SourceLine(int LineNumber, string Text);

    internal enum EditKind { Equal, Delete, Insert }

    internal readonly record struct Edit(EditKind Kind, int BaseIndex, int TargetIndex);

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
        int maxHunkBytes)
    {
        var edits = Diff(baseLines.Select(line => line.Text).ToArray(), targetLines.Select(line => line.Text).ToArray());
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

        var groups = clusters
            .Select(cluster => (Start: Math.Max(0, cluster.First - contextLines), End: Math.Min(edits.Count - 1, cluster.Last + contextLines)))
            .ToList();

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

        // The budget is too small for even a single line; emit a minimal marker rather than nothing.
        var (omittedBaseAll, omittedTargetAll) = (baseLines.Count, targetLines.Count);
        return new HunkResult(string.Empty, omittedBaseAll, omittedTargetAll, true, true);
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

    /// <summary>
    /// Classic Myers O(ND) diff (Myers 1986), following the widely reproduced trace/backtrack formulation.
    /// Returns the edit script in display order (deletes before inserts within a change run).
    /// </summary>
    internal static IReadOnlyList<Edit> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var max = n + m;
        if (max == 0)
        {
            return [];
        }

        var v = new int[2 * max + 1];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
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
                    return Backtrack(trace, n, m, max);
                }
            }
        }

        throw new InvalidOperationException("Myers diff failed to converge.");
    }

    private static IReadOnlyList<Edit> Backtrack(List<int[]> trace, int n, int m, int max)
    {
        var x = n;
        var y = m;
        var edits = new List<Edit>();
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var prevK = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? k + 1 : k - 1;
            var prevX = v[prevK + max];
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
