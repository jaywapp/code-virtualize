using System.Text;
using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Diff;

public sealed class SymbolDiffService
{
    public SymbolDiffResult Compare(SymbolDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Baseline.Validate();
        ValidateSnapshot(request.Base, nameof(request.Base));
        ValidateSnapshot(request.Target, nameof(request.Target));
        if (!string.Equals(request.Baseline.BaseId, request.Base.SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(request.Baseline.TargetId, request.Target.SnapshotId, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, "Baseline IDs do not match the supplied immutable snapshots.");
        }

        var baseView = CreateView(request.Base);
        var targetView = CreateView(request.Target);
        var changes = new List<SymbolDiffChange>();
        var unmatchedBase = new Dictionary<string, SymbolView>(baseView, StringComparer.Ordinal);
        var unmatchedTarget = new Dictionary<string, SymbolView>(targetView, StringComparer.Ordinal);

        foreach (var symbolId in baseView.Keys.Intersect(targetView.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = baseView[symbolId];
            var after = targetView[symbolId];
            AddMatchedChanges(changes, before, after);
            unmatchedBase.Remove(symbolId);
            unmatchedTarget.Remove(symbolId);
        }

        PairSignatureChanges(changes, unmatchedBase, unmatchedTarget);
        if (request.DetectRenameCandidates)
        {
            PairRenameCandidates(changes, unmatchedBase, unmatchedTarget);
        }

        foreach (var before in unmatchedBase.Values.OrderBy(item => SortKey(item.Symbol), StringComparer.Ordinal))
        {
            changes.Add(Change(DiffKind.Deleted, before, null, 1m, "symbol-removed"));
        }

        foreach (var after in unmatchedTarget.Values.OrderBy(item => SortKey(item.Symbol), StringComparer.Ordinal))
        {
            changes.Add(Change(DiffKind.Added, null, after, 1m, "symbol-added"));
        }

        var ordered = changes.OrderBy(change => change.Kind)
            .ThenBy(change => SortKey(change.BaseSymbol ?? change.TargetSymbol!), StringComparer.Ordinal)
            .ToArray();
        var limitations = request.Base.Coverage.Limitations.Concat(request.Target.Coverage.Limitations)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ordered.Any(change => change.Kind == DiffKind.RenameCandidate))
        {
            limitations.Add("rename-candidates-are-heuristic-and-not-confirmed-renames");
        }

        var coverage = CombineCoverage(request.Base.Coverage, request.Target.Coverage, limitations);
        var contract = new DiffContract(
            request.Baseline,
            ordered.Select(change => change.Contract).ToArray(),
            coverage,
            limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            false,
            null);
        contract.Validate();
        var selectionKey = DiffDigests.Utf8(string.Join("\n",
            request.Baseline.Kind,
            request.Baseline.Provider,
            request.Baseline.BaseId,
            request.Baseline.TargetId,
            request.Baseline.SessionId ?? string.Empty,
            request.Base.InputFingerprint,
            request.Target.InputFingerprint));
        return new SymbolDiffResult(selectionKey, contract, ordered);
    }

    private static void AddMatchedChanges(ICollection<SymbolDiffChange> changes, SymbolView before, SymbolView after)
    {
        if (!string.Equals(before.Symbol.Signature, after.Symbol.Signature, StringComparison.Ordinal))
        {
            changes.Add(Change(DiffKind.SignatureChanged, before, after, 1m, "signature-changed"));
        }
        else if (!string.Equals(before.Source, after.Source, StringComparison.Ordinal))
        {
            var kind = NormalizeWhitespace(before.Source) == NormalizeWhitespace(after.Source)
                ? DiffKind.FormattingOnly
                : DiffKind.BodyChanged;
            changes.Add(Change(kind, before, after, 1m, kind == DiffKind.FormattingOnly ? "formatting-only" : "body-changed"));
        }

        if (!string.Equals(before.Remark, after.Remark, StringComparison.Ordinal))
        {
            changes.Add(Change(DiffKind.RemarkChanged, before, after, 1m, "remark-changed", useRemark: true));
        }
    }

    private static void PairSignatureChanges(
        ICollection<SymbolDiffChange> changes,
        IDictionary<string, SymbolView> unmatchedBase,
        IDictionary<string, SymbolView> unmatchedTarget)
    {
        var baseGroups = unmatchedBase.Values.GroupBy(SignatureMatchKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var targetGroups = unmatchedTarget.Values.GroupBy(SignatureMatchKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var key in baseGroups.Keys.Intersect(targetGroups.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = baseGroups[key];
            var after = targetGroups[key];
            if (before.Length != 1 || after.Length != 1 ||
                string.Equals(before[0].Symbol.Signature, after[0].Symbol.Signature, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(Change(DiffKind.SignatureChanged, before[0], after[0], 0.9m, "signature-changed"));
            unmatchedBase.Remove(before[0].Symbol.SymbolId);
            unmatchedTarget.Remove(after[0].Symbol.SymbolId);
        }
    }

    private static void PairRenameCandidates(
        ICollection<SymbolDiffChange> changes,
        IDictionary<string, SymbolView> unmatchedBase,
        IDictionary<string, SymbolView> unmatchedTarget)
    {
        foreach (var before in unmatchedBase.Values.OrderBy(item => SortKey(item.Symbol), StringComparer.Ordinal).ToArray())
        {
            var candidates = unmatchedTarget.Values.Where(after =>
                    string.Equals(before.Symbol.ProjectId, after.Symbol.ProjectId, StringComparison.Ordinal) &&
                    string.Equals(before.Symbol.Kind, after.Symbol.Kind, StringComparison.Ordinal) &&
                    string.Equals(RenameShape(before), RenameShape(after), StringComparison.Ordinal))
                .OrderBy(after => SortKey(after.Symbol), StringComparer.Ordinal).ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var after = candidates[0];
            var reverseCount = unmatchedBase.Values.Count(candidate =>
                string.Equals(candidate.Symbol.ProjectId, after.Symbol.ProjectId, StringComparison.Ordinal) &&
                string.Equals(candidate.Symbol.Kind, after.Symbol.Kind, StringComparison.Ordinal) &&
                string.Equals(RenameShape(candidate), RenameShape(after), StringComparison.Ordinal));
            if (reverseCount != 1)
            {
                continue;
            }

            changes.Add(Change(DiffKind.RenameCandidate, before, after, 0.5m, "rename-candidate"));
            unmatchedBase.Remove(before.Symbol.SymbolId);
            unmatchedTarget.Remove(after.Symbol.SymbolId);
        }
    }

    private static SymbolDiffChange Change(
        DiffKind kind,
        SymbolView? before,
        SymbolView? after,
        decimal confidence,
        string evidenceKind,
        bool useRemark = false)
    {
        var baseText = useRemark ? before?.Remark : before?.Source;
        var targetText = useRemark ? after?.Remark : after?.Source;
        var evidence = new DiffEvidenceContract(
            evidenceKind,
            UnifiedHunk(before?.PrimaryPath, after?.PrimaryPath, baseText, targetText),
            baseText is null ? null : DiffDigests.Utf8(baseText),
            targetText is null ? null : DiffDigests.Utf8(targetText));
        var entry = new DiffEntryContract(
            kind,
            before?.Symbol.SymbolId,
            after?.Symbol.SymbolId,
            before?.Symbol.Declarations.Select(declaration => declaration.Location).ToArray() ?? [],
            after?.Symbol.Declarations.Select(declaration => declaration.Location).ToArray() ?? [],
            [evidence],
            confidence);
        entry.Validate();
        return new SymbolDiffChange(
            kind,
            before?.Symbol,
            after?.Symbol,
            before?.Source,
            after?.Source,
            before?.Remark,
            after?.Remark,
            entry);
    }

    private static IReadOnlyDictionary<string, SymbolView> CreateView(SymbolDiffSnapshot snapshot)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = snapshot.Sources.ToDictionary(source => NormalizePath(source.Path), Decode, pathComparer);
        var remarks = snapshot.Remarks.GroupBy(remark => remark.SymbolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join("\n", group.OrderBy(item => item.Location.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Location.Span.Start).Select(item => Extract(item.Location, sources))), StringComparer.Ordinal);
        return snapshot.Symbols.ToDictionary(
            symbol => symbol.SymbolId,
            symbol =>
            {
                var source = string.Join("\n// --- partial declaration ---\n", symbol.Declarations
                    .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                    .ThenBy(declaration => declaration.Location.Span.Start)
                    .Select(declaration => Extract(declaration.Location, sources)));
                return new SymbolView(
                    symbol,
                    source,
                    remarks.GetValueOrDefault(symbol.SymbolId),
                    symbol.Declarations[0].Location.Path ?? symbol.Declarations[0].Location.Uri ?? "unknown");
            },
            StringComparer.Ordinal);
    }

    private static string Extract(LocationContract location, IReadOnlyDictionary<string, DecodedSource> sources)
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

    private static DecodedSource Decode(DiffSourceDocument document)
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

    private static void ValidateSnapshot(SymbolDiffSnapshot snapshot, string name)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId) || string.IsNullOrWhiteSpace(snapshot.InputFingerprint))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"{name} identity is required.");
        }
        snapshot.Coverage.Validate();
        if (snapshot.Symbols.Select(symbol => symbol.SymbolId).Distinct(StringComparer.Ordinal).Count() != snapshot.Symbols.Count)
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"{name} contains duplicate symbol IDs.");
        }
        foreach (var symbol in snapshot.Symbols) symbol.Validate();
        foreach (var remark in snapshot.Remarks)
        {
            if (!snapshot.Symbols.Any(symbol => symbol.SymbolId == remark.SymbolId))
                throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"{name} contains a remark for an unknown symbol.");
            remark.Location.Validate();
        }
        _ = CreateView(snapshot);
    }

    private static CoverageContract CombineCoverage(CoverageContract before, CoverageContract after, IReadOnlyList<string> limitations)
    {
        var complete = before.Level == CoverageLevel.CompleteWithinScope && after.Level == CoverageLevel.CompleteWithinScope &&
                       !before.Truncated && !after.Truncated;
        return new CoverageContract(
            $"symbol-diff:{before.Scope}->{after.Scope}",
            complete ? CoverageLevel.CompleteWithinScope : CoverageLevel.Partial,
            Math.Max(before.AnalyzedFiles, after.AnalyzedFiles),
            Math.Max(before.ExcludedFiles, after.ExcludedFiles),
            before.FailedFiles + after.FailedFiles,
            before.UnknownFiles + after.UnknownFiles,
            before.FailedProjects.Concat(after.FailedProjects).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            limitations.Count == 0 && !complete ? ["baseline-or-target-coverage-is-partial"] : limitations,
            false);
    }

    private static string SignatureMatchKey(SymbolView view) =>
        $"{view.Symbol.ProjectId}\n{view.Symbol.Kind}\n{view.Symbol.QualifiedName}";

    private static string RenameShape(SymbolView view)
    {
        var signature = view.Symbol.Signature.Replace(view.Symbol.Name, "<name>", StringComparison.Ordinal);
        var source = view.Source.Replace(view.Symbol.Name, "<name>", StringComparison.Ordinal);
        return NormalizeWhitespace(signature + "\n" + source);
    }

    private static string NormalizeWhitespace(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string SortKey(SymbolContract symbol) =>
        $"{symbol.ProjectId}\n{symbol.QualifiedName}\n{symbol.Kind}\n{symbol.Signature}\n{symbol.SymbolId}";

    private static string UnifiedHunk(string? basePath, string? targetPath, string? before, string? after)
    {
        var beforeLines = Lines(before);
        var afterLines = Lines(after);
        var builder = new StringBuilder();
        builder.Append("--- ").AppendLine(basePath is null ? "/dev/null" : $"a/{basePath}");
        builder.Append("+++ ").AppendLine(targetPath is null ? "/dev/null" : $"b/{targetPath}");
        builder.Append("@@ -1,").Append(beforeLines.Length).Append(" +1,").Append(afterLines.Length).AppendLine(" @@");
        foreach (var line in beforeLines) builder.Append('-').AppendLine(line);
        foreach (var line in afterLines) builder.Append('+').AppendLine(line);
        return builder.ToString();
    }

    private static string[] Lines(string? value) => value is null
        ? []
        : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static int LineAt(string text, int offset)
    {
        var line = 1;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r')
            {
                line++;
                if (index + 1 < offset && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed record DecodedSource(DiffSourceDocument Document, string Text);
    private sealed record SymbolView(SymbolContract Symbol, string Source, string? Remark, string PrimaryPath);
}