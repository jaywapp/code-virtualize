using System.Text;
using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Diff;

public sealed class SymbolDiffService
{
    public SymbolDiffResult Compare(SymbolDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Baseline.Validate();
        var options = request.Evidence ?? DiffEvidenceOptions.Default;
        options.Validate();
        var basePrepared = PrepareSnapshot(request.Base, nameof(request.Base));
        var targetPrepared = PrepareSnapshot(request.Target, nameof(request.Target));
        if (!string.Equals(request.Baseline.BaseId, request.Base.SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(request.Baseline.TargetId, request.Target.SnapshotId, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, "Baseline IDs do not match the supplied immutable snapshots.");
        }

        var baseView = basePrepared.View;
        var targetView = targetPrepared.View;
        var budget = new EvidenceBudget(options);
        var changes = new List<SymbolDiffChange>();
        var unmatchedBase = new Dictionary<string, SymbolView>(baseView, StringComparer.Ordinal);
        var unmatchedTarget = new Dictionary<string, SymbolView>(targetView, StringComparer.Ordinal);

        foreach (var symbolId in baseView.Keys.Intersect(targetView.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = baseView[symbolId];
            var after = targetView[symbolId];
            AddMatchedChanges(changes, before, after, basePrepared.MembersByContainer, targetPrepared.MembersByContainer, budget);
            unmatchedBase.Remove(symbolId);
            unmatchedTarget.Remove(symbolId);
        }

        PairSignatureChanges(changes, unmatchedBase, unmatchedTarget, budget);
        if (request.DetectRenameCandidates)
        {
            PairRenameCandidates(changes, unmatchedBase, unmatchedTarget, budget);
        }

        foreach (var before in unmatchedBase.Values.OrderBy(item => SortKey(item.Symbol), StringComparer.Ordinal))
        {
            changes.Add(BuildChange(DiffKind.Deleted, before, null, 1m, BuildAddedOrDeletedEvidence("symbol-removed", before, budget)));
        }

        foreach (var after in unmatchedTarget.Values.OrderBy(item => SortKey(item.Symbol), StringComparer.Ordinal))
        {
            changes.Add(BuildChange(DiffKind.Added, null, after, 1m, BuildAddedOrDeletedEvidence("symbol-added", after, budget)));
        }

        var ordered = changes.OrderBy(change => change.Kind)
            .ThenBy(change => SortKey(change.BaseSymbol ?? change.TargetSymbol!), StringComparer.Ordinal)
            .ToArray();
        var limitations = request.Base.Coverage.Limitations.Concat(request.Target.Coverage.Limitations)
            .Distinct(StringComparer.Ordinal).ToList();
        if (FileLevelTextChanged(request.Base, basePrepared, request.Target, targetPrepared))
        {
            limitations.Add(FileLevelTextChangedLimitation);
        }
        if (ordered.Any(change => change.Kind == DiffKind.RenameCandidate))
        {
            limitations.Add("rename-candidates-are-heuristic-and-not-confirmed-renames");
        }

        var evidenceTruncated = ordered.Any(change => change.Contract.Evidence.Any(evidence => evidence.Truncated));
        if (evidenceTruncated)
        {
            limitations.Add(DiffContract.EvidenceBudgetExhaustedLimitation);
        }

        var coverage = CombineCoverage(request.Base.Coverage, request.Target.Coverage, limitations);
        var contract = new DiffContract(
            request.Baseline,
            ordered.Select(change => change.Contract).ToArray(),
            coverage,
            limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            false,
            null,
            evidenceTruncated);
        contract.Validate();
        var selectionKey = DiffDigests.SelectionKey(request.Baseline, request.Base.InputFingerprint, request.Target.InputFingerprint);
        return new SymbolDiffResult(selectionKey, contract, ordered);
    }

    private static void AddMatchedChanges(
        List<SymbolDiffChange> changes,
        SymbolView before,
        SymbolView after,
        IReadOnlyDictionary<string, SymbolContract[]> baseMembers,
        IReadOnlyDictionary<string, SymbolContract[]> targetMembers,
        EvidenceBudget budget)
    {
        if (!string.Equals(before.Symbol.Signature, after.Symbol.Signature, StringComparison.Ordinal))
        {
            changes.Add(BuildChange(DiffKind.SignatureChanged, before, after, 1m,
                BuildSymbolLineHunkEvidence("signature-changed", before, after, budget)));
            return;
        }

        if (before.IsContainer || after.IsContainer)
        {
            // Container self text (4.4): every character of the type's declaration lines that is not inside
            // a direct member's (or any other non-ancestor symbol's) span. Whitespace-only differences and
            // differences that are only member placeholders coming and going (a member added or removed, with
            // its separator) are explained elsewhere or not at all; anything else is reported here.
            var evidences = new List<DiffEvidenceContract>();
            var pairs = PairDeclarations(before, after);
            if (pairs.Any(pair => pair.Base is null || pair.Target is null ||
                    ClassifySelfText(pair.Base, pair.Target, before, after) == SelfTextDifference.Substantive))
            {
                evidences.AddRange(BuildPairEvidence("body-changed", pairs, useSelfText: true, before, after, budget));
            }

            var baseOrder = MemberOrder(baseMembers.GetValueOrDefault(before.Symbol.SymbolId, []), pairs, useBase: true);
            var targetOrder = MemberOrder(targetMembers.GetValueOrDefault(after.Symbol.SymbolId, []), pairs, useBase: false);
            if (OrderChanged(baseOrder, targetOrder))
            {
                evidences.Add(new DiffEvidenceContract(
                    "member-order-changed", DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false,
                    DiffDigests.Utf8(before.FullText), DiffDigests.Utf8(after.FullText)));
            }

            if (evidences.Count > 0)
            {
                changes.Add(BuildChange(DiffKind.BodyChanged, before, after, 1m, evidences));
            }
        }
        else
        {
            // Change detection for a leaf (non-container) symbol compares its own declaration span text
            // (FullText), not whole source lines: a leaf's line can be shared with an unrelated neighboring
            // symbol (an enum member list, or "int a, b;" field declarators), and that neighbor's edit must
            // not make this symbol appear changed too (M2). Text on a member's lines outside its span (an
            // attribute or modifier on a field declarator's line, a trailing comment) is its container's
            // self text and is reported there.
            var spanChanged = !string.Equals(before.FullText, after.FullText, StringComparison.Ordinal);
            var spanFormattingOnly = spanChanged && NormalizeWhitespace(before.FullText) == NormalizeWhitespace(after.FullText);
            if (spanChanged && !spanFormattingOnly)
            {
                changes.Add(BuildChange(DiffKind.BodyChanged, before, after, 1m,
                    BuildSymbolLineHunkEvidence("body-changed", before, after, budget)));
            }
            else if (before.OwnsSelfText || after.OwnsSelfText)
            {
                // A top-level leaf (no indexed container, e.g. a namespace-level delegate) has no container
                // whose self text would cover the rest of its lines, so it owns that text itself.
                AddTopLevelLeafSelfTextChange(changes, before, after, spanFormattingOnly, budget);
            }
            else if (spanFormattingOnly)
            {
                changes.Add(BuildChange(DiffKind.FormattingOnly, before, after, 1m,
                    BuildSymbolLineHunkEvidence("formatting-only", before, after, budget)));
            }
        }

        if (!string.Equals(before.Remark, after.Remark, StringComparison.Ordinal))
        {
            changes.Add(BuildChange(DiffKind.RemarkChanged, before, after, 1m,
                [BuildRemarkLineHunkEvidence(before, after, budget)]));
        }
    }

    private static void PairSignatureChanges(
        ICollection<SymbolDiffChange> changes,
        IDictionary<string, SymbolView> unmatchedBase,
        IDictionary<string, SymbolView> unmatchedTarget,
        EvidenceBudget budget)
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

            changes.Add(BuildChange(DiffKind.SignatureChanged, before[0], after[0], 0.9m,
                BuildSymbolLineHunkEvidence("signature-changed", before[0], after[0], budget)));
            unmatchedBase.Remove(before[0].Symbol.SymbolId);
            unmatchedTarget.Remove(after[0].Symbol.SymbolId);
        }
    }

