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
        ValidateSnapshot(request.Base, nameof(request.Base));
        ValidateSnapshot(request.Target, nameof(request.Target));
        if (!string.Equals(request.Baseline.BaseId, request.Base.SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(request.Baseline.TargetId, request.Target.SnapshotId, StringComparison.Ordinal))
        {
            throw new DiffException(DiffErrorCodes.InvalidSnapshot, "Baseline IDs do not match the supplied immutable snapshots.");
        }

        var baseContainers = IndexContainers(request.Base);
        var targetContainers = IndexContainers(request.Target);
        var baseView = CreateView(request.Base, baseContainers.MembersByContainer);
        var targetView = CreateView(request.Target, targetContainers.MembersByContainer);
        var budget = new EvidenceBudget(options);
        var changes = new List<SymbolDiffChange>();
        var unmatchedBase = new Dictionary<string, SymbolView>(baseView, StringComparer.Ordinal);
        var unmatchedTarget = new Dictionary<string, SymbolView>(targetView, StringComparer.Ordinal);

        foreach (var symbolId in baseView.Keys.Intersect(targetView.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = baseView[symbolId];
            var after = targetView[symbolId];
            AddMatchedChanges(changes, before, after, baseContainers.Order, targetContainers.Order, budget);
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

        // Changed-line coverage safety net (N1): every entry computed so far only reports a symbol when its
        // OWN declaration span text differs. A symbol's span can leave out text on the same physical line
        // (an attribute, a modifier, a trailing comment) that this project's C# extractor does not index
        // separately, so an edit confined to that leftover text would otherwise vanish from the diff
        // entirely (a "silent partial" — the file digest changed but the diff reports nothing). This pass
        // whole-file diffs every digest-changed source, and for any changed line not already covered by an
        // existing entry's declaration range, attributes it to the innermost indexed symbol whose range
        // covers that line and reports it there, rather than dropping it.
        var symbolOrdered = changes.OrderBy(change => change.Kind)
            .ThenBy(change => SortKey(change.BaseSymbol ?? change.TargetSymbol!), StringComparer.Ordinal)
            .ToArray();
        var safetyNet = ComputeChangedLineSafetyNet(request.Base, request.Target, baseView, targetView, symbolOrdered, budget);
        changes.AddRange(safetyNet.Changes);

        var ordered = changes.OrderBy(change => change.Kind)
            .ThenBy(change => SortKey(change.BaseSymbol ?? change.TargetSymbol!), StringComparer.Ordinal)
            .ToArray();
        var limitations = request.Base.Coverage.Limitations.Concat(request.Target.Coverage.Limitations)
            .Concat(safetyNet.Limitations)
            .Distinct(StringComparer.Ordinal).ToList();
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
        if (safetyNet.CoverageTruncated)
        {
            // A changed file's edit distance exceeded the line-diff cap before the safety net could even
            // determine whether any of its changes were unattributed, and no ordinary entry mentions that
            // file at all — make that explicit via coverage rather than silently returning a complete-looking
            // empty diff for it.
            coverage = coverage with { Level = CoverageLevel.Partial, Truncated = true };
        }

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
        IReadOnlyDictionary<string, string[]> baseContainerOrder,
        IReadOnlyDictionary<string, string[]> targetContainerOrder,
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
            var evidences = new List<DiffEvidenceContract>();
            var selfChanged = NormalizeWhitespace(before.CompareText) != NormalizeWhitespace(after.CompareText);
            if (selfChanged)
            {
                evidences.AddRange(BuildSymbolLineHunkEvidence("body-changed", before, after, budget));
            }

            var baseOrder = baseContainerOrder.GetValueOrDefault(before.Symbol.SymbolId, []);
            var targetOrder = targetContainerOrder.GetValueOrDefault(after.Symbol.SymbolId, []);
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
        else if (!string.Equals(before.FullText, after.FullText, StringComparison.Ordinal))
        {
            // Change detection for a leaf (non-container) symbol must compare its own declaration span
            // text (FullText), not whole source lines (CompareText): a leaf's line can be shared with an
            // unrelated neighboring symbol (an enum member list, or "int a, b;" field declarators), and
            // that neighbor's edit must not make this symbol appear changed too (M2).
            var formatting = NormalizeWhitespace(before.FullText) == NormalizeWhitespace(after.FullText);
            var kind = formatting ? DiffKind.FormattingOnly : DiffKind.BodyChanged;
            changes.Add(BuildChange(kind, before, after, 1m,
                BuildSymbolLineHunkEvidence(formatting ? "formatting-only" : "body-changed", before, after, budget)));
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

            changes.Add(BuildChange(DiffKind.RenameCandidate, before, after, 0.5m,
                BuildSymbolLineHunkEvidence("rename-candidate", before, after, budget)));
            unmatchedBase.Remove(before.Symbol.SymbolId);
            unmatchedTarget.Remove(after.Symbol.SymbolId);
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

    private static List<DiffEvidenceContract> BuildSymbolLineHunkEvidence(
        string kind, SymbolView before, SymbolView after, EvidenceBudget budget)
    {
        // Pair declarations by their normalized file path first (M4), not by list index: a symbol's
        // Declarations are sorted by (path, span start), so if a partial declaration's file was replaced
        // (base has A.cs+B.cs, target has A.cs+C.cs), index-based pairing would compare B.cs against C.cs
        // as if one were edited into the other, instead of reporting B.cs removed and C.cs added.
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var baseByPath = before.Declarations.GroupBy(declaration => DiffSnapshotReader.NormalizePath(declaration.Location.Path!), pathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), pathComparer);
        var afterByPath = after.Declarations.GroupBy(declaration => DiffSnapshotReader.NormalizePath(declaration.Location.Path!), pathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), pathComparer);
        var allPaths = baseByPath.Keys.Concat(afterByPath.Keys).Distinct(pathComparer).Order(StringComparer.Ordinal);

        var evidences = new List<DiffEvidenceContract>();
        foreach (var path in allPaths)
        {
            var baseDeclarations = baseByPath.GetValueOrDefault(path, []);
            var targetDeclarations = afterByPath.GetValueOrDefault(path, []);
            var pairCount = Math.Min(baseDeclarations.Length, targetDeclarations.Length);
            for (var i = 0; i < pairCount; i++)
            {
                var baseDeclaration = baseDeclarations[i];
                var targetDeclaration = targetDeclarations[i];
                if (string.Equals(baseDeclaration.RawText, targetDeclaration.RawText, StringComparison.Ordinal))
                {
                    // Nothing changed in this declaration's own span text; a different declaration in the
                    // same symbol (or, for a container, self text elsewhere) is what triggered this entry.
                    continue;
                }

                evidences.Add(BuildLineHunkFromLines(
                    kind, baseDeclaration.Lines, targetDeclaration.Lines, baseDeclaration.Location.Path, targetDeclaration.Location.Path,
                    DiffDigests.Utf8(baseDeclaration.RawText), DiffDigests.Utf8(targetDeclaration.RawText), budget));
            }

            for (var i = pairCount; i < baseDeclarations.Length; i++)
            {
                var declaration = baseDeclarations[i];
                evidences.Add(BuildHeaderOnlyEvidence("declaration-removed", declaration, added: false,
                    DiffDigests.Utf8(declaration.RawText), declaration.Lines.Count, budget));
            }

            for (var i = pairCount; i < targetDeclarations.Length; i++)
            {
                var declaration = targetDeclarations[i];
                evidences.Add(BuildHeaderOnlyEvidence("declaration-added", declaration, added: true,
                    DiffDigests.Utf8(declaration.RawText), declaration.Lines.Count, budget));
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

    // --- Changed-line coverage safety net (N1, hardened per X1-X4) --------------------------------------

    private const string UnattributedLineChangedEvidenceKind = "span-adjacent-line-changed";
    private const string ChangedLineAttributedLimitation = "diff-span-adjacent-line-attributed";
    private const string UnattributedChangeBudgetLimitation = "diff-unattributed-change-budget-exceeded";

    private sealed record SafetyNetResult(List<SymbolDiffChange> Changes, List<string> Limitations, bool CoverageTruncated);

    private static SafetyNetResult ComputeChangedLineSafetyNet(
        SymbolDiffSnapshot baseSnapshot,
        SymbolDiffSnapshot targetSnapshot,
        IReadOnlyDictionary<string, SymbolView> baseView,
        IReadOnlyDictionary<string, SymbolView> targetView,
        IReadOnlyList<SymbolDiffChange> existingChanges,
        EvidenceBudget budget)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var baseSourcesByPath = baseSnapshot.Sources.ToDictionary(source => DiffSnapshotReader.NormalizePath(source.Path), pathComparer);
        var targetSourcesByPath = targetSnapshot.Sources.ToDictionary(source => DiffSnapshotReader.NormalizePath(source.Path), pathComparer);

        var commonPairs = baseSourcesByPath.Keys.Intersect(targetSourcesByPath.Keys, pathComparer)
            .Where(path => !string.Equals(baseSourcesByPath[path].ContentHash, targetSourcesByPath[path].ContentHash, StringComparison.Ordinal))
            .Select(path => (BasePath: path, TargetPath: path));

        // X3: a file that was renamed/moved (present only in base, matched by a different only-in-target
        // path) still needs whole-file attribution — pair each base-only path with whichever target-only
        // path shares the most symbol IDs (the file that "became" it), not just paths with the same name.
        var renamedPairs = FindRenamedFilePairs(baseSnapshot, targetSnapshot, baseSourcesByPath, targetSourcesByPath, pathComparer);

        var filePairs = commonPairs.Concat(renamedPairs)
            .OrderBy(pair => pair.BasePath, StringComparer.Ordinal).ThenBy(pair => pair.TargetPath, StringComparer.Ordinal)
            .ToArray();
        if (filePairs.Length == 0)
        {
            return new SafetyNetResult([], [], false);
        }

        var attributedBase = AttributedLines(existingChanges, useBase: true, baseView, pathComparer);
        var attributedTarget = AttributedLines(existingChanges, useBase: false, targetView, pathComparer);
        var baseSources = DiffSnapshotReader.DecodeSources(baseSnapshot);
        var targetSources = DiffSnapshotReader.DecodeSources(targetSnapshot);
        var baseBySymbolId = baseSnapshot.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var targetBySymbolId = targetSnapshot.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);

        // Substantive changes take priority over whitespace-only ones for the same symbol (X4): if a symbol
        // ends up in both buckets, the whitespace-only entries are dropped in favor of the real one.
        var pendingBodyChanged = new Dictionary<string, HashSet<(string BasePath, string TargetPath)>>(StringComparer.Ordinal);
        var pendingFormattingOnly = new Dictionary<string, HashSet<(string BasePath, string TargetPath)>>(StringComparer.Ordinal);
        var limitations = new List<string>();
        var coverageTruncated = false;

        void ProcessUnit(
            string basePath, string targetPath,
            IReadOnlyList<LineHunkBuilder.SourceLine> baseFileLines, IReadOnlyList<LineHunkBuilder.SourceLine> targetFileLines,
            LineHunkBuilder.Edit? deleteEdit, LineHunkBuilder.Edit? insertEdit)
        {
            var baseLine = deleteEdit is { } de ? baseFileLines[de.BaseIndex].LineNumber : (int?)null;
            var targetLine = insertEdit is { } ie ? targetFileLines[ie.TargetIndex].LineNumber : (int?)null;

            var baseAttributed = baseLine is int bl && attributedBase.TryGetValue(basePath, out var bLines) && bLines.Contains(bl);
            var targetAttributed = targetLine is int tl && attributedTarget.TryGetValue(targetPath, out var tLines) && tLines.Contains(tl);
            if (baseAttributed || targetAttributed)
            {
                // X1: attribution is decided per delete/insert PAIR (or lone unpaired line), never for the
                // whole run — an already-attributed neighbor sharing a run with a genuinely unattributed
                // line (e.g. two adjacent edited field declarations) must not suppress the latter. Either
                // side being attributed is conclusive for the pair as a whole: e.g. adding an enum member on
                // a shared line is already fully explained by that member's own Added entry (whose location
                // covers the target line) - the untouched siblings on the same line must not also be flagged
                // (M2), even though the base side of that same replace pair has no entry of its own.
                return;
            }

            var coveringSymbol = baseLine is int blCover ? FindCoveringSymbol(baseSnapshot, baseBySymbolId, basePath, blCover, pathComparer) : null;
            coveringSymbol ??= targetLine is int tlCover ? FindCoveringSymbol(targetSnapshot, targetBySymbolId, targetPath, tlCover, pathComparer) : null;
            if (coveringSymbol is null)
            {
                // Outside every indexed symbol's declaration range (a using/namespace line, blank line
                // between top-level types, etc.). Out of scope for this safety net; existing behavior
                // (silently not represented per-symbol) is unchanged for these lines.
                return;
            }

            var baseText = deleteEdit is { } deText ? baseFileLines[deText.BaseIndex].Text : null;
            var targetText = insertEdit is { } ieText ? targetFileLines[ieText.TargetIndex].Text : null;
            var whitespaceOnly = IsWhitespaceOnlyChange(baseText, targetText);

            var isContainer = (baseView.TryGetValue(coveringSymbol.SymbolId, out var baseCoveringView) && baseCoveringView.IsContainer) ||
                               (targetView.TryGetValue(coveringSymbol.SymbolId, out var targetCoveringView) && targetCoveringView.IsContainer);

            if (whitespaceOnly)
            {
                // X4 / U2 rule 3: a container's own self text ignores whitespace-only differences entirely
                // (no entry at all); a leaf's whitespace-only edit is still reported, but as formatting_only.
                if (isContainer)
                {
                    return;
                }

                Accumulate(pendingFormattingOnly, coveringSymbol.SymbolId, basePath, targetPath);
                return;
            }

            Accumulate(pendingBodyChanged, coveringSymbol.SymbolId, basePath, targetPath);
        }

        foreach (var (basePath, targetPath) in filePairs)
        {
            var baseText = baseSources[basePath].Text;
            var targetText = targetSources[targetPath].Text;
            var baseFileLines = DiffSnapshotReader.AllLines(baseText);
            var targetFileLines = DiffSnapshotReader.AllLines(targetText);
            var edits = LineHunkBuilder.Diff(
                baseFileLines.Select(line => line.Text).ToArray(),
                targetFileLines.Select(line => line.Text).ToArray(),
                budget.Options.MaxLinesForLineDiff);

            if (edits is null)
            {
                // X2: the whole-file edit distance exceeded the line-diff cap (an unusually large rewrite).
                // This must be flagged regardless of whether some ordinary entry already mentions the file —
                // an existing entry (e.g. a fingerprint-only per-symbol entry for one huge method) says
                // nothing about whether OTHER, smaller changes elsewhere in the same file were missed. The
                // coverage downgrade to Partial/Truncated is reserved for the case where nothing at all
                // represents this file, so a fully-covered-by-ordinary-entries file isn't wrongly demoted.
                limitations.Add(UnattributedChangeBudgetLimitation);
                var hasEntryForPath = existingChanges.Any(change =>
                    HasLocationForPath(change.Contract, basePath, pathComparer) || HasLocationForPath(change.Contract, targetPath, pathComparer));
                if (!hasEntryForPath)
                {
                    coverageTruncated = true;
                }
                continue;
            }

            foreach (var (start, end) in ChangeRuns(edits))
            {
                var deletes = new List<LineHunkBuilder.Edit>();
                var inserts = new List<LineHunkBuilder.Edit>();
                for (var i = start; i <= end; i++)
                {
                    if (edits[i].Kind == LineHunkBuilder.EditKind.Delete)
                    {
                        deletes.Add(edits[i]);
                    }
                    else
                    {
                        inserts.Add(edits[i]);
                    }
                }

                // X1: pair the k-th deleted line with the k-th inserted line by position within the run
                // (Myers already emits deletes before inserts within one run), not the run as a whole.
                var pairCount = Math.Min(deletes.Count, inserts.Count);
                for (var k = 0; k < pairCount; k++)
                {
                    ProcessUnit(basePath, targetPath, baseFileLines, targetFileLines, deletes[k], inserts[k]);
                }
                for (var k = pairCount; k < deletes.Count; k++)
                {
                    ProcessUnit(basePath, targetPath, baseFileLines, targetFileLines, deletes[k], null);
                }
                for (var k = pairCount; k < inserts.Count; k++)
                {
                    ProcessUnit(basePath, targetPath, baseFileLines, targetFileLines, null, inserts[k]);
                }
            }
        }

        var changes = new List<SymbolDiffChange>();
        var symbolIds = pendingBodyChanged.Keys.Concat(pendingFormattingOnly.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var symbolId in symbolIds)
        {
            var baseSymbolView = baseView.GetValueOrDefault(symbolId);
            var targetSymbolView = targetView.GetValueOrDefault(symbolId);
            if (baseSymbolView is null && targetSymbolView is null)
            {
                continue;
            }

            DeclarationView? FindDeclaration(SymbolView? view, string path) =>
                view?.Declarations.FirstOrDefault(declaration =>
                    declaration.Location.Path is not null && pathComparer.Equals(DiffSnapshotReader.NormalizePath(declaration.Location.Path), path));

            if (pendingBodyChanged.TryGetValue(symbolId, out var bodyPairs) && bodyPairs.Count > 0)
            {
                var kind = baseSymbolView is not null && targetSymbolView is not null ? DiffKind.BodyChanged
                    : baseSymbolView is not null ? DiffKind.Deleted
                    : DiffKind.Added;
                var evidences = bodyPairs.OrderBy(pair => pair.BasePath, StringComparer.Ordinal).ThenBy(pair => pair.TargetPath, StringComparer.Ordinal)
                    .Select(pair => BuildUnattributedLineHunkEvidence(
                        FindDeclaration(baseSymbolView, pair.BasePath), FindDeclaration(targetSymbolView, pair.TargetPath), budget))
                    .ToList();
                changes.Add(BuildChange(kind, baseSymbolView, targetSymbolView, 1m, evidences));
            }
            else if (pendingFormattingOnly.TryGetValue(symbolId, out var formattingPairs) && formattingPairs.Count > 0)
            {
                var evidences = formattingPairs.OrderBy(pair => pair.BasePath, StringComparer.Ordinal).ThenBy(pair => pair.TargetPath, StringComparer.Ordinal)
                    .Select(pair => BuildUnattributedFingerprintEvidence(
                        FindDeclaration(baseSymbolView, pair.BasePath), FindDeclaration(targetSymbolView, pair.TargetPath)))
                    .ToList();
                changes.Add(BuildChange(DiffKind.FormattingOnly, baseSymbolView, targetSymbolView, 1m, evidences));
            }
        }

        if (changes.Count > 0)
        {
            limitations.Add(ChangedLineAttributedLimitation);
        }

        return new SafetyNetResult(changes, limitations, coverageTruncated);
    }

    private static void Accumulate(
        Dictionary<string, HashSet<(string BasePath, string TargetPath)>> bucket, string symbolId, string basePath, string targetPath)
    {
        if (!bucket.TryGetValue(symbolId, out var pairs))
        {
            pairs = [];
            bucket[symbolId] = pairs;
        }

        pairs.Add((basePath, targetPath));
    }

    /// <summary>
    /// A delete/insert pair (or a lone unpaired delete or insert) is whitespace-only when, after the same
    /// whitespace normalization used elsewhere for formatting_only classification, the two sides read the
    /// same — or, for an unpaired line (pure blank-line insertion/deletion), when that single line is blank.
    /// </summary>
    private static bool IsWhitespaceOnlyChange(string? baseText, string? targetText)
    {
        if (baseText is not null && targetText is not null)
        {
            return NormalizeWhitespace(baseText) == NormalizeWhitespace(targetText);
        }

        var only = baseText ?? targetText;
        return only is not null && NormalizeWhitespace(only).Length == 0;
    }

    /// <summary>
    /// X3: pairs each base-only source path with the target-only path that shares the most symbol IDs (a
    /// file rename/move keeps most of its symbols' deterministic IDs, since those do not depend on path).
    /// Each target-only path is used for at most one match; ties break on the ordinally smallest target
    /// path. A base-only path with no overlapping target-only path is left unmatched (existing behavior:
    /// out of scope for this safety net, same as any other file-level-only change).
    /// </summary>
    private static IReadOnlyList<(string BasePath, string TargetPath)> FindRenamedFilePairs(
        SymbolDiffSnapshot baseSnapshot,
        SymbolDiffSnapshot targetSnapshot,
        IReadOnlyDictionary<string, DiffSourceDocument> baseSourcesByPath,
        IReadOnlyDictionary<string, DiffSourceDocument> targetSourcesByPath,
        StringComparer pathComparer)
    {
        var baseOnlyPaths = baseSourcesByPath.Keys.Except(targetSourcesByPath.Keys, pathComparer).Order(StringComparer.Ordinal).ToArray();
        if (baseOnlyPaths.Length == 0)
        {
            return [];
        }

        var targetOnlyPaths = new List<string>(targetSourcesByPath.Keys.Except(baseSourcesByPath.Keys, pathComparer).Order(StringComparer.Ordinal));
        if (targetOnlyPaths.Count == 0)
        {
            return [];
        }

        var baseSymbolsByPath = SymbolIdsByPath(baseSnapshot, pathComparer);
        var targetSymbolsByPath = SymbolIdsByPath(targetSnapshot, pathComparer);

        var pairs = new List<(string BasePath, string TargetPath)>();
        foreach (var basePath in baseOnlyPaths)
        {
            if (!baseSymbolsByPath.TryGetValue(basePath, out var baseSymbolIds) || baseSymbolIds.Count == 0)
            {
                continue;
            }

            string? bestTarget = null;
            var bestOverlap = 0;
            foreach (var targetPath in targetOnlyPaths)
            {
                if (!targetSymbolsByPath.TryGetValue(targetPath, out var targetSymbolIds))
                {
                    continue;
                }

                var overlap = baseSymbolIds.Count(targetSymbolIds.Contains);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestTarget = targetPath;
                }
            }

            if (bestTarget is not null)
            {
                pairs.Add((basePath, bestTarget));
                targetOnlyPaths.Remove(bestTarget);
            }
        }

        return pairs;
    }

    private static Dictionary<string, HashSet<string>> SymbolIdsByPath(SymbolDiffSnapshot snapshot, StringComparer pathComparer)
    {
        var result = new Dictionary<string, HashSet<string>>(pathComparer);
        foreach (var symbol in snapshot.Symbols)
        {
            foreach (var declaration in symbol.Declarations)
            {
                if (declaration.Location.Path is null)
                {
                    continue;
                }

                var path = DiffSnapshotReader.NormalizePath(declaration.Location.Path);
                if (!result.TryGetValue(path, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    result[path] = ids;
                }

                ids.Add(symbol.SymbolId);
            }
        }

        return result;
    }

    private static Dictionary<string, HashSet<int>> AttributedLines(
        IReadOnlyList<SymbolDiffChange> changes, bool useBase, IReadOnlyDictionary<string, SymbolView> view, StringComparer pathComparer)
    {
        var result = new Dictionary<string, HashSet<int>>(pathComparer);
        foreach (var change in changes)
        {
            var symbol = useBase ? change.BaseSymbol : change.TargetSymbol;
            if (symbol is null || !view.TryGetValue(symbol.SymbolId, out var symbolView))
            {
                continue;
            }

            foreach (var declaration in symbolView.Declarations)
            {
                if (declaration.Location.Path is null)
                {
                    continue;
                }

                var path = DiffSnapshotReader.NormalizePath(declaration.Location.Path);
                if (!result.TryGetValue(path, out var lines))
                {
                    lines = [];
                    result[path] = lines;
                }

                foreach (var line in declaration.Lines)
                {
                    lines.Add(line.LineNumber);
                }
            }
        }

        return result;
    }

    private static List<(int Start, int End)> ChangeRuns(IReadOnlyList<LineHunkBuilder.Edit> edits)
    {
        var runs = new List<(int, int)>();
        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == LineHunkBuilder.EditKind.Equal)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < edits.Count && edits[i].Kind != LineHunkBuilder.EditKind.Equal)
            {
                i++;
            }

            runs.Add((start, i - 1));
        }

        return runs;
    }

    /// <summary>
    /// The innermost (most deeply nested, by containerId chain length) indexed symbol whose declaration
    /// covers <paramref name="line"/> in <paramref name="path"/>; ties (symbols at the same nesting depth,
    /// e.g. sibling enum members sharing one line) break on the leftmost declaration span start, so the
    /// choice is deterministic.
    /// </summary>
    private static SymbolContract? FindCoveringSymbol(
        SymbolDiffSnapshot snapshot, IReadOnlyDictionary<string, SymbolContract> bySymbolId, string path, int line, StringComparer pathComparer)
    {
        SymbolContract? best = null;
        var bestDepth = -1;
        var bestStart = int.MaxValue;
        foreach (var symbol in snapshot.Symbols)
        {
            foreach (var declaration in symbol.Declarations)
            {
                if (declaration.Location.Path is null || !pathComparer.Equals(DiffSnapshotReader.NormalizePath(declaration.Location.Path), path))
                {
                    continue;
                }

                if (line < declaration.Location.Span.StartLine || line > declaration.Location.Span.EndLine)
                {
                    continue;
                }

                var depth = SymbolDepth(symbol, bySymbolId);
                if (depth > bestDepth || (depth == bestDepth && declaration.Location.Span.Start < bestStart))
                {
                    best = symbol;
                    bestDepth = depth;
                    bestStart = declaration.Location.Span.Start;
                }
            }
        }

        return best;
    }

    private static int SymbolDepth(SymbolContract symbol, IReadOnlyDictionary<string, SymbolContract> bySymbolId)
    {
        var depth = 0;
        var current = symbol;
        var guard = 0;
        while (current.ContainerId is not null && bySymbolId.TryGetValue(current.ContainerId, out var container) && guard++ < 64)
        {
            depth++;
            current = container;
        }

        return depth;
    }

    private static bool HasLocationForPath(DiffEntryContract entry, string normalizedPath, StringComparer pathComparer) =>
        entry.BaseLocations.Concat(entry.TargetLocations)
            .Any(location => location.Path is not null && pathComparer.Equals(DiffSnapshotReader.NormalizePath(location.Path), normalizedPath));

    private static DiffEvidenceContract BuildUnattributedLineHunkEvidence(
        DeclarationView? baseDeclaration, DeclarationView? targetDeclaration, EvidenceBudget budget)
    {
        var baseLines = baseDeclaration?.Lines ?? [];
        var targetLines = targetDeclaration?.Lines ?? [];
        var baseHash = baseDeclaration is null ? null : DiffDigests.Utf8(baseDeclaration.RawText);
        var targetHash = targetDeclaration is null ? null : DiffDigests.Utf8(targetDeclaration.RawText);
        return BuildLineHunkFromLines(
            UnattributedLineChangedEvidenceKind, baseLines, targetLines,
            baseDeclaration?.Location.Path, targetDeclaration?.Location.Path, baseHash, targetHash, budget);
    }

    /// <summary>X4: a whitespace-only (formatting_only) safety-net attribution reports only a fingerprint —
    /// there is no substantive text difference worth rendering as a hunk.</summary>
    private static DiffEvidenceContract BuildUnattributedFingerprintEvidence(DeclarationView? baseDeclaration, DeclarationView? targetDeclaration)
    {
        var baseHash = baseDeclaration is null ? null : DiffDigests.Utf8(baseDeclaration.RawText);
        var targetHash = targetDeclaration is null ? null : DiffDigests.Utf8(targetDeclaration.RawText);
        return new DiffEvidenceContract(UnattributedLineChangedEvidenceKind, DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false, baseHash, targetHash);
    }

    // ----------------------------------------------------------------------------------------------------

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

    private static IReadOnlyDictionary<string, SymbolView> CreateView(
        SymbolDiffSnapshot snapshot, IReadOnlyDictionary<string, SymbolContract[]> membersByContainer)
    {
        var sources = DiffSnapshotReader.DecodeSources(snapshot);
        var remarksBySymbol = snapshot.Remarks.GroupBy(remark => remark.SymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Location.Path, StringComparer.Ordinal)
                    .ThenBy(item => item.Location.Span.Start).ToArray(),
                StringComparer.Ordinal);

        var views = new Dictionary<string, SymbolView>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.Symbols)
        {
            var isContainer = membersByContainer.ContainsKey(symbol.SymbolId);
            var orderedDeclarations = symbol.Declarations
                .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Location.Span.Start)
                .ToArray();

            var declarationViews = new List<DeclarationView>();
            foreach (var declaration in orderedDeclarations)
            {
                var location = declaration.Location;
                var rawText = DiffSnapshotReader.Extract(location, sources);
                var sourceText = sources[DiffSnapshotReader.NormalizePath(location.Path!)].Text;
                List<(int StartLine, int EndLine)>? excluded = null;
                if (isContainer && membersByContainer.TryGetValue(symbol.SymbolId, out var members))
                {
                    excluded = members
                        .SelectMany(member => member.Declarations)
                        .Where(memberDeclaration => memberDeclaration.Location.Path is not null &&
                            string.Equals(DiffSnapshotReader.NormalizePath(memberDeclaration.Location.Path), DiffSnapshotReader.NormalizePath(location.Path!),
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        .Select(memberDeclaration => (memberDeclaration.Location.Span.StartLine, memberDeclaration.Location.Span.EndLine))
                        .ToList();
                }

                var lines = DiffSnapshotReader.FullLines(sourceText, location.Span, excluded);
                var headerLines = DiffSnapshotReader.HeaderLines(sourceText, location.Span);
                declarationViews.Add(new DeclarationView(location, rawText, lines, headerLines));
            }

            var remark = remarksBySymbol.TryGetValue(symbol.SymbolId, out var remarkLocations)
                ? string.Join("\n", remarkLocations.Select(item => DiffSnapshotReader.Extract(item.Location, sources)))
                : null;
            IReadOnlyList<LineHunkBuilder.SourceLine> remarkLines = [];
            string? remarkPath = null;
            var remarkMultiFile = false;
            if (remarkLocations is { Length: > 0 })
            {
                var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var distinctPaths = remarkLocations.Select(item => item.Location.Path!).Distinct(pathComparer).ToArray();
                // Multiple remark locations normally come from partial declarations. If they land in
                // different files, a single "--- a/<path>" hunk header can only ever name one of them, so
                // (M3) render this comparison as fingerprint-only rather than misattributing every line to
                // whichever file happened to sort first.
                remarkMultiFile = distinctPaths.Length > 1;
                remarkPath = distinctPaths[0];
                remarkLines = remarkLocations
                    .SelectMany(item => DiffSnapshotReader.FullLines(sources[DiffSnapshotReader.NormalizePath(item.Location.Path!)].Text, item.Location.Span))
                    .ToArray();
            }

            var compareText = string.Join("\n// --- partial declaration ---\n",
                declarationViews.Select(view => string.Join("\n", view.Lines.Select(line => line.Text))));
            var fullText = string.Join("\n// --- partial declaration ---\n", declarationViews.Select(view => view.RawText));
            var primaryPath = orderedDeclarations[0].Location.Path ?? orderedDeclarations[0].Location.Uri ?? "unknown";

            views[symbol.SymbolId] = new SymbolView(
                symbol, declarationViews, compareText, fullText, remark, remarkLines, remarkPath, remarkMultiFile, isContainer, primaryPath);
        }

        return views;
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
        _ = CreateView(snapshot, IndexContainers(snapshot).MembersByContainer);
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
        // keep using their self text (CompareText, members' line ranges excluded) as before M2 (N2): a
        // container's FullText includes every member's full text, so renaming the type while also editing a
        // member's body would otherwise change the shape twice over and wrongly break the rename match.
        var source = (view.IsContainer ? view.CompareText : view.FullText).Replace(view.Symbol.Name, "<name>", StringComparison.Ordinal);
        return NormalizeWhitespace(signature + "\n" + source);
    }

    private static string NormalizeWhitespace(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string SortKey(SymbolContract symbol) =>
        $"{symbol.ProjectId}\n{symbol.QualifiedName}\n{symbol.Kind}\n{symbol.Signature}\n{symbol.SymbolId}";

    private sealed record DeclarationView(
        LocationContract Location,
        string RawText,
        IReadOnlyList<LineHunkBuilder.SourceLine> Lines,
        IReadOnlyList<LineHunkBuilder.SourceLine> HeaderLines);

    private sealed record SymbolView(
        SymbolContract Symbol,
        IReadOnlyList<DeclarationView> Declarations,
        string CompareText,
        string FullText,
        string? Remark,
        IReadOnlyList<LineHunkBuilder.SourceLine> RemarkLines,
        string? RemarkPath,
        bool RemarkMultiFile,
        bool IsContainer,
        string PrimaryPath);

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