    private static void PairRenameCandidates(
        ICollection<SymbolDiffChange> changes,
        IDictionary<string, SymbolView> unmatchedBase,
        IDictionary<string, SymbolView> unmatchedTarget,
        EvidenceBudget budget)
    {
        // A before/after pair is a rename candidate when it is the only unmatched symbol with its
        // (project, kind, rename shape) on each side. Shapes are computed once per symbol and grouped, so
        // the pass stays linear in the number of unmatched symbols.
        static string RenameKey(SymbolView view) => $"{view.Symbol.ProjectId}\n{view.Symbol.Kind}\n{RenameShape(view)}";

        var baseKeys = unmatchedBase.Values.ToDictionary(view => view.Symbol.SymbolId, RenameKey, StringComparer.Ordinal);
        var targetByKey = unmatchedTarget.Values.GroupBy(RenameKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var baseCountByKey = baseKeys.Values.GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var before in unmatchedBase.Values.OrderBy(item => SortKey(item.Symbol), StringComparer.Ordinal).ToArray())
        {
            var key = baseKeys[before.Symbol.SymbolId];
            if (!targetByKey.TryGetValue(key, out var candidates) || candidates.Count != 1 || baseCountByKey[key] != 1)
            {
                continue;
            }

            var after = candidates[0];
            changes.Add(BuildChange(DiffKind.RenameCandidate, before, after, 0.5m,
                BuildSymbolLineHunkEvidence("rename-candidate", before, after, budget)));
            unmatchedBase.Remove(before.Symbol.SymbolId);
            unmatchedTarget.Remove(after.Symbol.SymbolId);
            candidates.Clear();
            baseCountByKey[key] = 0;
        }
    }

    private static SymbolDiffChange BuildChange(
        DiffKind kind, SymbolView? before, SymbolView? after, decimal confidence, IReadOnlyList<DiffEvidenceContract> evidence)
    {
        var entry = new DiffEntryContract(
            kind,
            before?.Symbol.SymbolId,
            after?.Symbol.SymbolId,
            before?.Symbol.Declarations.Select(declaration => declaration.Location).ToArray() ?? [],
            after?.Symbol.Declarations.Select(declaration => declaration.Location).ToArray() ?? [],
            evidence,
            confidence);
        entry.Validate();
        return new SymbolDiffChange(kind, before?.Symbol, after?.Symbol, entry);
    }

    private static void AddTopLevelLeafSelfTextChange(
        List<SymbolDiffChange> changes, SymbolView before, SymbolView after, bool spanFormattingOnly, EvidenceBudget budget)
    {
        var pairs = PairDeclarations(before, after);
        var differences = pairs
            .Select(pair => pair.Base is null || pair.Target is null
                ? SelfTextDifference.Substantive
                : ClassifySelfText(pair.Base, pair.Target, before, after))
            .ToArray();
        if (differences.Contains(SelfTextDifference.Substantive))
        {
            changes.Add(BuildChange(DiffKind.BodyChanged, before, after, 1m,
                BuildPairEvidence("body-changed", pairs, useSelfText: true, before, after, budget)));
        }
        else if (spanFormattingOnly)
        {
            changes.Add(BuildChange(DiffKind.FormattingOnly, before, after, 1m,
                BuildSymbolLineHunkEvidence("formatting-only", before, after, budget)));
        }
        else if (differences.Contains(SelfTextDifference.WhitespaceOnly))
        {
            // Whitespace outside the span (for example re-indenting the declaration's first line) is still a
            // formatting change of this symbol's own lines; report it as a fingerprint with no hunk.
            var evidences = pairs
                .Where(pair => pair.Base is not null && pair.Target is not null &&
                    ClassifySelfText(pair.Base, pair.Target, before, after) == SelfTextDifference.WhitespaceOnly)
                .Select(pair => new DiffEvidenceContract(
                    "formatting-only", DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false,
                    DiffDigests.Utf8(pair.Base!.RawText), DiffDigests.Utf8(pair.Target!.RawText)))
                .ToList();
            changes.Add(BuildChange(DiffKind.FormattingOnly, before, after, 1m, evidences));
        }
    }

    private static List<DiffEvidenceContract> BuildSymbolLineHunkEvidence(
        string kind, SymbolView before, SymbolView after, EvidenceBudget budget) =>
        BuildPairEvidence(kind, PairDeclarations(before, after), before.IsContainer || after.IsContainer, before, after, budget);

    /// <summary>
    /// Pairs a symbol's base and target declarations (M4 and moves). Declarations are paired by normalized
    /// file path first, in span order within one path, never by list index across paths: if a partial
    /// declaration's file was replaced while another stayed, index-based pairing would compare unrelated
    /// files. Declarations left unpaired by path are paired with each other only when exactly one remains
    /// on each side (the declaration moved to another file: a type extracted, a file renamed or split);
    /// otherwise they are reported as removed and added declarations.
    /// </summary>
    private static List<(DeclarationView? Base, DeclarationView? Target)> PairDeclarations(SymbolView before, SymbolView after)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var baseByPath = before.Declarations.GroupBy(declaration => DiffSnapshotReader.NormalizePath(declaration.Location.Path!), pathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), pathComparer);
        var afterByPath = after.Declarations.GroupBy(declaration => DiffSnapshotReader.NormalizePath(declaration.Location.Path!), pathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), pathComparer);
        var allPaths = baseByPath.Keys.Concat(afterByPath.Keys).Distinct(pathComparer).Order(StringComparer.Ordinal);

        var pairs = new List<(DeclarationView? Base, DeclarationView? Target)>();
        var baseLeft = new List<DeclarationView>();
        var targetLeft = new List<DeclarationView>();
        foreach (var path in allPaths)
        {
            var baseDeclarations = baseByPath.GetValueOrDefault(path, []);
            var targetDeclarations = afterByPath.GetValueOrDefault(path, []);
            var pairCount = Math.Min(baseDeclarations.Length, targetDeclarations.Length);
            for (var i = 0; i < pairCount; i++)
            {
                pairs.Add((baseDeclarations[i], targetDeclarations[i]));
            }

            baseLeft.AddRange(baseDeclarations.Skip(pairCount));
            targetLeft.AddRange(targetDeclarations.Skip(pairCount));
        }

        if (baseLeft.Count == 1 && targetLeft.Count == 1)
        {
            pairs.Add((baseLeft[0], targetLeft[0]));
        }
        else
        {
            pairs.AddRange(baseLeft.Select(declaration => ((DeclarationView?)declaration, (DeclarationView?)null)));
            pairs.AddRange(targetLeft.Select(declaration => ((DeclarationView?)null, (DeclarationView?)declaration)));
        }

        return pairs;
    }

    private static List<DiffEvidenceContract> BuildPairEvidence(
        string kind,
        IReadOnlyList<(DeclarationView? Base, DeclarationView? Target)> pairs,
        bool useSelfText,
        SymbolView before,
        SymbolView after,
        EvidenceBudget budget)
    {
        var evidences = new List<DiffEvidenceContract>();
        foreach (var (baseDeclaration, targetDeclaration) in pairs)
        {
            if (baseDeclaration is not null && targetDeclaration is not null)
            {
                var unchanged = useSelfText
                    ? ClassifySelfText(baseDeclaration, targetDeclaration, before, after) != SelfTextDifference.Substantive
                    : string.Equals(baseDeclaration.RawText, targetDeclaration.RawText, StringComparison.Ordinal);
                if (unchanged)
                {
                    // Nothing this entry is about changed in this declaration pair; another declaration of the
                    // same symbol is what triggered the entry.
                    continue;
                }

                // Self text is compared on masked lines but renders the original lines, so the evidence shows
                // the real changed line even when that line also holds member text.
                evidences.Add(BuildLineHunkFromLines(
                    kind,
                    useSelfText ? baseDeclaration.SelfLines : baseDeclaration.Lines,
                    useSelfText ? targetDeclaration.SelfLines : targetDeclaration.Lines,
                    baseDeclaration.Location.Path, targetDeclaration.Location.Path,
                    DiffDigests.Utf8(baseDeclaration.RawText), DiffDigests.Utf8(targetDeclaration.RawText), budget));
            }
            else if (baseDeclaration is not null)
            {
                evidences.Add(BuildHeaderOnlyEvidence("declaration-removed", baseDeclaration, added: false,
                    DiffDigests.Utf8(baseDeclaration.RawText), baseDeclaration.Lines.Count, budget));
            }
            else if (targetDeclaration is not null)
            {
                evidences.Add(BuildHeaderOnlyEvidence("declaration-added", targetDeclaration, added: true,
                    DiffDigests.Utf8(targetDeclaration.RawText), targetDeclaration.Lines.Count, budget));
            }
        }

        if (evidences.Count == 0)
        {
            // Defensive fallback: the callers only invoke this when a change was already detected, but if
            // per-declaration text happens to match exactly, still surface fingerprint evidence rather than
            // silently reporting an entry with no evidence at all.
            var baseHash = before.Declarations.Count > 0 ? DiffDigests.Utf8(before.FullText) : null;
            var targetHash = after.Declarations.Count > 0 ? DiffDigests.Utf8(after.FullText) : null;
            evidences.Add(new DiffEvidenceContract(kind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false, baseHash, targetHash));
        }

        return evidences;
    }

    private static DiffEvidenceContract BuildLineHunkFromLines(
        string kind,
        IReadOnlyList<LineHunkBuilder.SourceLine> baseLines,
        IReadOnlyList<LineHunkBuilder.SourceLine> targetLines,
        string? basePath,
        string? targetPath,
        string? baseHash,
        string? targetHash,
        EvidenceBudget budget)
    {
        if (budget.Exhausted ||
            baseLines.Count > budget.Options.MaxLinesForLineDiff ||
            targetLines.Count > budget.Options.MaxLinesForLineDiff)
        {
            return new DiffEvidenceContract(kind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, true, baseHash, targetHash);
        }

        var built = LineHunkBuilder.Build(
            baseLines, targetLines, basePath, targetPath, budget.Options.ContextLines, budget.Options.MaxHunkBytesPerEntry,
            budget.Options.MaxLinesForLineDiff);
        if (!built.HasChanges)
        {
            return new DiffEvidenceContract(kind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false, baseHash, targetHash);
        }

        if (built.Text is null)
        {
            // Either the per-entry byte budget could not fit even a single line (H1: e.g. a one-line
            // declaration whose entire text is one very long string literal), or the Myers edit-distance
            // cap was hit before a hunk could be computed (M1: a near-total rewrite). Either way, no hunk
            // text exists to report — downgrade to fingerprint-only evidence rather than an empty string,
            // which would fail contract validation.
            return new DiffEvidenceContract(kind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, true, baseHash, targetHash);
        }

        var bytes = Encoding.UTF8.GetByteCount(built.Text);
        if (!budget.TryConsume(bytes))
        {
            return new DiffEvidenceContract(kind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, true, baseHash, targetHash);
        }

        return new DiffEvidenceContract(
            kind, DiffEvidenceTextMode.LineHunks, built.Text, budget.Options.ContextLines,
            built.OmittedBaseLines, built.OmittedTargetLines, built.Truncated, baseHash, targetHash);
    }

    private static DiffEvidenceContract BuildHeaderOnlyEvidence(
        string kind, DeclarationView declaration, bool added, string hash, int totalLines, EvidenceBudget budget)
    {
        var baseHash = added ? null : hash;
        var targetHash = added ? hash : null;
        var text = LineHunkBuilder.BuildHeaderOnlyHunk(
            declaration.HeaderLines, added ? null : declaration.Location.Path, added ? declaration.Location.Path : null, added);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (!budget.TryConsume(bytes))
        {
            return new DiffEvidenceContract(kind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, true, baseHash, targetHash);
        }

        var omitted = Math.Max(0, totalLines - declaration.HeaderLines.Count);
        return new DiffEvidenceContract(
            kind, DiffEvidenceTextMode.HeaderOnly, text, 0,
            added ? 0 : omitted, added ? omitted : 0, false, baseHash, targetHash);
    }

    private static List<DiffEvidenceContract> BuildAddedOrDeletedEvidence(string kind, SymbolView view, EvidenceBudget budget)
    {
        var added = kind == "symbol-added";
        var totalLines = view.Declarations.Sum(declaration => declaration.Lines.Count);
        return view.Declarations
            .Select(declaration => BuildHeaderOnlyEvidence(kind, declaration, added, DiffDigests.Utf8(declaration.RawText), totalLines, budget))
            .ToList();
    }

    private static DiffEvidenceContract BuildRemarkLineHunkEvidence(SymbolView before, SymbolView after, EvidenceBudget budget)
    {
        var baseHash = before.Remark is null ? null : DiffDigests.Utf8(before.Remark);
        var targetHash = after.Remark is null ? null : DiffDigests.Utf8(after.Remark);
        if (before.RemarkMultiFile || after.RemarkMultiFile)
        {
            return new DiffEvidenceContract("remark-changed", DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false, baseHash, targetHash);
        }

        return BuildLineHunkFromLines(
            "remark-changed", before.RemarkLines, after.RemarkLines, before.RemarkPath, after.RemarkPath, baseHash, targetHash, budget);
    }

    // --- Self text: owned-text comparison ---------------------------------------------------------------

    /// <summary>
    /// Stands in for one masked span (another symbol's declaration) inside a self text. In the line keys a
    /// hunk is computed on, it is followed by that symbol's ID and <see cref="PlaceholderEnd"/>, so lines
    /// align by member identity; the classification text uses the bare placeholder. Real occurrences of
    /// these characters in source are escaped with <see cref="PlaceholderEscape"/>.
    /// </summary>
    private const char Placeholder = '\uE000';
    private const char PlaceholderEscape = '\uE001';
    private const char PlaceholderEnd = '\uE002';

    private const string FileLevelTextChangedLimitation = "diff-file-level-text-changed";

    private enum SelfTextDifference { None, WhitespaceOnly, MemberPlaceholdersOnly, Substantive }

    /// <summary>
    /// Classifies the difference between two self texts: (a) whitespace only; (b) only member placeholders
    /// added or removed together with the one list separator (',' or ';') that belongs to each — the trace
    /// of a member added or removed (an enum member, a field declarator), which that member's own entry
    /// explains; (c) anything else (attribute, comment, modifier, a declaration shell, text that moved
    /// relative to its members) is substantive. With anonymous placeholders, two members trading places
    /// while their surrounding text stays put (an attribute that now decorates the other field) reads the
    /// same, so the text is also compared with placeholders named by member ID (members present on both
    /// sides only): if that differs and the visible lines differ beyond whitespace, it is substantive. A
    /// pure reordering of members whose lines hold no other text is not; the member-order rule reports it.
    /// </summary>
    private static SelfTextDifference ClassifySelfText(DeclarationView before, DeclarationView after, SymbolView beforeView, SymbolView afterView)
    {
        // Rule (b'): a line that touches only members existing on this side alone (a field declaration
        // added or removed as a whole, attribute and trailing comment included) is that member's own trace;
        // the member's added/removed entry shows the full line, so it is not container evidence.
        var beforeLines = before.SelfTextLines.Where(line => !OnlyOneSidedMembers(line, afterView.SnapshotSymbolIds)).ToArray();
        var afterLines = after.SelfTextLines.Where(line => !OnlyOneSidedMembers(line, beforeView.SnapshotSymbolIds)).ToArray();

        var beforeTokens = NormalizeWhitespace(string.Concat(beforeLines.Select(line => line.Anonymous)));
        var afterTokens = NormalizeWhitespace(string.Concat(afterLines.Select(line => line.Anonymous)));
        if (!string.Equals(StripMemberPlaceholders(beforeTokens), StripMemberPlaceholders(afterTokens), StringComparison.Ordinal))
        {
            return SelfTextDifference.Substantive;
        }

        var common = new HashSet<string>(before.MemberIds.Intersect(after.MemberIds, StringComparer.Ordinal), StringComparer.Ordinal);
        if (!string.Equals(
                NamedMemberText(beforeLines, common),
                NamedMemberText(afterLines, common),
                StringComparison.Ordinal) &&
            !VisibleLines(beforeLines).SequenceEqual(VisibleLines(afterLines), StringComparer.Ordinal))
        {
            return SelfTextDifference.Substantive;
        }

        if (string.Equals(before.SelfText, after.SelfText, StringComparison.Ordinal))
        {
            return SelfTextDifference.None;
        }

        return string.Equals(NormalizeWhitespace(before.SelfText), NormalizeWhitespace(after.SelfText), StringComparison.Ordinal)
            ? SelfTextDifference.WhitespaceOnly
            : SelfTextDifference.MemberPlaceholdersOnly;
    }

    /// <summary>The text with named placeholders of <paramref name="common"/> members kept and the separator
    /// that follows each of them dropped (separators belong to their member: an existing member gains or
    /// loses its ',' when a member is appended after it or removed).</summary>
    private static string NamedMemberText(IEnumerable<SelfTextLine> lines, IReadOnlySet<string> common)
    {
        var named = StripMemberPlaceholders(NormalizeWhitespace(string.Concat(lines.Select(line => line.Named))), common);
        var builder = new StringBuilder(named.Length);
        for (var i = 0; i < named.Length; i++)
        {
            if (named[i] is ',' or ';' && i > 0 && named[i - 1] == PlaceholderEnd)
            {
                continue;
            }

            builder.Append(named[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rule (b'): the line lies strictly inside the declaration (not its first or last line, which carry the
    /// type's own header and closing text), every member touching it exists only on this side, and the line
    /// is one of those members' header lines — the lines their added/removed header_only evidence shows. A
    /// member's later lines (e.g. a method's closing "} // note" line) stay container text.
    /// </summary>
    private static bool OnlyOneSidedMembers(SelfTextLine line, IReadOnlySet<string> otherSideSymbols) =>
        !line.Boundary && line.MemberHeaderLine && line.Members.Count > 0 && line.Members.All(member => !otherSideSymbols.Contains(member));

    private static IEnumerable<string> VisibleLines(IEnumerable<SelfTextLine> lines) =>
        lines.Where(line => line.Visible).Select(line => NormalizeWhitespace(line.Named)).Where(line => line.Length > 0);

    /// <summary>
    /// Removes each placeholder and the separator attached to it: the separator right after it, or, when
    /// none follows (the last item of a list), the separator right before it. Every other character is kept,
    /// so a change anywhere outside placeholders and their own separators survives. With
    /// <paramref name="keep"/>, the text has named placeholders (each followed by a member ID and
    /// <see cref="PlaceholderEnd"/>) and those of the kept members stay as they are.
    /// </summary>
    private static string StripMemberPlaceholders(string tokens, IReadOnlySet<string>? keep = null)
    {
        static bool IsSeparator(char value) => value is ',' or ';';

        var builder = new StringBuilder(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] != Placeholder)
            {
                builder.Append(tokens[i]);
                continue;
            }

            if (keep is not null)
            {
                var end = tokens.IndexOf(PlaceholderEnd, i + 1);
                if (keep.Contains(tokens[(i + 1)..end]))
                {
                    builder.Append(tokens, i, end - i + 1);
                    i = end;
                    continue;
                }

                i = end;
            }

            if (i + 1 < tokens.Length && IsSeparator(tokens[i + 1]))
            {
                i++;
            }
            else if (builder.Length > 0 && IsSeparator(builder[^1]))
            {
                builder.Length--;
            }
        }

        return builder.ToString();
    }

    /// <summary>One line of a self text: its text with anonymous and with named placeholders, the members
    /// whose spans touch it, whether it is rendered in evidence (it holds more than member text), and
    /// whether it is the declaration's first or last line, and whether it lies within the header lines of
    /// every member touching it.</summary>
    private sealed record SelfTextLine(
        string Anonymous, string Named, IReadOnlySet<string> Members, bool Visible, bool Boundary, bool MemberHeaderLine);

    private sealed record SelfTextView(
        IReadOnlyList<LineHunkBuilder.SourceLine> Lines, string Text, IReadOnlyList<SelfTextLine> TextLines, IReadOnlySet<string> MemberIds);

    /// <summary>
    /// Builds the self text of one declaration: its full source lines where every character inside another
    /// symbol's span (<paramref name="masked"/>, merged and sorted) becomes one placeholder per span, and —
    /// for a nested declaration — characters outside its own span are dropped (they belong to the enclosing
    /// type's self text). Returns the lines the evidence renders (original text, compared on keys whose
    /// placeholders carry the member ID), leaving out lines that hold nothing but member text; the complete
    /// text with anonymous and with named placeholders, used for classification; and the masked member IDs.
    /// </summary>
    private static SelfTextView BuildSelfText(
        string text, IReadOnlyList<int> lineStarts, TextSpanContract span, bool nested, IReadOnlyList<MaskedSpan> masked)
    {
        var lines = new List<LineHunkBuilder.SourceLine>();
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var all = new StringBuilder();
        var textLines = new List<SelfTextLine>();
        var spanEnd = span.Start + span.Length;
        var maskIndex = 0;
        var emittedMask = -1;
        for (var line = span.StartLine; line <= span.EndLine; line++)
        {
            var start = lineStarts[line - 1];
            var end = DiffSnapshotReader.LineContentEnd(text, lineStarts, line);
            var key = new StringBuilder(end - start);
            var anonymous = new StringBuilder(end - start);
            var lineMembers = new HashSet<string>(StringComparer.Ordinal);
            var memberHeaderLine = true;
            var hasOwnText = false;
            var hidesText = false;
            for (var position = start; position < end; position++)
            {
                if (nested && (position < span.Start || position >= spanEnd))
                {
                    hidesText = true;
                    continue;
                }

                while (maskIndex < masked.Count && masked[maskIndex].End <= position)
                {
                    maskIndex++;
                }

                if (maskIndex < masked.Count && masked[maskIndex].Start <= position)
                {
                    hidesText = true;
                    lineMembers.Add(masked[maskIndex].Id);
                    memberHeaderLine &= line >= masked[maskIndex].HeaderFirstLine && line <= masked[maskIndex].HeaderLastLine;
                    if (emittedMask != maskIndex)
                    {
                        key.Append(Placeholder).Append(masked[maskIndex].Id).Append(PlaceholderEnd);
                        anonymous.Append(Placeholder);
                        memberIds.Add(masked[maskIndex].Id);
                        emittedMask = maskIndex;
                    }

                    continue;
                }

                var character = text[position];
                if (character is Placeholder or PlaceholderEscape or PlaceholderEnd)
                {
                    var escaped = character == Placeholder ? '0' : character == PlaceholderEscape ? '1' : '2';
                    key.Append(PlaceholderEscape).Append(escaped);
                    anonymous.Append(PlaceholderEscape).Append(escaped);
                }
                else
                {
                    key.Append(character);
                    anonymous.Append(character);
                }

                hasOwnText |= !char.IsWhiteSpace(character);
            }

            var keyText = key.ToString();
            var anonymousText = anonymous.ToString();
            var visible = hasOwnText || !hidesText;
            all.Append(anonymousText).Append('\n');
            textLines.Add(new SelfTextLine(
                anonymousText, keyText, lineMembers, visible, line == span.StartLine || line == span.EndLine, memberHeaderLine));
            if (visible)
            {
                lines.Add(new LineHunkBuilder.SourceLine(line, text[start..end], keyText));
            }
        }

        return new SelfTextView(lines, all.ToString(), textLines, memberIds);
    }

    /// <summary>
    /// Text outside every indexed declaration's lines (usings, namespace lines, comments between top-level
    /// types) is not owned by any symbol and is not compared per symbol. Rather than let a change there
    /// disappear silently, flag it by comparing that residual text (whitespace-insensitive, remark spans
    /// excluded): for a file present on both sides whose bytes changed; and for a file present on one side
    /// only, against the other-side-only file it shares the most symbols with (a moved, renamed, split or
    /// merged file), looked up in both directions. A one-side-only file sharing no symbol is flagged when it
    /// declares no symbol at all but has non-whitespace text.
    /// </summary>
    private static bool FileLevelTextChanged(
        SymbolDiffSnapshot baseSnapshot, PreparedSnapshot basePrepared, SymbolDiffSnapshot targetSnapshot, PreparedSnapshot targetPrepared)
    {
        var pathComparer = PathComparer;
        var baseIndex = new FileLevelIndex(baseSnapshot, basePrepared, pathComparer);
        var targetIndex = new FileLevelIndex(targetSnapshot, targetPrepared, pathComparer);
        var baseSources = basePrepared.Sources;
        var targetSources = targetPrepared.Sources;

        foreach (var path in baseSources.Keys.Where(targetSources.ContainsKey).Order(StringComparer.Ordinal))
        {
            if (!string.Equals(baseSources[path].Document.ContentHash, targetSources[path].Document.ContentHash, StringComparison.Ordinal) &&
                !string.Equals(baseIndex.Residual(path), targetIndex.Residual(path), StringComparison.Ordinal))
            {
                return true;
            }
        }

        var baseOnly = baseSources.Keys.Where(path => !targetSources.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        var targetOnly = targetSources.Keys.Where(path => !baseSources.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        var pairs = new HashSet<(string Base, string Target)>();
        var targetOnlyById = InvertSymbolIds(targetIndex, targetOnly);
        var baseOnlyById = InvertSymbolIds(baseIndex, baseOnly);
        foreach (var path in baseOnly)
        {
            if (BestSharedFile(baseIndex.SymbolIds(path), targetOnlyById) is { } partner)
            {
                pairs.Add((path, partner));
            }
        }

        foreach (var path in targetOnly)
        {
            if (BestSharedFile(targetIndex.SymbolIds(path), baseOnlyById) is { } partner)
            {
                pairs.Add((partner, path));
            }
        }

        foreach (var (basePath, targetPath) in pairs.OrderBy(pair => pair.Base, StringComparer.Ordinal).ThenBy(pair => pair.Target, StringComparer.Ordinal))
        {
            if (!string.Equals(baseIndex.Residual(basePath), targetIndex.Residual(targetPath), StringComparison.Ordinal))
            {
                return true;
            }
        }

        var paired = pairs.Select(pair => pair.Base).Concat(pairs.Select(pair => pair.Target)).ToHashSet(pathComparer);
        return baseOnly.Where(path => !paired.Contains(path)).Any(path => !baseIndex.HasDeclarations(path) && baseIndex.Residual(path).Length > 0) ||
               targetOnly.Where(path => !paired.Contains(path)).Any(path => !targetIndex.HasDeclarations(path) && targetIndex.Residual(path).Length > 0);
    }

    private static Dictionary<string, List<string>> InvertSymbolIds(FileLevelIndex index, IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var id in index.SymbolIds(path))
            {
                if (!result.TryGetValue(id, out var list))
                {
                    list = [];
                    result[id] = list;
                }

                list.Add(path);
            }
        }

        return result;
    }

    /// <summary>The candidate file sharing the most symbol IDs with <paramref name="symbolIds"/> (ties:
    /// ordinal smallest path), or null when none shares any.</summary>
    private static string? BestSharedFile(IReadOnlySet<string> symbolIds, IReadOnlyDictionary<string, List<string>> candidatesById)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in symbolIds)
        {
            foreach (var candidate in candidatesById.GetValueOrDefault(id, []))
            {
                counts[candidate] = counts.GetValueOrDefault(candidate) + 1;
            }
        }

        return counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key).FirstOrDefault();
    }

    /// <summary>Per-file declaration lines, remark spans and symbol IDs of one snapshot, indexed once.</summary>
    private sealed class FileLevelIndex
    {
        private readonly PreparedSnapshot prepared;
        private readonly Dictionary<string, List<(int StartLine, int EndLine)>> declarationLines;
        private readonly Dictionary<string, List<(int Start, int End)>> remarks;
        private readonly Dictionary<string, HashSet<string>> symbolIds;
        private readonly Dictionary<string, string> residuals;

        public FileLevelIndex(SymbolDiffSnapshot snapshot, PreparedSnapshot prepared, StringComparer pathComparer)
        {
            this.prepared = prepared;
            declarationLines = new Dictionary<string, List<(int, int)>>(pathComparer);
            remarks = new Dictionary<string, List<(int, int)>>(pathComparer);
            symbolIds = new Dictionary<string, HashSet<string>>(pathComparer);
            residuals = new Dictionary<string, string>(pathComparer);
            foreach (var symbol in snapshot.Symbols)
            {
                foreach (var declaration in symbol.Declarations.Where(item => item.Location.Path is not null))
                {
                    var path = DiffSnapshotReader.NormalizePath(declaration.Location.Path!);
                    GetOrAdd(declarationLines, path).Add((declaration.Location.Span.StartLine, declaration.Location.Span.EndLine));
                    if (!symbolIds.TryGetValue(path, out var ids))
                    {
                        ids = new HashSet<string>(StringComparer.Ordinal);
                        symbolIds[path] = ids;
                    }

                    ids.Add(symbol.SymbolId);
                }
            }

            foreach (var remark in snapshot.Remarks.Where(item => item.Location.Path is not null))
            {
                GetOrAdd(remarks, DiffSnapshotReader.NormalizePath(remark.Location.Path!))
                    .Add((remark.Location.Span.Start, remark.Location.Span.Start + remark.Location.Span.Length));
            }
        }

        public bool HasDeclarations(string path) => declarationLines.ContainsKey(path);

        public IReadOnlySet<string> SymbolIds(string path) =>
            symbolIds.TryGetValue(path, out var ids) ? ids : new HashSet<string>(StringComparer.Ordinal);

        public string Residual(string path)
        {
            if (residuals.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var text = prepared.Sources[path].Text;
            var starts = prepared.LineStarts(path);
            var depth = new int[starts.Count + 2];
            foreach (var (startLine, endLine) in declarationLines.GetValueOrDefault(path, []))
            {
                depth[Math.Clamp(startLine, 1, starts.Count + 1)]++;
                depth[Math.Clamp(endLine + 1, 1, starts.Count + 1)]--;
            }

            var remarkSpans = remarks.GetValueOrDefault(path, []).OrderBy(item => item.Start).ToArray();
            var residual = new StringBuilder();
            var covered = 0;
            var remarkIndex = 0;
            for (var line = 1; line <= starts.Count; line++)
            {
                covered += depth[line];
                if (covered > 0)
                {
                    continue;
                }

                var end = DiffSnapshotReader.LineContentEnd(text, starts, line);
                for (var position = starts[line - 1]; position < end; position++)
                {
                    if (char.IsWhiteSpace(text[position]))
                    {
                        continue;
                    }

                    while (remarkIndex < remarkSpans.Length && remarkSpans[remarkIndex].End <= position)
                    {
                        remarkIndex++;
                    }

                    // Remark spans are sorted by start; one starting later can still cover this position only
                    // if an earlier one does not, so check the remaining ones that start at or before it.
                    var inRemark = false;
                    for (var i = remarkIndex; i < remarkSpans.Length && remarkSpans[i].Start <= position; i++)
                    {
                        if (position < remarkSpans[i].End)
                        {
                            inRemark = true;
                            break;
                        }
                    }

                    if (!inRemark)
                    {
                        residual.Append(text[position]);
                    }
                }
            }

            var result = residual.ToString();
            residuals[path] = result;
            return result;
        }

        private static List<(int, int)> GetOrAdd(Dictionary<string, List<(int, int)>> map, string path)
        {
            if (!map.TryGetValue(path, out var list))
            {
                list = [];
                map[path] = list;
            }

            return list;
        }
    }

    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Member IDs in declaration order: by the paired declaration (see <see cref="PairDeclarations"/>) that
    /// contains each member, then by position. Ordering by the declaration pair rather than by file path keeps
    /// a partial declaration moved to a differently named file from reading as a member reordering.
    /// </summary>
    private static string[] MemberOrder(
        IReadOnlyList<SymbolContract> members, IReadOnlyList<(DeclarationView? Base, DeclarationView? Target)> pairs, bool useBase)
    {
        var pathComparer = PathComparer;
        int PairIndex(LocationContract location)
        {
            for (var i = 0; i < pairs.Count; i++)
            {
                var declaration = useBase ? pairs[i].Base : pairs[i].Target;
                if (declaration is not null && location.Path is not null && declaration.Location.Path is not null &&
                    pathComparer.Equals(DiffSnapshotReader.NormalizePath(location.Path), DiffSnapshotReader.NormalizePath(declaration.Location.Path)) &&
                    location.Span.Start >= declaration.Location.Span.Start &&
                    location.Span.Start < declaration.Location.Span.Start + declaration.Location.Span.Length)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        return members
            .Select(member => (member.SymbolId, Location: PrimaryLocation(member)))
            .OrderBy(item => PairIndex(item.Location))
            .ThenBy(item => item.Location.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Location.Span.Start)
            .Select(item => item.SymbolId)
            .ToArray();
    }

    private static bool OrderChanged(IReadOnlyList<string> baseOrder, IReadOnlyList<string> targetOrder)
    {
        if (baseOrder.Count == 0 && targetOrder.Count == 0)
        {
            return false;
        }

        var targetSet = new HashSet<string>(targetOrder, StringComparer.Ordinal);
        var baseCommon = baseOrder.Where(id => targetSet.Contains(id)).ToArray();
        var baseSet = new HashSet<string>(baseOrder, StringComparer.Ordinal);
        var targetCommon = targetOrder.Where(id => baseSet.Contains(id)).ToArray();
        return !baseCommon.SequenceEqual(targetCommon, StringComparer.Ordinal);
    }

    private static (IReadOnlyDictionary<string, SymbolContract[]> MembersByContainer, IReadOnlyDictionary<string, string[]> Order)
        IndexContainers(SymbolDiffSnapshot snapshot)
    {
        var membersByContainer = snapshot.Symbols
            .Where(symbol => symbol.ContainerId is not null)
            .GroupBy(symbol => symbol.ContainerId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(symbol => PrimaryLocation(symbol).Path, StringComparer.Ordinal)
                    .ThenBy(symbol => PrimaryLocation(symbol).Span.Start).ToArray(),
                StringComparer.Ordinal);
        var order = membersByContainer.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(symbol => symbol.SymbolId).ToArray(),
            StringComparer.Ordinal);
        return (membersByContainer, order);
    }

    private static LocationContract PrimaryLocation(SymbolContract symbol) =>
        symbol.Declarations.OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Location.Span.Start).First().Location;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A snapshot validated once, with its decoded sources, line starts, container index and
    /// symbol views, shared by validation and comparison.</summary>
    private sealed class PreparedSnapshot(
        IReadOnlyDictionary<string, DiffSnapshotReader.DecodedSource> sources,
        IReadOnlyDictionary<string, SymbolContract[]> membersByContainer,
        IReadOnlyDictionary<string, string[]> order)
    {
        private readonly Dictionary<string, List<int>> lineStarts = new(PathComparer);

        public IReadOnlyDictionary<string, DiffSnapshotReader.DecodedSource> Sources { get; } = sources;

        public IReadOnlyDictionary<string, SymbolContract[]> MembersByContainer { get; } = membersByContainer;

        public IReadOnlyDictionary<string, string[]> Order { get; } = order;

        public IReadOnlyDictionary<string, SymbolView> View { get; set; } = new Dictionary<string, SymbolView>();

        public List<int> LineStarts(string path)
        {
            if (!lineStarts.TryGetValue(path, out var starts))
            {
                starts = DiffSnapshotReader.LineStarts(Sources[path].Text);
                lineStarts[path] = starts;
            }

            return starts;
        }
    }

    /// <summary>One masked span in a self text: the outermost symbol it stands for and that symbol's header
    /// lines (the lines its added/removed header_only evidence renders).</summary>
    private readonly record struct MaskedSpan(int Start, int End, string Id, int HeaderFirstLine, int HeaderLastLine);

    /// <summary>
    /// All declaration spans of one file, sorted by (start, end descending, input order), with each span's
    /// enclosing span. Declaration spans nest or are disjoint in practice, so the spans intersecting a line
    /// range are the run starting inside it plus the enclosing chain of the last span starting before it —
    /// found by binary search in time proportional to the answer, not the file. A file whose spans overlap
    /// without nesting falls back to a linear scan, so the result is the same either way.
    /// </summary>
    private sealed class FileSpanIndex
    {
        private readonly (string Id, int Start, int End, int Sequence, int HeaderFirstLine, int HeaderLastLine)[] spans;
        private readonly int[] parent;
        private readonly bool laminar;

        public FileSpanIndex(List<(string Id, int Start, int End, int Sequence, int HeaderFirstLine, int HeaderLastLine)> items)
        {
            spans = items.OrderBy(item => item.Start).ThenByDescending(item => item.End).ThenBy(item => item.Sequence).ToArray();
            parent = new int[spans.Length];
            laminar = true;
            var stack = new Stack<int>();
            for (var i = 0; i < spans.Length; i++)
            {
                while (stack.Count > 0 && spans[stack.Peek()].End <= spans[i].Start)
                {
                    stack.Pop();
                }

                if (stack.Count > 0 && spans[i].End > spans[stack.Peek()].End)
                {
                    laminar = false;
                }

                parent[i] = stack.Count > 0 ? stack.Peek() : -1;
                stack.Push(i);
            }
        }

        public IEnumerable<(string Id, int Start, int End, int Sequence, int HeaderFirstLine, int HeaderLastLine)> Intersecting(int rangeStart, int rangeEnd)
        {
            if (!laminar)
            {
                return spans.Where(item => item.Start < rangeEnd && item.End > rangeStart);
            }

            var result = new List<(string, int, int, int, int, int)>();
            var low = 0;
            var high = spans.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (spans[middle].Start < rangeStart)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            for (var k = low - 1; k >= 0; k = parent[k])
            {
                if (spans[k].End > rangeStart)
                {
                    result.Add(spans[k]);
                }
            }

            for (var i = low; i < spans.Length && spans[i].Start < rangeEnd; i++)
            {
                result.Add(spans[i]);
            }

            return result;
        }
    }

    private static PreparedSnapshot PrepareSnapshot(SymbolDiffSnapshot snapshot, string name)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId) || string.IsNullOrWhiteSpace(snapshot.InputFingerprint))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"{name} identity is required.");
        }
        snapshot.Coverage.Validate();
        var symbolIds = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.Symbols.Any(symbol => !symbolIds.Add(symbol.SymbolId)))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"{name} contains duplicate symbol IDs.");
        }
        foreach (var symbol in snapshot.Symbols) symbol.Validate();
        foreach (var remark in snapshot.Remarks)
        {
            if (!symbolIds.Contains(remark.SymbolId))
                throw new DiffException(DiffErrorCodes.InvalidSnapshot, $"{name} contains a remark for an unknown symbol.");
            remark.Location.Validate();
        }

        var sources = DiffSnapshotReader.DecodeSources(snapshot);
        var containers = IndexContainers(snapshot);
        var prepared = new PreparedSnapshot(sources, containers.MembersByContainer, containers.Order);
        prepared.View = CreateView(snapshot, prepared);
        return prepared;
    }

    private static IReadOnlyDictionary<string, SymbolView> CreateView(SymbolDiffSnapshot snapshot, PreparedSnapshot prepared)
    {
        var sources = prepared.Sources;
        var membersByContainer = prepared.MembersByContainer;
        var pathComparer = PathComparer;
        var bySymbolId = snapshot.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var snapshotSymbolIds = new HashSet<string>(bySymbolId.Keys, StringComparer.Ordinal);

        var remarksBySymbol = snapshot.Remarks.GroupBy(remark => remark.SymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Location.Path, StringComparer.Ordinal)
                    .ThenBy(item => item.Location.Span.Start).ToArray(),
                StringComparer.Ordinal);

        // Validate every declaration and remark location first, symbol by symbol in the same order as
        // before (declarations, then that symbol's remarks), so the same error surfaces first; then index.
        string ExtractLocation(LocationContract location)
        {
            var path = location.Path is null ? null : DiffSnapshotReader.NormalizePath(location.Path);
            var starts = path is not null && sources.ContainsKey(path) ? prepared.LineStarts(path) : null;
            return DiffSnapshotReader.Extract(location, sources, starts);
        }

        var extracted = new Dictionary<DeclarationContract, string>(ReferenceEqualityComparer.Instance);
        var remarkTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.Symbols)
        {
            foreach (var declaration in symbol.Declarations
                         .OrderBy(item => item.Location.Path, StringComparer.Ordinal).ThenBy(item => item.Location.Span.Start))
            {
                extracted[declaration] = ExtractLocation(declaration.Location);
            }

            if (remarksBySymbol.TryGetValue(symbol.SymbolId, out var symbolRemarks))
            {
                remarkTexts[symbol.SymbolId] = string.Join("\n", symbolRemarks.Select(item => ExtractLocation(item.Location)));
            }
        }

        var spanItems = new Dictionary<string, List<(string Id, int Start, int End, int Sequence, int HeaderFirstLine, int HeaderLastLine)>>(pathComparer);
        var sequence = 0;
        foreach (var symbol in snapshot.Symbols)
        {
            foreach (var declaration in symbol.Declarations)
            {
                var span = declaration.Location.Span;
                var path = DiffSnapshotReader.NormalizePath(declaration.Location.Path!);
                if (span.Length <= 0)
                {
                    sequence++;
                    continue;
                }

                var (headerFirst, headerLast) = DiffSnapshotReader.HeaderLineRange(sources[path].Text, span, prepared.LineStarts(path));
                if (!spanItems.TryGetValue(path, out var list))
                {
                    list = [];
                    spanItems[path] = list;
                }

                list.Add((symbol.SymbolId, span.Start, span.Start + span.Length, sequence++, headerFirst, headerLast));
            }
        }

        var spansByPath = spanItems.ToDictionary(pair => pair.Key, pair => new FileSpanIndex(pair.Value), pathComparer);

        var views = new Dictionary<string, SymbolView>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.Symbols)
        {
            var isContainer = membersByContainer.ContainsKey(symbol.SymbolId);
            var ancestors = Ancestors(symbol, bySymbolId);
            var nested = ancestors.Count > 0;
            var orderedDeclarations = symbol.Declarations
                .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Location.Span.Start)
                .ToArray();

            var declarationViews = new List<DeclarationView>();
            foreach (var declaration in orderedDeclarations)
            {
                var location = declaration.Location;
                var rawText = extracted[declaration];
                var path = DiffSnapshotReader.NormalizePath(location.Path!);
                var sourceText = sources[path].Text;
                var lineStarts = prepared.LineStarts(path);

                // Every other symbol's span on this declaration's lines is masked, except the spans of its
                // own enclosing types (which contain it): each character of a type's lines then belongs to
                // exactly one leaf span or to the innermost type's self text.
                var rangeStart = lineStarts[location.Span.StartLine - 1];
                var rangeEnd = DiffSnapshotReader.LineContentEnd(sourceText, lineStarts, location.Span.EndLine);
                var intersecting = spansByPath.TryGetValue(path, out var index)
                    ? index.Intersecting(rangeStart, rangeEnd)
                    : [];
                var masked = MergeIntervals(intersecting
                    .Where(item => !string.Equals(item.Id, symbol.SymbolId, StringComparison.Ordinal) && !ancestors.Contains(item.Id))
                    .OrderBy(item => item.Start).ThenBy(item => item.End).ThenBy(item => item.Sequence)
                    .Select(item => new MaskedSpan(item.Start, item.End, item.Id, item.HeaderFirstLine, item.HeaderLastLine)));
                var self = BuildSelfText(sourceText, lineStarts, location.Span, nested, masked);

                var lines = DiffSnapshotReader.FullLines(sourceText, location.Span, lineStarts);
                var headerLines = DiffSnapshotReader.HeaderLines(sourceText, location.Span, lineStarts);
                declarationViews.Add(new DeclarationView(location, rawText, lines, headerLines, self.Lines, self.Text, self.TextLines, self.MemberIds));
            }

            var remark = remarksBySymbol.TryGetValue(symbol.SymbolId, out var remarkLocations) ? remarkTexts[symbol.SymbolId] : null;
            IReadOnlyList<LineHunkBuilder.SourceLine> remarkLines = [];
            string? remarkPath = null;
            var remarkMultiFile = false;
            if (remarkLocations is { Length: > 0 })
            {
                var distinctPaths = remarkLocations.Select(item => item.Location.Path!).Distinct(pathComparer).ToArray();
                // Multiple remark locations normally come from partial declarations. If they land in
                // different files, a single "--- a/<path>" hunk header can only ever name one of them, so
                // (M3) render this comparison as fingerprint-only rather than misattributing every line to
                // whichever file happened to sort first.
                remarkMultiFile = distinctPaths.Length > 1;
                remarkPath = distinctPaths[0];
                remarkLines = remarkLocations
                    .SelectMany(item =>
                    {
                        var remarkFile = DiffSnapshotReader.NormalizePath(item.Location.Path!);
                        return DiffSnapshotReader.FullLines(sources[remarkFile].Text, item.Location.Span, prepared.LineStarts(remarkFile));
                    })
                    .ToArray();
            }

            var selfText = string.Join("\n// --- partial declaration ---\n", declarationViews.Select(view => view.SelfText));
            var fullText = string.Join("\n// --- partial declaration ---\n", declarationViews.Select(view => view.RawText));
            var primaryPath = orderedDeclarations[0].Location.Path ?? orderedDeclarations[0].Location.Uri ?? "unknown";

            views[symbol.SymbolId] = new SymbolView(
                symbol, declarationViews, selfText, fullText, remark, remarkLines, remarkPath, remarkMultiFile, isContainer,
                OwnsSelfText: isContainer || !nested, primaryPath, snapshotSymbolIds);
        }

        return views;
    }

    /// <summary>The IDs of the types enclosing <paramref name="symbol"/>, following indexed containerIds.</summary>
    private static HashSet<string> Ancestors(SymbolContract symbol, IReadOnlyDictionary<string, SymbolContract> bySymbolId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var current = symbol;
        while (current.ContainerId is not null && bySymbolId.TryGetValue(current.ContainerId, out var container) && result.Add(container.SymbolId))
        {
            current = container;
        }

        return result;
    }

    /// <summary>Merges overlapping spans (a member and the members nested in it) into one masked interval,
    /// named after the span that starts first (the outermost one).</summary>
    private static List<MaskedSpan> MergeIntervals(IEnumerable<MaskedSpan> intervals)
    {
        var merged = new List<MaskedSpan>();
        foreach (var item in intervals.OrderBy(item => item.Start).ThenByDescending(item => item.End))
        {
            if (merged.Count > 0 && item.Start < merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, item.End) };
            }
            else
            {
                merged.Add(item);
            }
        }

        return merged;
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
        // Leaf symbols use the declaration's own span text (FullText), not whole source lines, for the same
        // reason as the leaf change-detection gate above (M2): a rename candidate's "shape" must not be
        // perturbed by an unrelated neighbor sharing a physical line with this declaration. Containers must
        // use their self text (member spans masked) (N2): a container's FullText includes every member's full
        // text, so renaming the type while also editing a member's body would otherwise change the shape
        // twice over and wrongly break the rename match.
        var source = (view.IsContainer ? view.SelfText : view.FullText).Replace(view.Symbol.Name, "<name>", StringComparison.Ordinal);
        return NormalizeWhitespace(signature + "\n" + source);
    }

    private static string NormalizeWhitespace(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string SortKey(SymbolContract symbol) =>
        $"{symbol.ProjectId}\n{symbol.QualifiedName}\n{symbol.Kind}\n{symbol.Signature}\n{symbol.SymbolId}";

    private sealed record DeclarationView(
        LocationContract Location,
        string RawText,
        IReadOnlyList<LineHunkBuilder.SourceLine> Lines,
        IReadOnlyList<LineHunkBuilder.SourceLine> HeaderLines,
        IReadOnlyList<LineHunkBuilder.SourceLine> SelfLines,
        string SelfText,
        IReadOnlyList<SelfTextLine> SelfTextLines,
        IReadOnlySet<string> MemberIds);

    private sealed record SymbolView(
        SymbolContract Symbol,
        IReadOnlyList<DeclarationView> Declarations,
        string SelfText,
        string FullText,
        string? Remark,
        IReadOnlyList<LineHunkBuilder.SourceLine> RemarkLines,
        string? RemarkPath,
        bool RemarkMultiFile,
        bool IsContainer,
        bool OwnsSelfText,
        string PrimaryPath,
        IReadOnlySet<string> SnapshotSymbolIds);

    private sealed class EvidenceBudget(DiffEvidenceOptions options)
    {
        private int used;

        public DiffEvidenceOptions Options { get; } = options;

        public bool Exhausted { get; private set; }

        public bool TryConsume(int bytes)
        {
            if (Exhausted)
            {
                return false;
            }

            if (used + bytes > Options.MaxEvidenceBytesTotal)
            {
                Exhausted = true;
                return false;
            }

            used += bytes;
            return true;
        }
    }
}
