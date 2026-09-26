using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Diff;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Snapshots;
using CodeVirtualize.Core.Vcs;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Integration.Tests.Diff;

internal static class DiffTests
{
    private const string SourcePath = "ReviewTarget.cs";
    private const string AnalysisKey = "analysis-001";

    public static void Run()
    {
        using var fixture = new GitDiffFixture();
        var provider = new GitBaselineProvider();
        var service = new SymbolDiffService();

        fixture.Write(FixtureSource("base", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false));
        fixture.CommitAndTag("base");
        var baseModel = Model(fixture.Read(), "base", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false);
        var vcsBase = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "base", Coverage(), baseModel.Symbols, baseModel.Remarks));

        fixture.Write(FixtureSource("dirty-before-session", "Dirty remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false));
        var dirtySource = fixture.Read();
        var dirtyModel = Model(dirtySource, "dirty-before-session", "Dirty remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false);
        var snapshotStorePath = Path.Combine(fixture.RootPath, "snapshot-store");
        new SessionSnapshotStore().Capture(new SessionSnapshotRequest(
            fixture.RepositoryPath,
            snapshotStorePath,
            "session-001",
            "gen-dirty-start",
            AnalysisKey,
            HashUtf8(dirtySource),
            [new SessionSnapshotSource(SourcePath, HashUtf8(dirtySource), "utf-8", ["project-001"])],
            new Dictionary<string, string>()));
        var sessionBase = new SessionBaselineProvider().Capture(new SessionSnapshotDiffRequest(
            snapshotStorePath, "session-001", Coverage(), dirtyModel.Symbols, dirtyModel.Remarks));

        fixture.Write(FixtureSource("dirty-before-session", "Current remark", "current", includeRemoved: false, "string", "RenamedCandidate", includeAdded: true));
        var currentSource = fixture.Read();
        var currentModel = Model(currentSource, "dirty-before-session", "Current remark", "current", includeRemoved: false, "string", "RenamedCandidate", includeAdded: true);
        var current = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, Coverage(), currentModel.Symbols, currentModel.Remarks));

        var vcsRequest = new SymbolDiffRequest(provider.CreateBaseline(vcsBase, current), vcsBase, current);
        var sessionRequest = new SymbolDiffRequest(
            SessionBaselineProvider.CreateBaseline(sessionBase, current, "session-001"), sessionBase, current);
        var vcsResult = service.Compare(vcsRequest);
        var sessionResult = service.Compare(sessionRequest);

        Assert(vcsResult.Contract.Baseline.Kind == BaselineKind.Vcs, "VCS diff must preserve its explicit baseline kind.");
        Assert(sessionResult.Contract.Baseline.Kind == BaselineKind.Session, "Session diff must preserve its explicit baseline kind.");
        Assert(Has(vcsResult, DiffKind.BodyChanged, "Existing"), "VCS diff must include changes already dirty when the session started.");
        Assert(!Has(sessionResult, DiffKind.BodyChanged, "Existing"), "Session diff must exclude changes already present in the captured dirty start.");
        Assert(Has(sessionResult, DiffKind.Added, "AddedDuringSession"), "Session diff must include symbols added during the session.");
        Assert(Has(sessionResult, DiffKind.Deleted, "Removed"), "Session diff must include removed symbols.");
        Assert(Has(sessionResult, DiffKind.SignatureChanged, "Signature"), "Signature changes must be paired conservatively.");
        Assert(Has(sessionResult, DiffKind.BodyChanged, "Body"), "Body changes must be distinct from dirty-start changes.");
        Assert(Has(sessionResult, DiffKind.RemarkChanged, "Existing"), "Remark changes must be explicit.");
        Assert(Has(sessionResult, DiffKind.RenameCandidate, "RenameMe"), "Uncertain rename must be a candidate rather than a confirmed rename.");

        var deleted = sessionResult.Changes.Single(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.Name == "Removed");
        var deletedEvidence = deleted.Contract.Evidence.Single();
        Assert(deletedEvidence.TextMode == DiffEvidenceTextMode.HeaderOnly &&
               deletedEvidence.TextualHunk!.Contains("Removed", StringComparison.Ordinal) &&
               deletedEvidence.BaseContentHash is not null && deletedEvidence.TargetContentHash is null,
            "A deleted symbol must report header_only evidence with a base content hash and no eagerly materialized full source.");

        var deletedSource = new DiffSourceResolver().Resolve(new DiffSourceRequest(
            sessionResult.SelectionKey, sessionResult.Contract.Baseline, sessionBase, current.InputFingerprint,
            DiffSide.Base, deleted.BaseSymbol!.SymbolId, SourcePart.Declaration, new SourceBudgetContract(65536, 400)));
        Assert(deletedSource.Source?.Content.Contains("gone", StringComparison.Ordinal) == true &&
               deletedSource.Source.ContentHash == deletedEvidence.BaseContentHash,
            "Lazily resolving the base side of a deleted symbol must return validated base source that hashes to the evidence's base content hash, without reading current source as a substitute.");

        Assert(sessionResult.Changes.All(change => change.Contract.Evidence.All(evidence =>
                evidence.TextMode == DiffEvidenceTextMode.FingerprintOnly || !string.IsNullOrWhiteSpace(evidence.TextualHunk))),
            "Every non-fingerprint evidence item must carry textual hunk evidence.");

        var gitText = provider.ReadTextualDiff(fixture.RepositoryPath, "base", null, SourcePath);
        Assert(gitText.Contains("dirty-before-session", StringComparison.Ordinal) && gitText.Contains("AddedDuringSession", StringComparison.Ordinal),
            "Read-only Git diff must include dirty-start and during-session working-tree changes.");

        ContractRoundTripAndUnknownKind(vcsResult.Contract);
        ErrorCases(provider, service, fixture, baseModel, current, currentModel);
        ModeSwitchRejectsStaleResponse(vcsRequest, vcsResult, sessionRequest, sessionResult).GetAwaiter().GetResult();
        LineHunksUseRealFileLineNumbers(sessionResult);
        AddedAndDeletedOmitBodyLines(sessionResult);
        LazyResolveRejectsMismatchedSnapshotAndSelectionKey(sessionResult, sessionBase, current);
        ContainerCascadeSuppressionAndMemberOrder();
        EvidenceBudgetTruncatesExplicitly();
        CrlfSnapshotHunkUsesRealLineNumbers();
        RealAnalyzerContainerSuppressionEndToEnd();
        OverBudgetSingleLineDowngradesToFingerprint();
        NearTotalRewriteDowngradesInsteadOfExhaustingMemory();
        EnumMembersSharingALineAreNotFalselyFlagged();
        FieldDeclaratorsSharingALineAreComparedIndependently();
        ContainerSelfTextGapProducesSeparateHunks();
        RemarkAcrossMultipleFilesDegradesToFingerprint();
        PartialDeclarationsPairByPathNotIndex();
        EolOnlyChangeIsReportedAsFormattingOnly();
        PureInsertionUsesPrecedingLineAnchor();
        AttributeOnSameLineAsFieldIsNotSilentlyDropped();
        TrailingCommentOnSameLineAsFieldIsNotSilentlyDropped();
        AttributeOnSeparateLineIsAttributedToCoveringSymbol();
        FileLineDiffBudgetExceededWithNoEntriesIsExplicit();
        RealAnalyzerAttributeOnSameLineEndToEnd();
        ContainerRenameCandidateSurvivesMemberBodyEdit();
        AdjacentFieldEditsAreBothAttributed();
        FileLineDiffBudgetLimitationAppearsEvenWithExistingEntries();
        RenamedFileAttributeChangeIsStillAttributed();
        BlankLineDeletionBetweenMembersIsNotReportedAsContainerChange();
        FieldReindentationIsReportedAsFormattingOnly();
        OwnedTextRegressionsWithRealAnalyzer();
        OwnedTextCoveragePropertyTest();
        LargeContainerDiffStaysNearLinear();
    }

    /// <summary>
    /// TASK-026 follow-up (container-id population): unlike <see cref="ContainerCascadeSuppressionAndMemberOrder"/>,
    /// which hand-sets <c>containerId</c> to exercise the Core suppression rule in isolation, this test runs
    /// the real C# analyzer (<see cref="CSharpIndexBuilder"/>) end to end so the containerId it now populates
    /// (CSharpSymbolExtractor) is what actually drives <see cref="SymbolDiffService"/>'s container-cascade
    /// suppression (U2) in production, not a test double.
    /// </summary>
    private static void RealAnalyzerContainerSuppressionEndToEnd()
    {
        using var fixture = new GitDiffFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "RealAnalyzer.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        var provider = new GitBaselineProvider();
        var service = new SymbolDiffService();
        var store = Path.Combine(fixture.RootPath, "real-analyzer-store");

        fixture.Write(RealAnalyzerFixtureSource("first pass", "one"));
        fixture.CommitAndTag("real-analyzer-base");
        var baseBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(fixture.RepositoryPath, store));
        var containerType = baseBuild.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "RealAnalyzerTarget");
        var member = baseBuild.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Value");
        Assert(member.ContainerId == containerType.SymbolId,
            "Sanity: the real analyzer must populate containerId for this fixture before the diff assertion is meaningful.");
        var baseSnapshot = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "real-analyzer-base", baseBuild.Manifest.Coverage, baseBuild.Symbols, []));

        // Member-only edit: only the method body text changes. The class's own text (namespace/braces/comment,
        // i.e. everything outside the member's own declaration span) is byte-identical to the base revision.
        fixture.Write(RealAnalyzerFixtureSource("first pass", "two"));
        var targetBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(fixture.RepositoryPath, store));
        var targetMember = targetBuild.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Value");
        Assert(targetMember.SymbolId == member.SymbolId, "Editing only the return expression must not change the method's deterministic identity.");
        var targetSnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, targetBuild.Manifest.Coverage, targetBuild.Symbols, []));

        var result = service.Compare(new SymbolDiffRequest(provider.CreateBaseline(baseSnapshot, targetSnapshot), baseSnapshot, targetSnapshot));

        Assert(result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.SymbolId == member.SymbolId),
            "The member's own body change must still be reported.");
        Assert(!result.Changes.Any(change => change.Kind is DiffKind.BodyChanged or DiffKind.FormattingOnly &&
                (change.BaseSymbol?.SymbolId == containerType.SymbolId || change.TargetSymbol?.SymbolId == containerType.SymbolId)),
            "With containerId populated by the real analyzer (not a hand-set test double), a member-only edit must not cascade into a containing-type entry end to end (U2).");
    }

    private static string RealAnalyzerFixtureSource(string comment, string memberValue) => string.Join("\n",
        "namespace Fixture.RealAnalyzer;",
        string.Empty,
        "public class RealAnalyzerTarget",
        "{",
        $"    // {comment}",
        $"    public string Value() => \"{memberValue}\";",
        "}",
        string.Empty);

    private static void LineHunksUseRealFileLineNumbers(SymbolDiffResult sessionResult)
    {
        var body = sessionResult.Changes.Single(change => change.Kind == DiffKind.BodyChanged && change.TargetSymbol?.Name == "Body");
        var evidence = body.Contract.Evidence.Single(item => item.Kind == "body-changed");
        Assert(evidence.TextMode == DiffEvidenceTextMode.LineHunks, "A body change must default to line_hunks evidence.");
        // "Body" is declared as a single expression-bodied line; the hunk header must cite that file's real
        // line number (from the fixture source), not a symbol-relative "1".
        Assert(!evidence.TextualHunk!.Contains("@@ -1,", StringComparison.Ordinal),
            "Line hunks must use real file line numbers, not symbol-relative numbering starting at 1.");
    }

    private static void AddedAndDeletedOmitBodyLines(SymbolDiffResult sessionResult)
    {
        var added = sessionResult.Changes.Single(change => change.Kind == DiffKind.Added && change.TargetSymbol?.Name == "AddedDuringSession");
        var addedEvidence = added.Contract.Evidence.Single();
        Assert(addedEvidence.TextMode == DiffEvidenceTextMode.HeaderOnly && addedEvidence.ContextLines == 0 &&
               addedEvidence.TargetContentHash is not null && addedEvidence.BaseContentHash is null,
            "An added symbol must report header_only evidence with only a target content hash.");

        var removed = sessionResult.Changes.Single(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.Name == "Removed");
        var removedEvidence = removed.Contract.Evidence.Single();
        Assert(removedEvidence.OmittedBaseLines == 0,
            $"The 'Removed' fixture symbol is a single expression-bodied line, so header_only must not omit any base line; got {removedEvidence.OmittedBaseLines}.");
    }

    private static void LazyResolveRejectsMismatchedSnapshotAndSelectionKey(
        SymbolDiffResult sessionResult, SymbolDiffSnapshot sessionBase, SymbolDiffSnapshot current)
    {
        var deleted = sessionResult.Changes.Single(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.Name == "Removed");
        var resolver = new DiffSourceResolver();
        var budget = new SourceBudgetContract(65536, 400);

        ThrowsDiff(() => resolver.Resolve(new DiffSourceRequest(
            sessionResult.SelectionKey, sessionResult.Contract.Baseline, current, sessionBase.InputFingerprint,
            DiffSide.Base, deleted.BaseSymbol!.SymbolId, SourcePart.Declaration, budget)), DiffErrorCodes.InvalidSnapshot);

        var bogusSelectionKey = $"sha256:{new string('0', 64)}";
        ThrowsDiff(() => resolver.Resolve(new DiffSourceRequest(
            bogusSelectionKey, sessionResult.Contract.Baseline, sessionBase, current.InputFingerprint,
            DiffSide.Base, deleted.BaseSymbol!.SymbolId, SourcePart.Declaration, budget)), DiffErrorCodes.StaleResponse);
    }

    private static void ContainerCascadeSuppressionAndMemberOrder()
    {
        using var fixture = new GitDiffFixture();
        var provider = new GitBaselineProvider();
        var service = new SymbolDiffService();

        fixture.Write(ContainerFixtureSource(commentBeforeSecond: "// first pass", memberOrder: false));
        fixture.CommitAndTag("container-base");
        var baseModel = ContainerModel(fixture.Read(), commentBeforeSecond: "// first pass", memberOrder: false);
        var baseSnapshot = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "container-base", Coverage(), baseModel.Symbols, baseModel.Remarks));

        // Only a member body changes; the container's own text (outside member spans) is untouched.
        fixture.Write(ContainerFixtureSource(commentBeforeSecond: "// first pass", memberOrder: false, firstValue: "changed"));
        var memberOnlyModel = ContainerModel(fixture.Read(), commentBeforeSecond: "// first pass", memberOrder: false, firstValue: "changed");
        var memberOnlySnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, Coverage(), memberOnlyModel.Symbols, memberOnlyModel.Remarks));
        var memberOnlyResult = service.Compare(new SymbolDiffRequest(
            provider.CreateBaseline(baseSnapshot, memberOnlySnapshot), baseSnapshot, memberOnlySnapshot));
        var containerSymbolId = baseModel.Symbols.Single(symbol => symbol.Kind == "class").SymbolId;
        Assert(!memberOnlyResult.Changes.Any(change => change.Kind is DiffKind.BodyChanged or DiffKind.FormattingOnly &&
                (change.BaseSymbol?.SymbolId == containerSymbolId || change.TargetSymbol?.SymbolId == containerSymbolId)),
            "A member-only body change must not cascade into a containing-type entry (D1/U2).");
        Assert(memberOnlyResult.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.TargetSymbol?.Name == "First"),
            "The member's own body change must still be reported.");

        // The container's own text changes (a comment between members), members unchanged.
        fixture.Write(ContainerFixtureSource(commentBeforeSecond: "// second pass", memberOrder: false));
        var commentModel = ContainerModel(fixture.Read(), commentBeforeSecond: "// second pass", memberOrder: false);
        var commentSnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, Coverage(), commentModel.Symbols, commentModel.Remarks));
        var commentResult = service.Compare(new SymbolDiffRequest(
            provider.CreateBaseline(baseSnapshot, commentSnapshot), baseSnapshot, commentSnapshot));
        var containerChange = commentResult.Changes.SingleOrDefault(change =>
            change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.SymbolId == containerSymbolId);
        Assert(containerChange is not null, "A comment change between members must be reported as the container's own body change (rule 4.4.2).");
        var containerEvidence = containerChange!.Contract.Evidence.Single(item => item.TextMode == DiffEvidenceTextMode.LineHunks);
        Assert(containerEvidence.TextualHunk!.Contains("second pass", StringComparison.Ordinal),
            "The container's self-text hunk must show the changed comment.");
        Assert(!commentResult.Changes.Any(change => change.TargetSymbol?.Name is "First" or "Second"),
            "Members whose own declarations did not change must not appear when only the container's self text changed.");

        // Only the declared order of the two members changes (positions swapped), member bodies untouched.
        fixture.Write(ContainerFixtureSource(commentBeforeSecond: "// first pass", memberOrder: true));
        var orderModel = ContainerModel(fixture.Read(), commentBeforeSecond: "// first pass", memberOrder: true);
        var orderSnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, Coverage(), orderModel.Symbols, orderModel.Remarks));
        var orderResult = service.Compare(new SymbolDiffRequest(
            provider.CreateBaseline(baseSnapshot, orderSnapshot), baseSnapshot, orderSnapshot));
        var orderChange = orderResult.Changes.SingleOrDefault(change =>
            change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.SymbolId == containerSymbolId);
        Assert(orderChange is not null, "A member reordering must be reported on the container.");
        var orderEvidence = orderChange!.Contract.Evidence.Single(item => item.Kind == "member-order-changed");
        Assert(orderEvidence.TextMode == DiffEvidenceTextMode.FingerprintOnly && orderEvidence.TextualHunk is null,
            "Member reordering must be reported as fingerprint-only evidence, not a full hunk.");
    }

    private static void EvidenceBudgetTruncatesExplicitly()
    {
        using var fixture = new GitDiffFixture();
        var provider = new GitBaselineProvider();
        var service = new SymbolDiffService();

        fixture.Write(FixtureSource("base", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false));
        fixture.CommitAndTag("budget-base");
        var baseModel = Model(fixture.Read(), "base", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false);
        var baseSnapshot = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "budget-base", Coverage(), baseModel.Symbols, baseModel.Remarks));

        fixture.Write(FixtureSource("changed", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false));
        var targetModel = Model(fixture.Read(), "changed", "Base remark", "start", includeRemoved: true, "int", "RenameMe", includeAdded: false);
        var targetSnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, Coverage(), targetModel.Symbols, targetModel.Remarks));

        var tinyBudget = new DiffEvidenceOptions(ContextLines: 1, MaxHunkBytesPerEntry: 8192, MaxEvidenceBytesTotal: 1, MaxLinesForLineDiff: 20000);
        var result = service.Compare(new SymbolDiffRequest(
            provider.CreateBaseline(baseSnapshot, targetSnapshot), baseSnapshot, targetSnapshot, Evidence: tinyBudget));

        Assert(result.Contract.EvidenceTruncated, "An exhausted total evidence budget must set evidenceTruncated on the diff contract.");
        Assert(result.Contract.Limitations.Contains(DiffContract.EvidenceBudgetExhaustedLimitation, StringComparer.Ordinal),
            "An exhausted total evidence budget must add the diff-evidence-budget-exhausted limitation.");
        Assert(result.Changes.Any(change => change.Contract.Evidence.Any(evidence =>
                evidence.Truncated && evidence.TextMode == DiffEvidenceTextMode.FingerprintOnly)),
            "Once the total evidence budget is exhausted, evidence must be explicitly downgraded to fingerprint_only rather than silently omitted.");
    }

    private static void CrlfSnapshotHunkUsesRealLineNumbers()
    {
        const string baseSource = "namespace Fixture.Crlf;\r\npublic static class CrlfTarget\r\n{\r\n    public static string Read()\r\n    {\r\n        return \"base\";\r\n    }\r\n}\r\n";
        const string targetSource = "namespace Fixture.Crlf;\r\npublic static class CrlfTarget\r\n{\r\n    public static string Read()\r\n    {\r\n        return \"changed\";\r\n    }\r\n}\r\n";
        var baseBytes = new UTF8Encoding(false).GetBytes(baseSource);
        var targetBytes = new UTF8Encoding(false).GetBytes(targetSource);
        var identity = new SymbolIdentityContract("crlf-project", AnalysisKey, "method", "Fixture.Crlf.CrlfTarget.Read", 0, [], null);
        var symbolId = DeterministicSymbolId.Create(identity);
        var declarationText = "public static string Read()\r\n    {\r\n        return \"base\";\r\n    }";
        var start = baseSource.IndexOf(declarationText, StringComparison.Ordinal);
        Assert(start >= 0, "CRLF fixture declaration text must be found in the base source.");
        var span = new TextSpanContract(start, declarationText.Length, LineAt(baseSource, start), LineAt(baseSource, start + declarationText.Length));
        var location = new LocationContract("crlf-file", HashUtf8Bytes(baseBytes), span, "Crlf.cs", null);
        var targetDeclarationText = declarationText.Replace("base", "changed", StringComparison.Ordinal);
        var targetStart = targetSource.IndexOf(targetDeclarationText, StringComparison.Ordinal);
        var targetSpan = new TextSpanContract(targetStart, targetDeclarationText.Length, LineAt(targetSource, targetStart), LineAt(targetSource, targetStart + targetDeclarationText.Length));
        var targetLocation = new LocationContract("crlf-file", HashUtf8Bytes(targetBytes), targetSpan, "Crlf.cs", null);

        var symbol = new SymbolContract(symbolId, "crlf-project", AnalysisKey, "method", "Read", "Fixture.Crlf.CrlfTarget.Read", "public static string Read()",
            "public", null, 0, IdentityQuality.Semantic, [], null, [new DeclarationContract(symbolId, location, DocumentKind.Source)], []);
        var targetSymbol = symbol with { Declarations = [new DeclarationContract(symbolId, targetLocation, DocumentKind.Source)] };

        var baseSnapshot = new SymbolDiffSnapshot("crlf-base", HashUtf8Bytes(baseBytes), DateTimeOffset.UtcNow, Coverage(),
            [symbol], [new DiffSourceDocument("Crlf.cs", baseBytes, "utf-8", HashUtf8Bytes(baseBytes))], []);
        var targetSnapshot = new SymbolDiffSnapshot("crlf-target", HashUtf8Bytes(targetBytes), DateTimeOffset.UtcNow, Coverage(),
            [targetSymbol], [new DiffSourceDocument("Crlf.cs", targetBytes, "utf-8", HashUtf8Bytes(targetBytes))], []);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "crlf-base", "crlf-target", null, DateTimeOffset.UtcNow, HashUtf8Bytes(baseBytes));

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        var change = result.Changes.Single(item => item.Kind == DiffKind.BodyChanged);
        var evidence = change.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.LineHunks, "A CRLF-encoded body change must still produce a line hunk.");
        Assert(evidence.TextualHunk!.Contains("@@ -5,3 +5,3 @@", StringComparison.Ordinal),
            "The hunk must cite the real (CRLF-counted) file line number of the changed 'return' line (line 5), not symbol-relative numbering.");
        Assert(evidence.TextualHunk.Contains("-        return \"base\";", StringComparison.Ordinal) &&
               evidence.TextualHunk.Contains("+        return \"changed\";", StringComparison.Ordinal),
            "The hunk must show full CRLF source lines without embedding raw \\r characters.");
        Assert(!evidence.TextualHunk.Contains("\r", StringComparison.Ordinal), "Rendered hunk lines must not retain the source's CRLF terminators.");
    }

    // --- TASK-028 review fixes (H1, M1, M2, M3, M4, L1, L2) ---------------------------------------------

    private static void OverBudgetSingleLineDowngradesToFingerprint()
    {
        // Both sides must individually exceed the per-entry budget: the trim loop shrinks a group from its
        // *last* edit first, so if only the target line were huge, trimming down to just the (small) base
        // delete line would "succeed" and mask the bug this test exists to catch.
        var baseHugeValue = new string('x', 9000);
        var targetHugeValue = new string('y', 9000);
        var baseSource = $"namespace Fixture.Budget;\npublic static class BudgetTarget\n{{\n    public const string Value = \"{baseHugeValue}\";\n}}\n";
        var targetSource = $"namespace Fixture.Budget;\npublic static class BudgetTarget\n{{\n    public const string Value = \"{targetHugeValue}\";\n}}\n";
        var baseDeclarationText = $"public const string Value = \"{baseHugeValue}\";";
        var targetDeclarationText = $"public const string Value = \"{targetHugeValue}\";";

        var baseSymbol = AdHocSymbol(baseSource, "Budget.cs", "budget-project", "field", "Value", "Fixture.Budget.BudgetTarget.Value", "public const string Value", baseDeclarationText);
        var targetSymbol = AdHocSymbol(targetSource, "Budget.cs", "budget-project", "field", "Value", "Fixture.Budget.BudgetTarget.Value", "public const string Value", targetDeclarationText);
        Assert(baseSymbol.SymbolId == targetSymbol.SymbolId, "Changing only the literal value must not change the field's identity.");

        var baseSnapshot = BuildSnapshot("h1-base", "Budget.cs", baseSource, [baseSymbol]);
        var targetSnapshot = BuildSnapshot("h1-target", "Budget.cs", targetSource, [targetSymbol]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "h1-base", "h1-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        var change = result.Changes.Single(item => item.Kind == DiffKind.BodyChanged);
        var evidence = change.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.FingerprintOnly && evidence.Truncated && evidence.TextualHunk is null,
            "A single line that alone exceeds the per-entry hunk byte budget must downgrade to fingerprint-only evidence instead of an empty hunk that fails contract validation (H1).");
        Assert(result.Contract.EvidenceTruncated && result.Contract.Limitations.Contains(DiffContract.EvidenceBudgetExhaustedLimitation, StringComparer.Ordinal),
            "The unfittable single-line hunk must surface as evidenceTruncated with the budget-exhausted limitation.");
    }

    private static void NearTotalRewriteDowngradesInsteadOfExhaustingMemory()
    {
        const int lineCount = 5000;
        var baseBody = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"        // base-only-line-{i}-aaaaaaaaaa"));
        var targetBody = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"        // target-only-line-{i}-bbbbbbbbbb"));
        var baseSource = $"namespace Fixture.Rewrite;\npublic static class RewriteTarget\n{{\n    public static void Big()\n    {{\n{baseBody}\n    }}\n}}\n";
        var targetSource = $"namespace Fixture.Rewrite;\npublic static class RewriteTarget\n{{\n    public static void Big()\n    {{\n{targetBody}\n    }}\n}}\n";
        var baseDeclarationText = $"public static void Big()\n    {{\n{baseBody}\n    }}";
        var targetDeclarationText = $"public static void Big()\n    {{\n{targetBody}\n    }}";

        var baseSymbol = AdHocSymbol(baseSource, "Rewrite.cs", "rewrite-project", "method", "Big", "Fixture.Rewrite.RewriteTarget.Big", "public static void Big()", baseDeclarationText);
        var targetSymbol = AdHocSymbol(targetSource, "Rewrite.cs", "rewrite-project", "method", "Big", "Fixture.Rewrite.RewriteTarget.Big", "public static void Big()", targetDeclarationText);

        var baseSnapshot = BuildSnapshot("m1-base", "Rewrite.cs", baseSource, [baseSymbol]);
        var targetSnapshot = BuildSnapshot("m1-target", "Rewrite.cs", targetSource, [targetSymbol]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "m1-base", "m1-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var stopwatch = Stopwatch.StartNew();
        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        stopwatch.Stop();

        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"A near-total rewrite (edit distance ~{2 * lineCount}, within MaxLinesForLineDiff) must abort the Myers computation via the internal trace-memory cap well before completing it, not spend unbounded time/memory on it; took {stopwatch.Elapsed}.");
        var change = result.Changes.Single(item => item.Kind == DiffKind.BodyChanged);
        var evidence = change.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.FingerprintOnly && evidence.Truncated,
            "A near-total rewrite whose edit distance exceeds the internal Myers trace budget must downgrade to fingerprint-only evidence (M1) instead of risking unbounded memory.");
    }

    private static void EnumMembersSharingALineAreNotFalselyFlagged()
    {
        var baseSource = "namespace Fixture.Shared;\npublic enum Color\n{\n    Red, Green, Blue\n}\n";
        var targetSource = "namespace Fixture.Shared;\npublic enum Color\n{\n    Red, Green, Blue, Yellow\n}\n";

        SymbolContract Member(string source, string name) =>
            AdHocSymbol(source, "Shared.cs", "shared-project", "enum_member", name, $"Fixture.Shared.Color.{name}", name, name);

        var baseRed = Member(baseSource, "Red");
        var baseGreen = Member(baseSource, "Green");
        var baseBlue = Member(baseSource, "Blue");
        var targetRed = Member(targetSource, "Red");
        var targetGreen = Member(targetSource, "Green");
        var targetBlue = Member(targetSource, "Blue");
        var targetYellow = Member(targetSource, "Yellow");
        Assert(baseRed.SymbolId == targetRed.SymbolId && baseGreen.SymbolId == targetGreen.SymbolId && baseBlue.SymbolId == targetBlue.SymbolId,
            "Sanity: members present in both revisions must share identity.");

        var baseSnapshot = BuildSnapshot("m2-enum-base", "Shared.cs", baseSource, [baseRed, baseGreen, baseBlue]);
        var targetSnapshot = BuildSnapshot("m2-enum-target", "Shared.cs", targetSource, [targetRed, targetGreen, targetBlue, targetYellow]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "m2-enum-base", "m2-enum-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count(change => change.Kind == DiffKind.Added && change.TargetSymbol?.Name == "Yellow") == 1,
            "Adding Yellow must be reported exactly once.");
        Assert(!result.Changes.Any(change => change.BaseSymbol?.Name is "Red" or "Green" or "Blue" || change.TargetSymbol?.Name is "Red" or "Green" or "Blue"),
            "Unrelated enum members sharing a physical line with the appended member must not be falsely reported as changed (M2).");
        Assert(result.Changes.Count == 1, $"Only the Added Yellow entry is expected; got {result.Changes.Count}.");
    }

    private static void FieldDeclaratorsSharingALineAreComparedIndependently()
    {
        var baseSource = "namespace Fixture.Shared;\npublic static class Fields\n{\n    public static int a = 1, b = 2;\n}\n";
        var targetSource = "namespace Fixture.Shared;\npublic static class Fields\n{\n    public static int a = 9, b = 2;\n}\n";

        var baseA = AdHocSymbol(baseSource, "Fields.cs", "fields-project", "field", "a", "Fixture.Shared.Fields.a", "public static int a", "a = 1");
        var baseB = AdHocSymbol(baseSource, "Fields.cs", "fields-project", "field", "b", "Fixture.Shared.Fields.b", "public static int b", "b = 2");
        var targetA = AdHocSymbol(targetSource, "Fields.cs", "fields-project", "field", "a", "Fixture.Shared.Fields.a", "public static int a", "a = 9");
        var targetB = AdHocSymbol(targetSource, "Fields.cs", "fields-project", "field", "b", "Fixture.Shared.Fields.b", "public static int b", "b = 2");
        Assert(baseB.SymbolId == targetB.SymbolId && baseA.SymbolId == targetA.SymbolId, "Sanity: field identities must be stable.");

        var baseSnapshot = BuildSnapshot("m2-fields-base", "Fields.cs", baseSource, [baseA, baseB]);
        var targetSnapshot = BuildSnapshot("m2-fields-target", "Fields.cs", targetSource, [targetA, targetB]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "m2-fields-base", "m2-fields-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.Name == "a") == 1,
            "The declarator whose own initializer changed must be reported.");
        Assert(!result.Changes.Any(change => change.BaseSymbol?.Name == "b" || change.TargetSymbol?.Name == "b"),
            "A field declarator sharing a physical line with an edited neighbor, but whose own text is unchanged, must not be falsely reported (M2).");
    }

    private static void ContainerSelfTextGapProducesSeparateHunks()
    {
        string ClassText(string leadingComment, string trailingComment) => string.Join("\n",
            "public static class GapTarget",
            "{",
            $"    // {leadingComment}",
            "    public static int A() => 1;",
            "    public static int B() => 1;",
            "    public static int C() => 1;",
            $"    // {trailingComment}",
            "}");

        var baseClassText = ClassText("leading v1", "trailing v1");
        var targetClassText = ClassText("leading v2", "trailing v2");
        var baseSource = "namespace Fixture.Gap;\n" + baseClassText + "\n";
        var targetSource = "namespace Fixture.Gap;\n" + targetClassText + "\n";

        var containerBase = AdHocSymbol(baseSource, "Gap.cs", "gap-project", "class", "GapTarget", "Fixture.Gap.GapTarget", "public static class GapTarget", baseClassText);
        var containerTarget = AdHocSymbol(targetSource, "Gap.cs", "gap-project", "class", "GapTarget", "Fixture.Gap.GapTarget", "public static class GapTarget", targetClassText);
        Assert(containerBase.SymbolId == containerTarget.SymbolId, "Sanity: container identity must be stable.");
        var containerId = containerBase.SymbolId;

        SymbolContract Member(string source, string name) => AdHocSymbol(
            source, "Gap.cs", "gap-project", "method", name, $"Fixture.Gap.GapTarget.{name}", $"public static int {name}()", $"public static int {name}() => 1;", containerId);

        var baseSnapshot = BuildSnapshot("m3-gap-base", "Gap.cs", baseSource,
            [containerBase, Member(baseSource, "A"), Member(baseSource, "B"), Member(baseSource, "C")]);
        var targetSnapshot = BuildSnapshot("m3-gap-target", "Gap.cs", targetSource,
            [containerTarget, Member(targetSource, "A"), Member(targetSource, "B"), Member(targetSource, "C")]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "m3-gap-base", "m3-gap-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        var change = result.Changes.Single(item => item.Kind == DiffKind.BodyChanged && item.BaseSymbol?.SymbolId == containerId);
        var evidence = change.Contract.Evidence.Single(item => item.TextMode == DiffEvidenceTextMode.LineHunks);
        var hunkCount = evidence.TextualHunk!.Split("@@ -", StringSplitOptions.None).Length - 1;
        Assert(hunkCount >= 2,
            $"A container self-text change spanning a line-number gap (excluded member lines A/B/C in between) must render as separate @@ hunks, not one hunk that falsely claims a contiguous range (M3); got {hunkCount} @@ block(s).\n{evidence.TextualHunk}");
        Assert(evidence.TextualHunk.Contains("leading v2", StringComparison.Ordinal) && evidence.TextualHunk.Contains("trailing v2", StringComparison.Ordinal),
            "Both sides of the gap must be represented in the evidence.");
        Assert(!result.Changes.Any(item => item.BaseSymbol?.Name is "A" or "B" or "C" || item.TargetSymbol?.Name is "A" or "B" or "C"),
            "Members whose own declarations did not change must not appear when only the container's self text changed.");
    }

    private static void RemarkAcrossMultipleFilesDegradesToFingerprint()
    {
        var fileASource = "namespace Fixture.Remark;\n/// <summary>A v1</summary>\npublic partial class RemarkTarget\n{\n}\n";
        var fileBSource = "namespace Fixture.Remark;\n/// <summary>B v1</summary>\npublic partial class RemarkTarget\n{\n    public static int X() => 1;\n}\n";
        var targetFileASource = "namespace Fixture.Remark;\n/// <summary>A v2</summary>\npublic partial class RemarkTarget\n{\n}\n";
        var targetFileBSource = fileBSource;

        var identity = new SymbolIdentityContract("remark-project", AnalysisKey, "class", "Fixture.Remark.RemarkTarget", 0, [], null);
        var symbolId = DeterministicSymbolId.Create(identity);

        var baseDeclA = FragmentLocation(fileASource, "A.cs", "public partial class RemarkTarget\n{\n}");
        var baseDeclB = FragmentLocation(fileBSource, "B.cs", "public partial class RemarkTarget\n{\n    public static int X() => 1;\n}");
        var baseRemarkA = FragmentLocation(fileASource, "A.cs", "/// <summary>A v1</summary>");
        var baseRemarkB = FragmentLocation(fileBSource, "B.cs", "/// <summary>B v1</summary>");

        var targetDeclA = FragmentLocation(targetFileASource, "A.cs", "public partial class RemarkTarget\n{\n}");
        var targetDeclB = FragmentLocation(targetFileBSource, "B.cs", "public partial class RemarkTarget\n{\n    public static int X() => 1;\n}");
        var targetRemarkA = FragmentLocation(targetFileASource, "A.cs", "/// <summary>A v2</summary>");
        var targetRemarkB = FragmentLocation(targetFileBSource, "B.cs", "/// <summary>B v1</summary>");

        var baseSymbol = new SymbolContract(symbolId, "remark-project", AnalysisKey, "class", "RemarkTarget", "Fixture.Remark.RemarkTarget", "public partial class RemarkTarget",
            "public", null, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(symbolId, baseDeclA, DocumentKind.Source), new DeclarationContract(symbolId, baseDeclB, DocumentKind.Source)], []);
        var targetSymbol = baseSymbol with
        {
            Declarations = [new DeclarationContract(symbolId, targetDeclA, DocumentKind.Source), new DeclarationContract(symbolId, targetDeclB, DocumentKind.Source)]
        };

        var baseBytesA = new UTF8Encoding(false).GetBytes(fileASource);
        var baseBytesB = new UTF8Encoding(false).GetBytes(fileBSource);
        var targetBytesA = new UTF8Encoding(false).GetBytes(targetFileASource);
        var targetBytesB = new UTF8Encoding(false).GetBytes(targetFileBSource);

        var baseSnapshot = new SymbolDiffSnapshot("m3-remark-base", HashUtf8Bytes(baseBytesA), DateTimeOffset.UtcNow, Coverage(), [baseSymbol],
            [new DiffSourceDocument("A.cs", baseBytesA, "utf-8", HashUtf8Bytes(baseBytesA)), new DiffSourceDocument("B.cs", baseBytesB, "utf-8", HashUtf8Bytes(baseBytesB))],
            [new DiffRemarkSnapshot(symbolId, baseRemarkA), new DiffRemarkSnapshot(symbolId, baseRemarkB)]);
        var targetSnapshot = new SymbolDiffSnapshot("m3-remark-target", HashUtf8Bytes(targetBytesA), DateTimeOffset.UtcNow, Coverage(), [targetSymbol],
            [new DiffSourceDocument("A.cs", targetBytesA, "utf-8", HashUtf8Bytes(targetBytesA)), new DiffSourceDocument("B.cs", targetBytesB, "utf-8", HashUtf8Bytes(targetBytesB))],
            [new DiffRemarkSnapshot(symbolId, targetRemarkA), new DiffRemarkSnapshot(symbolId, targetRemarkB)]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "m3-remark-base", "m3-remark-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        var remarkChange = result.Changes.Single(item => item.Kind == DiffKind.RemarkChanged);
        var evidence = remarkChange.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.FingerprintOnly && evidence.TextualHunk is null,
            "A remark spanning multiple files cannot be faithfully rendered as one unified-diff hunk (one '--- a/<path>' header), so it must degrade to fingerprint-only evidence (M3) instead of misattributing every line to whichever file's path sorts first.");
        Assert(evidence.BaseContentHash is not null && evidence.TargetContentHash is not null, "Fingerprint evidence must still carry both content hashes.");
    }

    private static void PartialDeclarationsPairByPathNotIndex()
    {
        var identity = new SymbolIdentityContract("partial-project", AnalysisKey, "class", "Fixture.PartialSwap.SwapTarget", 0, [], null);
        var symbolId = DeterministicSymbolId.Create(identity);

        var baseFileA = "namespace Fixture.PartialSwap;\npublic partial class SwapTarget\n{\n    // in A\n}\n";
        var baseFileB = "namespace Fixture.PartialSwap;\npublic partial class SwapTarget\n{\n    // in B\n}\n";
        var targetFileA = baseFileA;
        var targetFileC = "namespace Fixture.PartialSwap;\npublic partial class SwapTarget\n{\n    // in C\n}\n";

        var baseDeclA = FragmentLocation(baseFileA, "A.cs", "public partial class SwapTarget\n{\n    // in A\n}");
        var baseDeclB = FragmentLocation(baseFileB, "B.cs", "public partial class SwapTarget\n{\n    // in B\n}");
        var targetDeclA = FragmentLocation(targetFileA, "A.cs", "public partial class SwapTarget\n{\n    // in A\n}");
        var targetDeclC = FragmentLocation(targetFileC, "C.cs", "public partial class SwapTarget\n{\n    // in C\n}");

        var baseSymbol = new SymbolContract(symbolId, "partial-project", AnalysisKey, "class", "SwapTarget", "Fixture.PartialSwap.SwapTarget", "public partial class SwapTarget",
            "public", null, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(symbolId, baseDeclA, DocumentKind.Source), new DeclarationContract(symbolId, baseDeclB, DocumentKind.Source)], []);
        var targetSymbol = baseSymbol with
        {
            Declarations = [new DeclarationContract(symbolId, targetDeclA, DocumentKind.Source), new DeclarationContract(symbolId, targetDeclC, DocumentKind.Source)]
        };

        var baseBytesA = new UTF8Encoding(false).GetBytes(baseFileA);
        var baseBytesB = new UTF8Encoding(false).GetBytes(baseFileB);
        var targetBytesA = new UTF8Encoding(false).GetBytes(targetFileA);
        var targetBytesC = new UTF8Encoding(false).GetBytes(targetFileC);

        var baseSnapshot = new SymbolDiffSnapshot("m4-base", HashUtf8Bytes(baseBytesA), DateTimeOffset.UtcNow, Coverage(), [baseSymbol],
            [new DiffSourceDocument("A.cs", baseBytesA, "utf-8", HashUtf8Bytes(baseBytesA)), new DiffSourceDocument("B.cs", baseBytesB, "utf-8", HashUtf8Bytes(baseBytesB))], []);
        var targetSnapshot = new SymbolDiffSnapshot("m4-target", HashUtf8Bytes(targetBytesA), DateTimeOffset.UtcNow, Coverage(), [targetSymbol],
            [new DiffSourceDocument("A.cs", targetBytesA, "utf-8", HashUtf8Bytes(targetBytesA)), new DiffSourceDocument("C.cs", targetBytesC, "utf-8", HashUtf8Bytes(targetBytesC))], []);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "m4-base", "m4-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        var change = result.Changes.Single(item => item.BaseSymbol?.SymbolId == symbolId || item.TargetSymbol?.SymbolId == symbolId);

        // A.cs pairs with A.cs by path (unchanged, so no evidence). B.cs and C.cs are then the only unpaired
        // declarations, one on each side, so they are paired as a moved declaration (owned-text redesign) and
        // compared as B.cs -> C.cs; before the redesign they were reported as removed + added header_only.
        var evidence = change.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.LineHunks &&
               evidence.TextualHunk!.Contains("--- a/B.cs\n+++ b/C.cs\n", StringComparison.Ordinal) &&
               evidence.TextualHunk.Contains("+    // in C", StringComparison.Ordinal) &&
               !evidence.TextualHunk.Contains("in A", StringComparison.Ordinal),
            $"A.cs must be paired with A.cs by path (M4) and never compared by index; the single leftover B.cs/C.cs pair is a moved declaration.\n{evidence.TextualHunk}");

        // With more than one unpaired declaration on a side, nothing is guessed: removed/added header_only.
        var targetFileD = "namespace Fixture.PartialSwap;\npublic partial class SwapTarget\n{\n    // in D\n}\n";
        var targetDeclD = FragmentLocation(targetFileD, "D.cs", "public partial class SwapTarget\n{\n    // in D\n}");
        var targetBytesD = new UTF8Encoding(false).GetBytes(targetFileD);
        var threeWayTarget = new SymbolDiffSnapshot("m4-target-3", HashUtf8Bytes(targetBytesD), DateTimeOffset.UtcNow, Coverage(),
            [baseSymbol with
            {
                Declarations =
                [
                    new DeclarationContract(symbolId, targetDeclA, DocumentKind.Source),
                    new DeclarationContract(symbolId, targetDeclC, DocumentKind.Source),
                    new DeclarationContract(symbolId, targetDeclD, DocumentKind.Source)
                ]
            }],
            [
                new DiffSourceDocument("A.cs", targetBytesA, "utf-8", HashUtf8Bytes(targetBytesA)),
                new DiffSourceDocument("C.cs", targetBytesC, "utf-8", HashUtf8Bytes(targetBytesC)),
                new DiffSourceDocument("D.cs", targetBytesD, "utf-8", HashUtf8Bytes(targetBytesD))
            ], []);
        var threeWay = new SymbolDiffService().Compare(new SymbolDiffRequest(
            new BaselineContract(BaselineKind.Vcs, "git", "m4-base", "m4-target-3", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint),
            baseSnapshot, threeWayTarget));
        var threeWayChange = threeWay.Changes.Single(item => item.BaseSymbol?.SymbolId == symbolId);
        Assert(threeWayChange.Contract.Evidence.Count == 3 &&
               threeWayChange.Contract.Evidence.All(item => item.TextMode == DiffEvidenceTextMode.HeaderOnly) &&
               threeWayChange.Contract.Evidence.Count(item => item.Kind == "declaration-removed") == 1 &&
               threeWayChange.Contract.Evidence.Count(item => item.Kind == "declaration-added") == 2,
            "With one removed and two added partial declarations, B.cs must be reported removed and C.cs/D.cs added, never compared line by line (M4).");
    }

    private static void EolOnlyChangeIsReportedAsFormattingOnly()
    {
        var baseSource = "namespace Fixture.Eol;\r\npublic static class EolTarget\r\n{\r\n    public static int Value()\r\n    {\r\n        return 1;\r\n    }\r\n}\r\n";
        var targetSource = baseSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        var declarationTextBase = "public static int Value()\r\n    {\r\n        return 1;\r\n    }";
        var declarationTextTarget = declarationTextBase.Replace("\r\n", "\n", StringComparison.Ordinal);

        var baseSymbol = AdHocSymbol(baseSource, "Eol.cs", "eol-project", "method", "Value", "Fixture.Eol.EolTarget.Value", "public static int Value()", declarationTextBase);
        var targetSymbol = AdHocSymbol(targetSource, "Eol.cs", "eol-project", "method", "Value", "Fixture.Eol.EolTarget.Value", "public static int Value()", declarationTextTarget);
        Assert(baseSymbol.SymbolId == targetSymbol.SymbolId, "Sanity: identity must be stable across an EOL-only change.");

        var baseSnapshot = BuildSnapshot("l1-base", "Eol.cs", baseSource, [baseSymbol]);
        var targetSnapshot = BuildSnapshot("l1-target", "Eol.cs", targetSource, [targetSymbol]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "l1-base", "l1-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        var change = result.Changes.Single();
        Assert(change.Kind == DiffKind.FormattingOnly,
            "A CRLF-to-LF-only change must still be reported as formatting_only (L1), matching pre-TASK-026 behavior, not silently dropped.");
        var evidence = change.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.FingerprintOnly && evidence.BaseContentHash != evidence.TargetContentHash,
            "An EOL-only change has no meaningful line-level hunk (every line's EOL-stripped text is identical), so fingerprint evidence with differing hashes is sufficient (L1).");
    }

    private static void PureInsertionUsesPrecedingLineAnchor()
    {
        var baseSource = string.Join("\n",
            "namespace Fixture.Insert;", "public static class InsertTarget", "{", "    public static void M()",
            "    {", "        Step1();", "    }", "}", string.Empty);
        var targetSource = string.Join("\n",
            "namespace Fixture.Insert;", "public static class InsertTarget", "{", "    public static void M()",
            "    {", "        Step1();", "        Step2();", "    }", "}", string.Empty);

        var baseDeclarationText = "public static void M()\n    {\n        Step1();\n    }";
        var targetDeclarationText = "public static void M()\n    {\n        Step1();\n        Step2();\n    }";

        var baseSymbol = AdHocSymbol(baseSource, "Insert.cs", "insert-project", "method", "M", "Fixture.Insert.InsertTarget.M", "public static void M()", baseDeclarationText);
        var targetSymbol = AdHocSymbol(targetSource, "Insert.cs", "insert-project", "method", "M", "Fixture.Insert.InsertTarget.M", "public static void M()", targetDeclarationText);

        var baseSnapshot = BuildSnapshot("l2-base", "Insert.cs", baseSource, [baseSymbol]);
        var targetSnapshot = BuildSnapshot("l2-target", "Insert.cs", targetSource, [targetSymbol]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "l2-base", "l2-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var tinyContext = new DiffEvidenceOptions(ContextLines: 0, MaxHunkBytesPerEntry: 8192, MaxEvidenceBytesTotal: 65536, MaxLinesForLineDiff: 20000);
        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot, Evidence: tinyContext));
        var change = result.Changes.Single(item => item.Kind == DiffKind.BodyChanged);
        var evidence = change.Contract.Evidence.Single();

        // M() spans base lines 4-7 (header, {, Step1(), }). Step2() is inserted after base's Step1() line
        // (line 6). With contextLines=0, the pure-insertion hunk's base anchor must be "-6,0" (the line
        // preceding the insertion point), not "-0,0" (L2).
        Assert(evidence.TextualHunk!.Contains("@@ -6,0 +", StringComparison.Ordinal),
            $"A pure insertion's base anchor must be the preceding base line (6), not 0.\n{evidence.TextualHunk}");
    }

    // --- TASK-028 confirmation review fixes (N1, N2) ----------------------------------------------------

    private static void AttributeOnSameLineAsFieldIsNotSilentlyDropped()
    {
        // N1 repro 1: the field's own indexed span (VariableDeclaratorSyntax) is just "speed" - the leading
        // attribute on the same physical line is outside it. There is no container symbol here, so the field
        // is a top-level symbol that owns the rest of its own lines (self text) and reports the change itself.
        // (With a container, the attribute is container self text instead - see the real-analyzer tests.)
        var baseSource = "namespace Fixture.N1;\npublic class N1Target\n{\n    [Range(0, 10)] private int speed;\n}\n";
        var targetSource = "namespace Fixture.N1;\npublic class N1Target\n{\n    [Range(0, 99)] private int speed;\n}\n";

        var baseField = AdHocSymbol(baseSource, "N1.cs", "n1-project", "field", "speed", "Fixture.N1.N1Target.speed", "private int speed", "speed");
        var targetField = AdHocSymbol(targetSource, "N1.cs", "n1-project", "field", "speed", "Fixture.N1.N1Target.speed", "private int speed", "speed");
        Assert(baseField.SymbolId == targetField.SymbolId, "Sanity: only the attribute argument changes, so the field's identity must be stable.");

        var baseSnapshot = BuildSnapshot("n1-attr-base", "N1.cs", baseSource, [baseField]);
        var targetSnapshot = BuildSnapshot("n1-attr-target", "N1.cs", targetSource, [targetField]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "n1-attr-base", "n1-attr-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0,
            "N1 repro 1: an attribute-argument-only edit on the same line as a field must not silently vanish (0 entries) just because the field's own declarator span is unchanged.");
        var change = result.Changes.Single();
        Assert(change.BaseSymbol?.SymbolId == baseField.SymbolId && change.TargetSymbol?.SymbolId == baseField.SymbolId,
            "The attribute change must be attributed to the field on that line (the only indexed symbol whose range covers it).");
        var evidence = change.Contract.Evidence.Single();
        Assert(evidence.TextMode == DiffEvidenceTextMode.LineHunks && evidence.TextualHunk!.Contains("+    [Range(0, 99)] private int speed;", StringComparison.Ordinal),
            "The entry's evidence must show the actual changed original line, not just a bare fingerprint.");
    }

    private static void TrailingCommentOnSameLineAsFieldIsNotSilentlyDropped()
    {
        // N1 repro 2: same underlying cause, but with a trailing same-line comment instead of a leading
        // attribute.
        var baseSource = "namespace Fixture.N1;\npublic class N1TrailingTarget\n{\n    public int Reading; // units: ms\n}\n";
        var targetSource = "namespace Fixture.N1;\npublic class N1TrailingTarget\n{\n    public int Reading; // units: s\n}\n";

        var baseField = AdHocSymbol(baseSource, "N1Trailing.cs", "n1-trailing-project", "field", "Reading", "Fixture.N1.N1TrailingTarget.Reading", "public int Reading", "Reading");
        var targetField = AdHocSymbol(targetSource, "N1Trailing.cs", "n1-trailing-project", "field", "Reading", "Fixture.N1.N1TrailingTarget.Reading", "public int Reading", "Reading");
        Assert(baseField.SymbolId == targetField.SymbolId, "Sanity: identity must be stable across a trailing-comment-only edit.");

        var baseSnapshot = BuildSnapshot("n1-trail-base", "N1Trailing.cs", baseSource, [baseField]);
        var targetSnapshot = BuildSnapshot("n1-trail-target", "N1Trailing.cs", targetSource, [targetField]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "n1-trail-base", "n1-trail-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0, "N1 repro 2: a trailing-comment-only edit on a field's own line must not silently vanish.");
        var change = result.Changes.Single();
        Assert(change.BaseSymbol?.SymbolId == baseField.SymbolId, "The trailing comment must be attributed to the field on the same line.");
        Assert(change.Contract.Evidence.Single().TextualHunk!.Contains("units: s", StringComparison.Ordinal),
            "The evidence must show the new comment text.");
    }

    private static void AttributeOnSeparateLineIsAttributedToCoveringSymbol()
    {
        // Recorded outcome: an attribute outside the field's own span (here on its own line) is the
        // containing class's self text, so it is attributed to the class, not to the field itself.
        var baseSource = "namespace Fixture.N1;\npublic class N1SeparateTarget\n{\n    [Range(0, 10)]\n    private int speed;\n}\n";
        var targetSource = "namespace Fixture.N1;\npublic class N1SeparateTarget\n{\n    [Range(0, 99)]\n    private int speed;\n}\n";

        var containerBase = AdHocSymbol(baseSource, "N1Separate.cs", "n1-separate-project", "class", "N1SeparateTarget", "Fixture.N1.N1SeparateTarget", "public class N1SeparateTarget",
            "public class N1SeparateTarget\n{\n    [Range(0, 10)]\n    private int speed;\n}");
        var containerTarget = AdHocSymbol(targetSource, "N1Separate.cs", "n1-separate-project", "class", "N1SeparateTarget", "Fixture.N1.N1SeparateTarget", "public class N1SeparateTarget",
            "public class N1SeparateTarget\n{\n    [Range(0, 99)]\n    private int speed;\n}");
        Assert(containerBase.SymbolId == containerTarget.SymbolId, "Sanity: container identity must be stable.");
        var containerId = containerBase.SymbolId;

        var baseField = AdHocSymbol(baseSource, "N1Separate.cs", "n1-separate-project", "field", "speed", "Fixture.N1.N1SeparateTarget.speed", "private int speed", "speed", containerId);
        var targetField = AdHocSymbol(targetSource, "N1Separate.cs", "n1-separate-project", "field", "speed", "Fixture.N1.N1SeparateTarget.speed", "private int speed", "speed", containerId);

        var baseSnapshot = BuildSnapshot("n1-sep-base", "N1Separate.cs", baseSource, [containerBase, baseField]);
        var targetSnapshot = BuildSnapshot("n1-sep-target", "N1Separate.cs", targetSource, [containerTarget, targetField]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "n1-sep-base", "n1-sep-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0,
            "An attribute on its own separate line (not sharing the decorated field's line) must not be silently dropped either.");
        var change = result.Changes.Single();
        Assert(change.BaseSymbol?.SymbolId == containerId && change.TargetSymbol?.SymbolId == containerId,
            $"Recorded attribution: a leading-attribute-only edit on its own separate line is attributed to the containing class's self text (M3's existing mechanism), not to the field itself. Actual: base={change.BaseSymbol?.Name}, target={change.TargetSymbol?.Name}.");
    }

    private static void FileLineDiffBudgetExceededWithNoEntriesIsExplicit()
    {
        const int lineCount = 5000;
        var baseBody = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"// base-only-safety-net-{i}-aaaaaaaaaa"));
        var targetBody = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"// target-only-safety-net-{i}-bbbbbbbbbb"));
        var baseSource = baseBody + "\n";
        var targetSource = targetBody + "\n";
        var baseBytes = new UTF8Encoding(false).GetBytes(baseSource);
        var targetBytes = new UTF8Encoding(false).GetBytes(targetSource);

        // No symbols at all reference this file, so no per-symbol entry will ever mention it. Text outside
        // every declaration is file-level text, which is not attributed to a symbol; a change to it must be
        // flagged explicitly instead of silently returning a complete-looking empty diff. (Before the
        // owned-text redesign this was a whole-file line diff hitting its budget and downgrading coverage;
        // detection is now an exact text comparison with no edit-distance cap, so no budget can hide it.)
        var baseSnapshot = new SymbolDiffSnapshot("n1-budget-base", HashUtf8Bytes(baseBytes), DateTimeOffset.UtcNow, Coverage(), [],
            [new DiffSourceDocument("Untracked.cs", baseBytes, "utf-8", HashUtf8Bytes(baseBytes))], []);
        var targetSnapshot = new SymbolDiffSnapshot("n1-budget-target", HashUtf8Bytes(targetBytes), DateTimeOffset.UtcNow, Coverage(), [],
            [new DiffSourceDocument("Untracked.cs", targetBytes, "utf-8", HashUtf8Bytes(targetBytes))], []);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "n1-budget-base", "n1-budget-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count == 0, "Sanity: no symbols reference this file, so no ordinary entry exists for it.");
        Assert(result.Contract.Limitations.Contains("diff-file-level-text-changed", StringComparer.Ordinal) &&
               result.Contract.Coverage.Limitations.Contains("diff-file-level-text-changed", StringComparer.Ordinal),
            "A changed file whose change no symbol owns must be surfaced explicitly as a limitation, not a silently complete-looking empty diff.");

        // Unchanged file-level text must not raise the limitation.
        var quiet = new SymbolDiffService().Compare(new SymbolDiffRequest(
            new BaselineContract(BaselineKind.Vcs, "git", "n1-budget-base", "n1-budget-base-2", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint),
            baseSnapshot, baseSnapshot with { SnapshotId = "n1-budget-base-2" }));
        Assert(!quiet.Contract.Limitations.Contains("diff-file-level-text-changed", StringComparer.Ordinal),
            "Identical file-level text must not be flagged.");
    }

    private static void RealAnalyzerAttributeOnSameLineEndToEnd()
    {
        using var fixture = new GitDiffFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "N1RealAnalyzer.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        var provider = new GitBaselineProvider();
        var service = new SymbolDiffService();
        var store = Path.Combine(fixture.RootPath, "n1-real-analyzer-store");

        string Source(int rangeMax) => string.Join("\n",
            "namespace Fixture.N1RealAnalyzer;",
            "public class N1RealAnalyzerTarget",
            "{",
            $"    [System.ComponentModel.DataAnnotations.Range(0, {rangeMax})] private int speed;",
            "}",
            string.Empty);

        fixture.Write(Source(10));
        fixture.CommitAndTag("n1-real-analyzer-base");
        var baseBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(fixture.RepositoryPath, store));
        var baseSpeed = baseBuild.Symbols.Single(symbol => symbol.Kind == "field" && symbol.Name == "speed");
        Assert(baseSpeed.ContainerId is not null, "Sanity: the real analyzer must populate containerId for this field (it is a member of the class).");
        var baseSnapshot = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "n1-real-analyzer-base", baseBuild.Manifest.Coverage, baseBuild.Symbols, []));

        // The attribute and the field are on the SAME physical line. The field's span is only "speed"; the
        // attribute is the containing class's self text (member spans are masked character by character, not
        // line by line), so the class reports it with the original line in its hunk.
        fixture.Write(Source(99));
        var targetBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(fixture.RepositoryPath, store));
        var targetSpeed = targetBuild.Symbols.Single(symbol => symbol.Kind == "field" && symbol.Name == "speed");
        Assert(targetSpeed.SymbolId == baseSpeed.SymbolId, "Sanity: only the attribute argument changed, so the field's identity must be stable.");
        var targetSnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, targetBuild.Manifest.Coverage, targetBuild.Symbols, []));

        var result = service.Compare(new SymbolDiffRequest(provider.CreateBaseline(baseSnapshot, targetSnapshot), baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0,
            "With symbols produced by the real C# analyzer (not a hand-built test double), a same-line attribute-only edit must not silently vanish end to end (N1), even though the field is inside an indexed container class.");
        Assert(result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.SymbolId == baseSpeed.ContainerId &&
                change.Contract.Evidence.Any(evidence => evidence.TextualHunk?.Contains(
                    "+    [System.ComponentModel.DataAnnotations.Range(0, 99)] private int speed;", StringComparison.Ordinal) == true)),
            "The change must be reported on the containing class (owner of the attribute text) with the changed original line in its hunk.");
        Assert(!result.Changes.Any(change => change.BaseSymbol?.SymbolId == baseSpeed.SymbolId),
            "The field's own span did not change, so the field itself must not be reported.");
    }

    private static void ContainerRenameCandidateSurvivesMemberBodyEdit()
    {
        // N2: renaming a container while ALSO editing a member's body must still match as a rename
        // candidate. Before N2's fix, the container's rename "shape" used FullText (which embeds every
        // member's full text), so the unrelated member body edit made the shapes differ and broke the
        // match, reporting a spurious delete+add pair for the container instead of a rename candidate.
        var baseSource = "namespace Fixture.N2;\npublic class Widget\n{\n    public int Value() => 1;\n}\n";
        var targetSource = "namespace Fixture.N2;\npublic class Gadget\n{\n    public int Value() => 2;\n}\n";

        var baseContainerIdentity = new SymbolIdentityContract("n2-project", AnalysisKey, "class", "Fixture.N2.Widget", 0, [], null);
        var baseContainerId = DeterministicSymbolId.Create(baseContainerIdentity);
        var targetContainerIdentity = new SymbolIdentityContract("n2-project", AnalysisKey, "class", "Fixture.N2.Gadget", 0, [], null);
        var targetContainerId = DeterministicSymbolId.Create(targetContainerIdentity);
        Assert(baseContainerId != targetContainerId, "Sanity: renaming the class changes its deterministic ID (the name is part of the qualified metadata name).");

        var baseContainer = new SymbolContract(baseContainerId, "n2-project", AnalysisKey, "class", "Widget", "Fixture.N2.Widget", "public class Widget",
            "public", null, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(baseContainerId, FragmentLocation(baseSource, "N2.cs", "public class Widget\n{\n    public int Value() => 1;\n}"), DocumentKind.Source)], []);
        var targetContainer = new SymbolContract(targetContainerId, "n2-project", AnalysisKey, "class", "Gadget", "Fixture.N2.Gadget", "public class Gadget",
            "public", null, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(targetContainerId, FragmentLocation(targetSource, "N2.cs", "public class Gadget\n{\n    public int Value() => 2;\n}"), DocumentKind.Source)], []);

        var baseValueIdentity = new SymbolIdentityContract("n2-project", AnalysisKey, "method", "Fixture.N2.Widget.Value", 0, [], null);
        var baseValueId = DeterministicSymbolId.Create(baseValueIdentity);
        var targetValueIdentity = new SymbolIdentityContract("n2-project", AnalysisKey, "method", "Fixture.N2.Gadget.Value", 0, [], null);
        var targetValueId = DeterministicSymbolId.Create(targetValueIdentity);

        var baseValue = new SymbolContract(baseValueId, "n2-project", AnalysisKey, "method", "Value", "Fixture.N2.Widget.Value", "public int Value()",
            "public", baseContainerId, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(baseValueId, FragmentLocation(baseSource, "N2.cs", "public int Value() => 1;"), DocumentKind.Source)], []);
        var targetValue = new SymbolContract(targetValueId, "n2-project", AnalysisKey, "method", "Value", "Fixture.N2.Gadget.Value", "public int Value()",
            "public", targetContainerId, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(targetValueId, FragmentLocation(targetSource, "N2.cs", "public int Value() => 2;"), DocumentKind.Source)], []);

        var baseSnapshot = BuildSnapshot("n2-base", "N2.cs", baseSource, [baseContainer, baseValue]);
        var targetSnapshot = BuildSnapshot("n2-target", "N2.cs", targetSource, [targetContainer, targetValue]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "n2-base", "n2-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Any(change => change.Kind == DiffKind.RenameCandidate &&
                change.BaseSymbol?.SymbolId == baseContainerId && change.TargetSymbol?.SymbolId == targetContainerId),
            "N2: renaming a container while also editing a member's body must still be matched as a rename candidate for the container.");
        Assert(!result.Changes.Any(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.SymbolId == baseContainerId),
            "The renamed container must not also be reported as deleted.");
        Assert(!result.Changes.Any(change => change.Kind == DiffKind.Added && change.TargetSymbol?.SymbolId == targetContainerId),
            "The renamed container must not also be reported as added (i.e. not a spurious delete+add pair alongside the rename candidate).");
    }

    // --- TASK-028 third confirmation review fixes (X1-X4) + invariant property test ---------------------

    private static void AdjacentFieldEditsAreBothAttributed()
    {
        // X1: Myers groups the two adjacent changed lines into ONE run (2 deletes, 2 inserts). Before the
        // fix, hp being already-attributed (its own declarator text changed) caused the *whole run* -
        // including speed's unrelated attribute-only edit on the adjacent line - to be skipped.
        var baseSource = "namespace Fixture.X1;\npublic class X1Target\n{\n    [Range(0, 10)] private int speed;\n    private int hp = 5;\n}\n";
        var targetSource = "namespace Fixture.X1;\npublic class X1Target\n{\n    [Range(0, 99)] private int speed;\n    private int hp = 6;\n}\n";

        var baseSpeed = AdHocSymbol(baseSource, "X1Mixed.cs", "x1-project", "field", "speed", "Fixture.X1.X1Target.speed", "private int speed", "speed");
        var targetSpeed = AdHocSymbol(targetSource, "X1Mixed.cs", "x1-project", "field", "speed", "Fixture.X1.X1Target.speed", "private int speed", "speed");
        var baseHp = AdHocSymbol(baseSource, "X1Mixed.cs", "x1-project", "field", "hp", "Fixture.X1.X1Target.hp", "private int hp", "hp = 5");
        var targetHp = AdHocSymbol(targetSource, "X1Mixed.cs", "x1-project", "field", "hp", "Fixture.X1.X1Target.hp", "private int hp", "hp = 6");
        Assert(baseSpeed.SymbolId == targetSpeed.SymbolId && baseHp.SymbolId == targetHp.SymbolId, "Sanity: both fields' identities are stable.");

        var baseSnapshot = BuildSnapshot("x1-mixed-base", "X1Mixed.cs", baseSource, [baseSpeed, baseHp]);
        var targetSnapshot = BuildSnapshot("x1-mixed-target", "X1Mixed.cs", targetSource, [targetSpeed, targetHp]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "x1-mixed-base", "x1-mixed-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.Name == "hp"),
            "hp's own initializer change must still be reported.");
        Assert(result.Changes.Any(change => change.BaseSymbol?.SymbolId == baseSpeed.SymbolId || change.TargetSymbol?.SymbolId == baseSpeed.SymbolId),
            "X1: speed's attribute-only change must not be silently dropped just because it shares a Myers change-run with hp's already-attributed adjacent edit.");
    }

    private static void FileLineDiffBudgetLimitationAppearsEvenWithExistingEntries()
    {
        const int lineCount = 2100;
        var baseBody = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"        // base-only-bigfile-{i}-aaaaaaaaaa"));
        var targetBody = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"        // target-only-bigfile-{i}-bbbbbbbbbb"));
        var baseSource = $"namespace Fixture.X2;\npublic class X2Target\n{{\n    [Range(0, 10)] private int speed;\n    public static void M()\n    {{\n{baseBody}\n    }}\n}}\n";
        var targetSource = $"namespace Fixture.X2;\npublic class X2Target\n{{\n    [Range(0, 99)] private int speed;\n    public static void M()\n    {{\n{targetBody}\n    }}\n}}\n";

        var baseSpeed = AdHocSymbol(baseSource, "X2Big.cs", "x2-project", "field", "speed", "Fixture.X2.X2Target.speed", "private int speed", "speed");
        var targetSpeed = AdHocSymbol(targetSource, "X2Big.cs", "x2-project", "field", "speed", "Fixture.X2.X2Target.speed", "private int speed", "speed");
        var baseMethodText = $"public static void M()\n    {{\n{baseBody}\n    }}";
        var targetMethodText = $"public static void M()\n    {{\n{targetBody}\n    }}";
        var baseMethod = AdHocSymbol(baseSource, "X2Big.cs", "x2-project", "method", "M", "Fixture.X2.X2Target.M", "public static void M()", baseMethodText);
        var targetMethod = AdHocSymbol(targetSource, "X2Big.cs", "x2-project", "method", "M", "Fixture.X2.X2Target.M", "public static void M()", targetMethodText);
        Assert(baseMethod.SymbolId == targetMethod.SymbolId, "Sanity: method identity stable.");
        Assert(baseSpeed.SymbolId == targetSpeed.SymbolId, "Sanity: speed identity stable.");

        var baseSnapshot = BuildSnapshot("x2-big-base", "X2Big.cs", baseSource, [baseSpeed, baseMethod]);
        var targetSnapshot = BuildSnapshot("x2-big-target", "X2Big.cs", targetSource, [targetSpeed, targetMethod]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "x2-big-base", "x2-big-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        var methodChange = result.Changes.Single(change => change.BaseSymbol?.SymbolId == baseMethod.SymbolId);
        Assert(methodChange.Contract.Evidence.Single().TextMode == DiffEvidenceTextMode.FingerprintOnly,
            "Sanity: the huge method's own per-symbol diff must still hit the line-hunk budget and downgrade to fingerprint_only.");
        // X2 (owned-text redesign): change detection no longer depends on a capped whole-file line diff, so
        // the huge rewrite elsewhere in the file cannot hide speed's attribute change - it is reported with
        // its own hunk rather than merely flagged by a budget limitation.
        var speedChange = result.Changes.Single(change => change.BaseSymbol?.SymbolId == baseSpeed.SymbolId);
        Assert(speedChange.Kind == DiffKind.BodyChanged &&
               speedChange.Contract.Evidence.Any(evidence => evidence.TextualHunk?.Contains("+    [Range(0, 99)] private int speed;", StringComparison.Ordinal) == true),
            "X2: speed's attribute change must be reported with the changed line even though another symbol in the same file exceeds the line-diff budget.");
    }

    private static void RenamedFileAttributeChangeIsStillAttributed()
    {
        // X3: Old.cs -> New.cs (same class/field, moved to a differently-named file) while ALSO changing the
        // attribute argument on the same line as the field. The field's only declaration moved, so its base
        // and target declarations are paired as a move (one unpaired declaration on each side) and its self
        // text (a top-level symbol here: no container) is compared across the two files.
        var baseSource = "namespace Fixture.X3;\npublic class X3Target\n{\n    [Range(0, 10)] private int speed;\n}\n";
        var targetSource = "namespace Fixture.X3;\npublic class X3Target\n{\n    [Range(0, 99)] private int speed;\n}\n";

        var baseSpeed = AdHocSymbol(baseSource, "Old.cs", "x3-project", "field", "speed", "Fixture.X3.X3Target.speed", "private int speed", "speed");
        var targetSpeed = AdHocSymbol(targetSource, "New.cs", "x3-project", "field", "speed", "Fixture.X3.X3Target.speed", "private int speed", "speed");
        Assert(baseSpeed.SymbolId == targetSpeed.SymbolId, "Sanity: moving files does not change a symbol's deterministic identity.");

        var baseSnapshot = BuildSnapshot("x3-base", "Old.cs", baseSource, [baseSpeed]);
        var targetSnapshot = BuildSnapshot("x3-target", "New.cs", targetSource, [targetSpeed]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "x3-base", "x3-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0,
            "X3: an attribute-only edit on a field whose file was also renamed/moved must not be silently dropped.");
        var change = result.Changes.Single();
        Assert(change.BaseSymbol?.SymbolId == baseSpeed.SymbolId && change.TargetSymbol?.SymbolId == baseSpeed.SymbolId,
            "The change must still be attributed to speed even though its file moved.");
        Assert(change.Contract.Evidence.Any(evidence => evidence.TextualHunk?.Contains("--- a/Old.cs\n+++ b/New.cs\n", StringComparison.Ordinal) == true) &&
               HunkHas(change, "+    [Range(0, 99)] private int speed;"),
            "The evidence must compare Old.cs to New.cs and show the changed line.");
    }

    private static void BlankLineDeletionBetweenMembersIsNotReportedAsContainerChange()
    {
        var baseSource = string.Join("\n",
            "namespace Fixture.X4;", "public class X4BlankTarget", "{", "    public static int A() => 1;", "",
            "    public static int B() => 1;", "}", string.Empty);
        var targetSource = string.Join("\n",
            "namespace Fixture.X4;", "public class X4BlankTarget", "{", "    public static int A() => 1;",
            "    public static int B() => 1;", "}", string.Empty);

        var containerBase = AdHocSymbol(baseSource, "X4Blank.cs", "x4-project", "class", "X4BlankTarget", "Fixture.X4.X4BlankTarget", "public class X4BlankTarget",
            "public class X4BlankTarget\n{\n    public static int A() => 1;\n\n    public static int B() => 1;\n}");
        var containerTarget = AdHocSymbol(targetSource, "X4Blank.cs", "x4-project", "class", "X4BlankTarget", "Fixture.X4.X4BlankTarget", "public class X4BlankTarget",
            "public class X4BlankTarget\n{\n    public static int A() => 1;\n    public static int B() => 1;\n}");
        Assert(containerBase.SymbolId == containerTarget.SymbolId, "Sanity: container identity stable.");
        var containerId = containerBase.SymbolId;

        SymbolContract Member(string source, string name) => AdHocSymbol(
            source, "X4Blank.cs", "x4-project", "method", name, $"Fixture.X4.X4BlankTarget.{name}", $"public static int {name}()", $"public static int {name}() => 1;", containerId);

        var baseSnapshot = BuildSnapshot("x4-blank-base", "X4Blank.cs", baseSource, [containerBase, Member(baseSource, "A"), Member(baseSource, "B")]);
        var targetSnapshot = BuildSnapshot("x4-blank-target", "X4Blank.cs", targetSource, [containerTarget, Member(targetSource, "A"), Member(targetSource, "B")]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "x4-blank-base", "x4-blank-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(!result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && (change.BaseSymbol?.SymbolId == containerId || change.TargetSymbol?.SymbolId == containerId)),
            "X4: deleting a single blank line between members is a whitespace-only change to the container's self text and must not be reported as body_changed (U2 rule 3).");
        Assert(!result.Changes.Any(change => change.BaseSymbol?.Name is "A" or "B" || change.TargetSymbol?.Name is "A" or "B"),
            "Members whose own declarations did not change must not appear either.");
    }

    private static void FieldReindentationIsReportedAsFormattingOnly()
    {
        var baseSource = "namespace Fixture.X4;\npublic class X4ReindentTarget\n{\n    private int X;\n}\n";
        var targetSource = "namespace Fixture.X4;\npublic class X4ReindentTarget\n{\n        private int X;\n}\n";

        var baseField = AdHocSymbol(baseSource, "X4Reindent.cs", "x4-reindent-project", "field", "X", "Fixture.X4.X4ReindentTarget.X", "private int X", "private int X;");
        var targetField = AdHocSymbol(targetSource, "X4Reindent.cs", "x4-reindent-project", "field", "X", "Fixture.X4.X4ReindentTarget.X", "private int X", "private int X;");
        Assert(baseField.SymbolId == targetField.SymbolId, "Sanity: identity unaffected by indentation.");

        var baseSnapshot = BuildSnapshot("x4-reindent-base", "X4Reindent.cs", baseSource, [baseField]);
        var targetSnapshot = BuildSnapshot("x4-reindent-target", "X4Reindent.cs", targetSource, [targetField]);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "x4-reindent-base", "x4-reindent-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0, "The indentation change must still be reported (not silently dropped).");
        var change = result.Changes.Single();
        Assert(change.Kind == DiffKind.FormattingOnly,
            $"X4: a whitespace-only (indentation) change must be classified as formatting_only, not body_changed. Actual: {change.Kind}.");
        Assert(change.BaseSymbol?.SymbolId == baseField.SymbolId, "Must be attributed to the field on that line.");
        Assert(change.Contract.Evidence.Single().TextMode == DiffEvidenceTextMode.FingerprintOnly,
            "Formatting-only evidence must be fingerprint-only, not a rendered hunk.");
    }

    // --- Owned-text redesign: real-analyzer regressions (TASK-028 review 4: N1-N6) -------------------------

    private sealed class RealWorkspace : IDisposable
    {
        public RealWorkspace(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), $"cv-{name}-{Guid.NewGuid():N}");
            Workspace = Path.Combine(Root, "workspace");
            Store = Path.Combine(Root, "store");
            Directory.CreateDirectory(Workspace);
            File.WriteAllText(Path.Combine(Workspace, "Property.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        }

        public string Root { get; }
        public string Workspace { get; }
        public string Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record RealDiffOutcome(
        SymbolDiffResult Result,
        SymbolDiffSnapshot Base,
        SymbolDiffSnapshot Target,
        IReadOnlyDictionary<string, string> BaseFiles,
        IReadOnlyDictionary<string, string> TargetFiles);

    /// <summary>Indexes <paramref name="baseFiles"/> and then <paramref name="targetFiles"/> with the real C#
    /// analyzer in one workspace and diffs the two snapshots.</summary>
    private static RealDiffOutcome RealDiff(RealWorkspace workspace, IReadOnlyDictionary<string, string> baseFiles, IReadOnlyDictionary<string, string> targetFiles)
    {
        SymbolDiffSnapshot Capture(IReadOnlyDictionary<string, string> files, string prefix)
        {
            foreach (var existing in Directory.GetFiles(workspace.Workspace, "*.cs"))
            {
                File.Delete(existing);
            }

            foreach (var (path, text) in files)
            {
                File.WriteAllText(Path.Combine(workspace.Workspace, path), text, new UTF8Encoding(false));
            }

            var build = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace.Workspace, workspace.Store));
            return new SymbolDiffSnapshot(
                $"{prefix}-{Guid.NewGuid():N}", build.Manifest.InputFingerprint, DateTimeOffset.UtcNow, build.Manifest.Coverage,
                build.Symbols, ReadSourceDocuments(workspace.Workspace, build.Manifest), []);
        }

        var baseSnapshot = Capture(baseFiles, "real-base");
        var targetSnapshot = Capture(targetFiles, "real-target");
        var baseline = new BaselineContract(
            BaselineKind.Vcs, "git", baseSnapshot.SnapshotId, targetSnapshot.SnapshotId, null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);
        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        result.Contract.Validate();
        return new RealDiffOutcome(result, baseSnapshot, targetSnapshot, baseFiles, targetFiles);
    }

    private static IReadOnlyList<DiffSourceDocument> ReadSourceDocuments(string workspace, ManifestContract manifest) =>
        manifest.Files.Select(file =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(workspace, file.Path));
            return new DiffSourceDocument(file.Path, bytes, file.Encoding, file.ContentHash);
        }).ToArray();

    private static bool HunkHas(SymbolDiffChange change, string line) =>
        change.Contract.Evidence.Any(evidence => evidence.TextualHunk?.Contains($"\n{line}\n", StringComparison.Ordinal) == true);

    private static string Describe(SymbolDiffResult result) => string.Join("\n", result.Changes.Select(change =>
        $"{change.Kind} {change.BaseSymbol?.Name ?? "-"}->{change.TargetSymbol?.Name ?? "-"}: " +
        string.Join(" | ", change.Contract.Evidence.Select(evidence => $"{evidence.Kind}/{evidence.TextMode}: {evidence.TextualHunk}"))));

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static void OwnedTextRegressionsWithRealAnalyzer()
    {
        using var workspace = new RealWorkspace("owned-text-regressions");

        // N1 swap: the two fields trade lines while speed's attribute changes. A positional line pairing
        // matched speed's old line with hp's new line and dropped "Range(0, 99)".
        var n1 = RealDiff(workspace,
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "public class Target", "{", "    [Range(0, 10)] private int speed;", "    private int hp = 5;", "}") },
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "public class Target", "{", "    private int hp = 6;", "    [Range(0, 99)] private int speed;", "}") });
        Assert(n1.Result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.Name == "Target" &&
                HunkHas(change, "+    [Range(0, 99)] private int speed;")),
            $"N1 swap: the class must report speed's changed attribute line.\n{Describe(n1.Result)}");
        Assert(n1.Result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.Name == "hp"),
            $"N1 swap: hp's own value change must still be reported.\n{Describe(n1.Result)}");

        // N2 insert above: a field is replaced above speed while speed's attribute changes.
        var n2 = RealDiff(workspace,
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "public class Target", "{", "    [Range(0, 10)] private int speed;", "    private int gone;", "}") },
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "public class Target", "{", "    private int added;", "    [Range(0, 99)] private int speed;", "}") });
        Assert(n2.Result.Changes.Any(change => change.BaseSymbol?.Name == "Target" && HunkHas(change, "+    [Range(0, 99)] private int speed;")),
            $"N2 insert-above: speed's attribute change must be in the class's hunk, not only a gone->added rename candidate.\n{Describe(n2.Result)}");

        // N3 enum attribute on the enum's own line, plus a member appended on that same line.
        var n3 = RealDiff(workspace,
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "[Description(\"a\")] public enum Color { Red, Green }") },
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "[Description(\"b\")] public enum Color { Red, Green, Blue }") });
        Assert(n3.Result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.Name == "Color" &&
                HunkHas(change, "+[Description(\"b\")] public enum Color { Red, Green, Blue }")),
            $"N3: the enum's same-line attribute change must be reported on the enum with the original line.\n{Describe(n3.Result)}");
        Assert(n3.Result.Changes.Any(change => change.Kind == DiffKind.Added && change.TargetSymbol?.Name == "Blue"),
            "N3: the appended member must still be reported as added.");
        Assert(!n3.Result.Changes.Any(change => change.BaseSymbol?.Name is "Red" or "Green"),
            "N3: members whose own text did not change must not be reported (M2).");

        // N4 absorb: a blank line deleted at the top of the class, speed's attribute and hp's value changed.
        var n4 = RealDiff(workspace,
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "public class Target", "{", "", "    [Range(0, 10)] private int speed;", "    private int hp = 5;", "}") },
            new Dictionary<string, string> { ["N.cs"] = Lines("namespace Fixture.N;", "public class Target", "{", "    [Range(0, 99)] private int speed;", "    private int hp = 6;", "}") });
        Assert(n4.Result.Changes.Any(change => change.BaseSymbol?.Name == "Target" && HunkHas(change, "+    [Range(0, 99)] private int speed;")),
            $"N4: speed's attribute change must not be absorbed by the blank-line deletion.\n{Describe(n4.Result)}");
        Assert(n4.Result.Changes.Any(change => change.Kind == DiffKind.BodyChanged && change.BaseSymbol?.Name == "hp"),
            "N4: hp's own change must be reported.");

        // N5 extract: Q moves unchanged from A.cs into its own new file. Nothing changed in any symbol.
        var n5 = RealDiff(workspace,
            new Dictionary<string, string>
            {
                ["A.cs"] = Lines("namespace Fixture.N;", "", "public class P", "{", "    public int X() => 1;", "}", "", "public class Q", "{", "    public int Y;", "}")
            },
            new Dictionary<string, string>
            {
                ["A.cs"] = Lines("namespace Fixture.N;", "", "public class P", "{", "    public int X() => 1;", "}"),
                ["Q.cs"] = Lines("namespace Fixture.N;", "", "public class Q", "{", "    public int Y;", "}")
            });
        Assert(n5.Result.Changes.Count == 0,
            $"N5: extracting an unchanged type into a new file must not report body_changed for the type or its members.\n{Describe(n5.Result)}");

        // N6 split: Old.cs becomes P.cs and Q.cs while Q.Y's attribute argument changes.
        var n6 = RealDiff(workspace,
            new Dictionary<string, string>
            {
                ["Old.cs"] = Lines("namespace Fixture.N;", "", "public class P", "{", "    public int X() => 1;", "}", "", "public class Q", "{", "    [R(1)] public int Y;", "}")
            },
            new Dictionary<string, string>
            {
                ["P.cs"] = Lines("namespace Fixture.N;", "", "public class P", "{", "    public int X() => 1;", "}"),
                ["Q.cs"] = Lines("namespace Fixture.N;", "", "public class Q", "{", "    [R(2)] public int Y;", "}")
            });
        var n6Change = n6.Result.Changes.SingleOrDefault(change => change.BaseSymbol?.Name == "Q");
        Assert(n6Change is not null && n6Change.Kind == DiffKind.BodyChanged &&
               n6Change.Contract.Evidence.Any(evidence => evidence.TextualHunk?.Contains("--- a/Old.cs\n+++ b/Q.cs\n", StringComparison.Ordinal) == true) &&
               HunkHas(n6Change, "+    [R(2)] public int Y;"),
            $"N6: Q's declaration moved Old.cs -> Q.cs is paired and its attribute change is reported with the new line.\n{Describe(n6.Result)}");
        Assert(n6.Result.Changes.Count == 1, $"N6: only Q changed.\n{Describe(n6.Result)}");

        MemberTraceLinesAreNotContainerEvidence(workspace);
        MultiLineMemberTrailingTextIsNotDropped(workspace);
        MovedFileLevelTextIsCompared(workspace);
    }

    /// <summary>
    /// Rule (b'): a whole field declaration line added or removed (shell "private int ...;", attribute and
    /// trailing comment included) is the field's own trace. The class reports nothing; the field's
    /// added/removed header_only evidence shows the whole original line.
    /// </summary>
    private static void MemberTraceLinesAreNotContainerEvidence(RealWorkspace workspace)
    {
        var baseFile = Lines("namespace Fixture.B;", "public class Target", "{", "    private int speed = 1;", "    private int hp = 5;", "}");
        bool ClassReported(SymbolDiffResult result) => result.Changes.Any(change => change.BaseSymbol?.Name == "Target" || change.TargetSymbol?.Name == "Target");

        var added = RealDiff(workspace,
            new Dictionary<string, string> { ["B.cs"] = baseFile },
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Target", "{", "    private int speed = 1;", "    private int added = 3;", "    private int hp = 5;", "}") });
        Assert(!ClassReported(added.Result), $"b': adding a field declaration must not report the class.\n{Describe(added.Result)}");
        Assert(added.Result.Changes.Any(change => change.Kind == DiffKind.Added && change.TargetSymbol?.Name == "added" &&
                HunkHas(change, "+    private int added = 3;")),
            $"b': the added field's header_only evidence must show the whole line.\n{Describe(added.Result)}");

        var removed = RealDiff(workspace,
            new Dictionary<string, string> { ["B.cs"] = baseFile },
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Target", "{", "    private int speed = 1;", "}") });
        Assert(!ClassReported(removed.Result), $"b': removing a field declaration must not report the class.\n{Describe(removed.Result)}");
        Assert(removed.Result.Changes.Any(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.Name == "hp" &&
                HunkHas(change, "-    private int hp = 5;")),
            $"b': the removed field's header_only evidence must show the whole line.\n{Describe(removed.Result)}");

        var attributed = RealDiff(workspace,
            new Dictionary<string, string> { ["B.cs"] = baseFile },
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Target", "{", "    private int speed = 1;", "    [Range(0, 7)] private int guarded = 2; // why", "    private int hp = 5;", "}") });
        Assert(!ClassReported(attributed.Result), $"b': adding an attributed field must not report the class.\n{Describe(attributed.Result)}");
        Assert(attributed.Result.Changes.Any(change => change.Kind == DiffKind.Added && change.TargetSymbol?.Name == "guarded" &&
                HunkHas(change, "+    [Range(0, 7)] private int guarded = 2; // why")),
            $"b': the added field's evidence must include its attribute and trailing comment.\n{Describe(attributed.Result)}");

        // An existing field's attribute on a line that exists on both sides is still class text.
        var existing = RealDiff(workspace,
            new Dictionary<string, string> { ["B.cs"] = baseFile },
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Target", "{", "    [Range(0, 9)] private int speed = 1;", "    private int extra = 4;", "    private int hp = 5;", "}") });
        Assert(existing.Result.Changes.Any(change => change.BaseSymbol?.Name == "Target" && HunkHas(change, "+    [Range(0, 9)] private int speed = 1;")),
            $"b': an attribute added to an existing field is still the class's change, even next to an added field.\n{Describe(existing.Result)}");

        // An attribute removed from an existing field while a new field declaration line is added right after
        // it must still be reported on the class (only the added line is a member trace).
        var removedAttribute = RealDiff(workspace,
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Target", "{", "    // note", "    [Range(0, 71)] private int rate = 5;", "", "    public int Compute()", "    {", "        return 1;", "    }", "}") },
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Target", "{", "    // note", "    private int rate = 5;", "    private int d66790 = 85;", "", "    public int Compute()", "    {", "        return 1;", "    }", "}") });
        Assert(removedAttribute.Result.Changes.Any(change => change.BaseSymbol?.Name == "Target" && HunkHas(change, "-    [Range(0, 71)] private int rate = 5;")),
            $"b': removing an existing field's attribute next to an added field must still be reported on the class.\n{Describe(removedAttribute.Result)}");

        // A type's first/last line is never a member trace, even when only an added member touches it.
        var boundary = RealDiff(workspace,
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "public class Tiny { }") },
            new Dictionary<string, string> { ["B.cs"] = Lines("namespace Fixture.B;", "[X] public class Tiny { private int y; }") });
        Assert(boundary.Result.Changes.Any(change => change.BaseSymbol?.Name == "Tiny" && change.Kind == DiffKind.BodyChanged &&
                HunkHas(change, "+[X] public class Tiny { private int y; }")),
            $"b': a type's own header line must stay container text even when only an added member touches it.\n{Describe(boundary.Result)}");
    }

    /// <summary>
    /// L1 (TASK-028 review 5): a multi-line member added or removed whose last line carries other text
    /// ("} // MUST-KEEP marker"). Its header_only evidence shows only the header lines, so rule (b') only
    /// leaves out the member's header lines; the closing line stays container text and is reported there.
    /// </summary>
    private static void MultiLineMemberTrailingTextIsNotDropped(RealWorkspace workspace)
    {
        var withMethod = Lines("namespace Fixture.L1;", "public class Target", "{", "    private int keep = 1;", "    public void M()", "    {",
            "    } // MUST-KEEP marker", "}");
        var withoutMethod = Lines("namespace Fixture.L1;", "public class Target", "{", "    private int keep = 1;", "}");

        var deleted = RealDiff(workspace, new Dictionary<string, string> { ["L1.cs"] = withMethod }, new Dictionary<string, string> { ["L1.cs"] = withoutMethod });
        Assert(deleted.Result.Changes.Any(change => change.Kind == DiffKind.Deleted && change.BaseSymbol?.Name == "M"),
            "L1: the deleted method must be reported.");
        Assert(deleted.Result.Changes.Any(change => HunkHas(change, "-    } // MUST-KEEP marker")),
            $"L1: the deleted method's closing-line comment must appear in some hunk.\n{Describe(deleted.Result)}");

        var added = RealDiff(workspace, new Dictionary<string, string> { ["L1.cs"] = withoutMethod }, new Dictionary<string, string> { ["L1.cs"] = withMethod });
        Assert(added.Result.Changes.Any(change => change.Kind == DiffKind.Added && change.TargetSymbol?.Name == "M"),
            "L1: the added method must be reported.");
        Assert(added.Result.Changes.Any(change => HunkHas(change, "+    } // MUST-KEEP marker")),
            $"L1: the added method's closing-line comment must appear in some hunk.\n{Describe(added.Result)}");

        // Without text beyond the member on its other lines, the method's own entry says everything.
        var plain = RealDiff(workspace,
            new Dictionary<string, string> { ["L1.cs"] = Lines("namespace Fixture.L1;", "public class Target", "{", "    private int keep = 1;", "    public void M()", "    {", "    }", "}") },
            new Dictionary<string, string> { ["L1.cs"] = withoutMethod });
        Assert(!plain.Result.Changes.Any(change => change.BaseSymbol?.Name == "Target"),
            $"L1: deleting a plain multi-line method must not report the class.\n{Describe(plain.Result)}");
    }

    /// <summary>
    /// P2 (TASK-028 review 5): file-level text (usings, namespace) of a file that was moved is compared with
    /// the file it moved from (the other-side-only file sharing the most symbols).
    /// </summary>
    private static void MovedFileLevelTextIsCompared(RealWorkspace workspace)
    {
        string Source(string usingLine) => Lines(usingLine, "namespace Fixture.P2;", "public class Mover", "{", "    public int V;", "}");

        var changed = RealDiff(workspace,
            new Dictionary<string, string> { ["Old.cs"] = Source("using System;") },
            new Dictionary<string, string> { ["New.cs"] = Source("using System.Linq;") });
        Assert(changed.Result.Changes.Count == 0, $"P2: sanity - no symbol changed.\n{Describe(changed.Result)}");
        Assert(changed.Result.Contract.Limitations.Contains("diff-file-level-text-changed", StringComparer.Ordinal),
            "P2: a using changed while the file moved must be flagged as a file-level text change.");

        var unchanged = RealDiff(workspace,
            new Dictionary<string, string> { ["Old.cs"] = Source("using System;") },
            new Dictionary<string, string> { ["New.cs"] = Source("using System;") });
        Assert(!unchanged.Result.Contract.Limitations.Contains("diff-file-level-text-changed", StringComparer.Ordinal),
            "P2: a pure move must not be flagged.");

        var split = RealDiff(workspace,
            new Dictionary<string, string> { ["Old.cs"] = Lines("using System;", "namespace Fixture.P2;", "public class P { }", "public class Q { }") },
            new Dictionary<string, string>
            {
                ["P.cs"] = Lines("using System;", "namespace Fixture.P2;", "public class P { }"),
                ["Q.cs"] = Lines("using System.Text;", "namespace Fixture.P2;", "public class Q { }"),
            });
        Assert(split.Result.Contract.Limitations.Contains("diff-file-level-text-changed", StringComparer.Ordinal),
            "P2: a split file whose second part gained a different using must be flagged too.");
    }

    /// <summary>
    /// P1 (TASK-028 review 5): one class with many single-line fields, one field changed. Before the fix the
    /// diff was quadratic in the number of symbols (N=5,000: ~6 s, N=20,000: ~100 s); it must now stay
    /// near-linear. The bound is generous for CI machines while far below the quadratic cost.
    /// </summary>
    private static void LargeContainerDiffStaysNearLinear()
    {
        const int count = 10000;
        (SymbolDiffSnapshot Snapshot, string ClassId) Build(string snapshotId, bool changed)
        {
            var builder = new StringBuilder("namespace Perf;\npublic class Big\n{\n");
            var starts = new int[count];
            var texts = new string[count];
            for (var i = 0; i < count; i++)
            {
                texts[i] = $"f{i} = {(changed && i == 0 ? 999999 : i)}";
                builder.Append("    private int ");
                starts[i] = builder.Length;
                builder.Append(texts[i]).Append(";\n");
            }

            builder.Append("}\n");
            var text = builder.ToString();
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var hash = HashUtf8Bytes(bytes);
            var classStart = text.IndexOf("public class Big", StringComparison.Ordinal);
            var classId = DeterministicSymbolId.Create(new SymbolIdentityContract("perf", AnalysisKey, "class", "Perf.Big", 0, [], null));
            SymbolContract Symbol(string id, string kind, string name, string qualifiedName, string? container, TextSpanContract span) =>
                new(id, "perf-project", AnalysisKey, kind, name, qualifiedName, $"{kind} {name}", "public", container, 0, IdentityQuality.Semantic, [], null,
                    [new DeclarationContract(id, new LocationContract("file-big", hash, span, "Big.cs", null), DocumentKind.Source)], []);
            var symbols = new List<SymbolContract>(count + 1)
            {
                Symbol(classId, "class", "Big", "Perf.Big", null, new TextSpanContract(classStart, text.Length - 1 - classStart, 2, count + 4)),
            };
            for (var i = 0; i < count; i++)
            {
                var id = DeterministicSymbolId.Create(new SymbolIdentityContract("perf", AnalysisKey, "field", $"Perf.Big.f{i}", 0, [], null));
                symbols.Add(Symbol(id, "field", $"f{i}", $"Perf.Big.f{i}", classId, new TextSpanContract(starts[i], texts[i].Length, i + 4, i + 4)));
            }

            return (new SymbolDiffSnapshot(snapshotId, hash, DateTimeOffset.UtcNow, Coverage(), symbols,
                [new DiffSourceDocument("Big.cs", bytes, "utf-8", hash)], []), classId);
        }

        var (baseSnapshot, _) = Build("perf-base", changed: false);
        var (targetSnapshot, _) = Build("perf-target", changed: true);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "perf-base", "perf-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);
        var stopwatch = Stopwatch.StartNew();
        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        stopwatch.Stop();

        Assert(result.Changes.Count == 1 && result.Changes[0].Kind == DiffKind.BodyChanged && result.Changes[0].BaseSymbol?.Name == "f0",
            $"P1: only f0 changed.\n{Describe(result)}");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"P1: diffing a class with {count} fields must stay near-linear (took {stopwatch.Elapsed.TotalSeconds:F2} s; the quadratic version took ~25 s).");
    }

    // --- Owned-text coverage property test (independent oracle) ---------------------------------------------

    private sealed record PField(string Name, int Value, int? Attr, bool AttrOwnLine, string? Trailing, int Indent, string Type = "int");

    private sealed record PMethod(string Name, int Value, string? Trailing);

    private sealed record PClass(
        string Name,
        string Comment,
        IReadOnlyList<PField> Fields,
        IReadOnlyList<(string Name, int Value)> Shared,
        IReadOnlyList<PMethod> Methods,
        bool BlankAfterFields,
        string? PropertyTrailing,
        int? NestedValue,
        string NestedComment);

    private sealed record PEnum(string Name, IReadOnlyList<string> Members, bool OneLine, string Description);

    /// <summary>A type declared on a single line, so its members sit on its first and last line.</summary>
    private sealed record PTiny(string Name, int? Attr, IReadOnlyList<(string Name, int Value)> Fields);

    /// <summary>A partial class with two declarations (file keys Name and Name#2).</summary>
    private sealed record PPartial(string Name, string CommentA, int ValueA, string CommentB, int ValueB);

    private sealed record PState(
        IReadOnlyList<PClass> Classes,
        IReadOnlyList<PEnum> Enums,
        PTiny Tiny,
        PPartial Partial,
        IReadOnlyDictionary<string, string> FileOf);

    /// <summary>An edit; <paramref name="NonSubstantive"/> edits change only whitespace or file placement.</summary>
    private sealed record PEdit(string Name, bool NonSubstantive, Func<PState, Random, PState> Apply);

    private static void OwnedTextCoveragePropertyTest()
    {
        const int seed = 20260925;
        const int iterations = 520;
        var rng = new Random(seed);
        var edits = BuildOwnedTextEditCatalog();
        var failures = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        using var workspace = new RealWorkspace("owned-text-property");

        for (var iteration = 0; iteration < iterations && failures.Count < 5; iteration++)
        {
            var initial = InitialPropertyState(rng);
            var chosen = new List<PEdit>();
            var target = initial;
            var editCount = rng.Next(1, 4);
            for (var e = 0; e < editCount; e++)
            {
                var edit = edits[rng.Next(edits.Count)];
                chosen.Add(edit);
                target = edit.Apply(target, rng);
                covered.Add(edit.Name);
            }

            var editNames = string.Join(",", chosen.Select(edit => edit.Name));
            try
            {
                var outcome = RealDiff(workspace, RenderPropertyState(initial), RenderPropertyState(target));
                VerifyOwnedTextOracle(outcome);
                if (chosen.All(edit => edit.NonSubstantive))
                {
                    // Accepted limitation (1:1 move heuristic): when more than one declaration of a partial type
                    // moves to new files at once, the unpaired declarations are reported as removed/added
                    // header_only evidence (over-reporting, not a loss). Nothing else may appear.
                    Assert(!outcome.Result.Changes.Any(change => change.Kind == DiffKind.BodyChanged &&
                            !change.Contract.Evidence.All(evidence => evidence.Kind is "declaration-removed" or "declaration-added")),
                        $"Whitespace/placement-only edits must not produce body_changed.\n{Describe(outcome.Result)}");
                }
            }
            catch (Exception exception)
            {
                static string Files(IReadOnlyDictionary<string, string> files) =>
                    string.Join("\n", files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"=== {pair.Key}\n{pair.Value}"));
                failures.Add($"iteration={iteration} seed={seed} edits=[{editNames}]: {exception.Message}\n" +
                    $"--- base files\n{Files(RenderPropertyState(initial))}--- target files\n{Files(RenderPropertyState(target))}");
            }
        }

        Assert(failures.Count == 0,
            $"Owned-text property test found {failures.Count} counterexample(s) (seed={seed}, {iterations} iterations):\n{string.Join("\n\n", failures)}");
        Assert(covered.Count == edits.Count, "Sanity: every edit kind must have been exercised at least once.");
    }

    private static PState InitialPropertyState(Random rng)
    {
        string? MaybeComment(string prefix) => rng.Next(2) == 0 ? $"{prefix}-{rng.Next(100)}" : null;

        var alpha = new PClass("Alpha", $"note-{rng.Next(100)}",
            [
                new PField("speed", rng.Next(100), rng.Next(100), rng.Next(2) == 0, MaybeComment("units"), 4),
                new PField("hp", rng.Next(100), rng.Next(2) == 0 ? rng.Next(100) : null, false, null, 4),
                new PField("mana", rng.Next(100), null, false, null, 4, rng.Next(2) == 0 ? "int" : "int[,]"),
            ],
            [("a", rng.Next(100)), ("b", rng.Next(100))],
            [new PMethod("Compute", rng.Next(100), MaybeComment("end")), new PMethod("Other", rng.Next(100), MaybeComment("tail"))],
            true, MaybeComment("prop"), rng.Next(2) == 0 ? rng.Next(100) : null, $"inner-{rng.Next(100)}");
        var beta = new PClass("Beta", $"note-{rng.Next(100)}",
            [new PField("rate", rng.Next(100), rng.Next(100), false, null, 4)], [],
            [new PMethod("Compute", rng.Next(100), MaybeComment("end"))],
            rng.Next(2) == 0, null, null, $"inner-{rng.Next(100)}");
        var color = new PEnum("Color", rng.Next(2) == 0 ? ["Red", "Green"] : ["Red", "Green", "Blue"], rng.Next(2) == 0, $"d{rng.Next(100)}");
        var tiny = new PTiny("Tiny", rng.Next(2) == 0 ? rng.Next(100) : null, [("y", rng.Next(100)), ("z", rng.Next(100))]);
        var partial = new PPartial("Gamma", $"part-a-{rng.Next(100)}", rng.Next(100), $"part-b-{rng.Next(100)}", rng.Next(100));
        var layout = rng.Next(3);
        var fileOf = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Alpha"] = "Main.cs",
            ["Beta"] = layout == 0 ? "Main.cs" : "Beta.cs",
            ["Color"] = layout == 2 ? "Beta.cs" : "Main.cs",
            ["Tiny"] = layout == 1 ? "Beta.cs" : "Main.cs",
            ["Gamma"] = "Main.cs",
            ["Gamma#2"] = rng.Next(2) == 0 ? "Main.cs" : "GammaPart.cs",
        };
        return new PState([alpha, beta], [color], tiny, partial, fileOf);
    }

    private static PState WithClass(PState state, Random rng, Func<PClass, Random, PClass> change)
    {
        var index = rng.Next(state.Classes.Count);
        var classes = state.Classes.ToArray();
        classes[index] = change(classes[index], rng);
        return state with { Classes = classes };
    }

    private static PClass WithField(PClass type, Random rng, Func<PField, Random, PField> change)
    {
        if (type.Fields.Count == 0)
        {
            return type;
        }

        var index = rng.Next(type.Fields.Count);
        var fields = type.Fields.ToArray();
        fields[index] = change(fields[index], rng);
        return type with { Fields = fields };
    }

    private static PClass WithMethod(PClass type, Random rng, Func<PMethod, Random, PMethod> change)
    {
        if (type.Methods.Count == 0)
        {
            return type;
        }

        var index = rng.Next(type.Methods.Count);
        var methods = type.Methods.ToArray();
        methods[index] = change(methods[index], rng);
        return type with { Methods = methods };
    }

    private static PState WithEnum(PState state, Func<PEnum, PEnum> change) => state with { Enums = state.Enums.Select(change).ToArray() };

    private static PState MoveType(PState state, string type, string file) =>
        state with { FileOf = new Dictionary<string, string>(state.FileOf, StringComparer.Ordinal) { [type] = file } };

    private static List<PEdit> BuildOwnedTextEditCatalog() =>
    [
        new PEdit("change-attr-value", false, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, r2) => f with { Attr = 100 + r2.Next(900) }))),
        new PEdit("remove-attr", false, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, _) => f with { Attr = null }))),
        new PEdit("change-trailing-comment", false, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, r2) => f with { Trailing = $"units-{100 + r2.Next(900)}" }))),
        new PEdit("change-class-comment", false, (s, rng) => WithClass(s, rng, (c, r) => c with { Comment = $"note-{100 + r.Next(900)}" })),
        new PEdit("change-field-value", false, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, r2) => f with { Value = 100 + r2.Next(900) }))),
        new PEdit("change-field-type", false, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, _) => f with { Type = f.Type == "int[,]" ? "int[]" : "int[,]" }))),
        new PEdit("rename-field", false, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, r2) => f with { Name = $"renamed{r2.Next(100000)}" }))),
        new PEdit("change-method-body", false, (s, rng) => WithClass(s, rng, (c, r) => WithMethod(c, r, (m, r2) => m with { Value = 100 + r2.Next(900) }))),
        new PEdit("change-method-trailing", false, (s, rng) => WithClass(s, rng, (c, r) => WithMethod(c, r, (m, r2) => m with { Trailing = $"end-{100 + r2.Next(900)}" }))),
        new PEdit("add-method", false, (s, rng) => WithClass(s, rng, (c, r) =>
        {
            var methods = c.Methods.ToList();
            methods.Insert(r.Next(methods.Count + 1), new PMethod($"Extra{r.Next(100000)}", r.Next(100), r.Next(2) == 0 ? $"added-{r.Next(100)}" : null));
            return c with { Methods = methods };
        })),
        new PEdit("remove-method", false, (s, rng) => WithClass(s, rng, (c, r) =>
            c.Methods.Count == 0 ? c : c with { Methods = c.Methods.Where((_, i) => i != r.Next(c.Methods.Count)).ToArray() })),
        new PEdit("change-property-comment", false, (s, rng) => WithClass(s, rng, (c, r) => c with { PropertyTrailing = $"prop-{100 + r.Next(900)}" })),
        new PEdit("toggle-nested-type", false, (s, rng) => WithClass(s, rng, (c, r) => c with { NestedValue = c.NestedValue is null ? r.Next(100) : null })),
        new PEdit("change-nested-comment", false, (s, rng) => WithClass(s, rng, (c, r) => c with { NestedComment = $"inner-{100 + r.Next(900)}" })),
        new PEdit("change-nested-value", false, (s, rng) => WithClass(s, rng, (c, r) => c.NestedValue is null ? c : c with { NestedValue = 100 + r.Next(900) })),
        new PEdit("swap-fields", false, (s, rng) => WithClass(s, rng, (c, r) =>
        {
            if (c.Fields.Count < 2) return c;
            var fields = c.Fields.ToArray();
            var i = r.Next(fields.Length - 1);
            (fields[i], fields[i + 1]) = (fields[i + 1], fields[i]);
            return c with { Fields = fields };
        })),
        new PEdit("add-field", false, (s, rng) => WithClass(s, rng, (c, r) =>
        {
            var fields = c.Fields.ToList();
            fields.Insert(r.Next(fields.Count + 1), new PField($"extra{r.Next(100000)}", r.Next(100), r.Next(2) == 0 ? r.Next(100) : null, false,
                r.Next(2) == 0 ? $"new-{r.Next(100)}" : null, 4));
            return c with { Fields = fields };
        })),
        new PEdit("remove-field", false, (s, rng) => WithClass(s, rng, (c, r) =>
            c.Fields.Count == 0 ? c : c with { Fields = c.Fields.Where((_, i) => i != r.Next(c.Fields.Count)).ToArray() })),
        new PEdit("add-declarator", false, (s, rng) => WithClass(s, rng, (c, r) => c with { Shared = [.. c.Shared, ($"d{r.Next(100000)}", r.Next(100))] })),
        new PEdit("remove-declarator", false, (s, rng) => WithClass(s, rng, (c, _) => c.Shared.Count == 0 ? c : c with { Shared = c.Shared.Take(c.Shared.Count - 1).ToArray() })),
        new PEdit("change-declarator-value", false, (s, rng) => WithClass(s, rng, (c, r) =>
            c.Shared.Count == 0 ? c : c with { Shared = c.Shared.Select((d, i) => i == 0 ? (d.Name, 100 + r.Next(900)) : d).ToArray() })),
        new PEdit("add-enum-member", false, (s, rng) => WithEnum(s, e => e with { Members = [.. e.Members, $"Member{rng.Next(100000)}"] })),
        new PEdit("remove-enum-member", false, (s, _) => WithEnum(s, e => e.Members.Count > 1 ? e with { Members = e.Members.Take(e.Members.Count - 1).ToArray() } : e)),
        new PEdit("change-enum-attr", false, (s, rng) => WithEnum(s, e => e with { Description = $"d{100 + rng.Next(900)}" })),
        new PEdit("tiny-add-field", false, (s, rng) => s with { Tiny = s.Tiny with { Fields = [.. s.Tiny.Fields, ($"t{rng.Next(100000)}", rng.Next(100))] } }),
        new PEdit("tiny-remove-field", false, (s, _) => s with { Tiny = s.Tiny with { Fields = s.Tiny.Fields.Skip(1).ToArray() } }),
        new PEdit("tiny-change-attr", false, (s, rng) => s with { Tiny = s.Tiny with { Attr = s.Tiny.Attr is null ? rng.Next(100) : null } }),
        new PEdit("partial-change-comment", false, (s, rng) => rng.Next(2) == 0
            ? s with { Partial = s.Partial with { CommentA = $"part-a-{100 + rng.Next(900)}" } }
            : s with { Partial = s.Partial with { CommentB = $"part-b-{100 + rng.Next(900)}" } }),
        new PEdit("partial-change-value", false, (s, rng) => s with { Partial = s.Partial with { ValueB = 100 + rng.Next(900) } }),
        new PEdit("toggle-blank-line", true, (s, rng) => WithClass(s, rng, (c, _) => c with { BlankAfterFields = !c.BlankAfterFields })),
        new PEdit("reindent-field", true, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, _) => f with { Indent = f.Indent == 4 ? 8 : 4 }))),
        new PEdit("toggle-attr-line", true, (s, rng) => WithClass(s, rng, (c, r) => WithField(c, r, (f, _) => f with { AttrOwnLine = !f.AttrOwnLine }))),
        new PEdit("toggle-enum-layout", true, (s, _) => WithEnum(s, e => e with { OneLine = !e.OneLine })),
        new PEdit("move-partial-part", true, (s, rng) => MoveType(s, "Gamma#2", $"GammaMoved{rng.Next(100000)}.cs")),
        new PEdit("move-type-to-new-file", true, (s, rng) =>
        {
            var types = s.FileOf.Keys.Order(StringComparer.Ordinal).ToArray();
            return MoveType(s, types[rng.Next(types.Length)], $"Moved{rng.Next(100000)}.cs");
        }),
        new PEdit("extract-type", true, (s, rng) =>
        {
            var shared = s.FileOf.GroupBy(pair => pair.Value).Where(group => group.Count() > 1).SelectMany(group => group.Select(pair => pair.Key))
                .Order(StringComparer.Ordinal).ToArray();
            return shared.Length == 0 ? s : MoveType(s, shared[rng.Next(shared.Length)], $"Extracted{rng.Next(100000)}.cs");
        }),
        new PEdit("split-file", true, (s, rng) =>
        {
            var file = s.FileOf.Values.GroupBy(value => value).Where(group => group.Count() > 1).Select(group => group.Key).Order(StringComparer.Ordinal).FirstOrDefault();
            if (file is null) return s;
            var fileOf = new Dictionary<string, string>(s.FileOf, StringComparer.Ordinal);
            foreach (var type in s.FileOf.Where(pair => pair.Value == file).Select(pair => pair.Key))
            {
                fileOf[type] = $"Split{type.Replace("#", "Part", StringComparison.Ordinal)}{rng.Next(100000)}.cs";
            }
            return s with { FileOf = fileOf };
        }),
        new PEdit("move-and-modify", false, (s, rng) =>
        {
            var moved = WithClass(s, rng, (c, r) => WithField(c, r, (f, r2) => f with { Attr = 100 + r2.Next(900) }));
            var types = moved.Classes.Select(c => c.Name).ToArray();
            return MoveType(moved, types[rng.Next(types.Length)], $"MovedEdited{rng.Next(100000)}.cs");
        }),
    ];

    private static Dictionary<string, string> RenderPropertyState(PState state)
    {
        var blocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string type, IEnumerable<string> lines)
        {
            var file = state.FileOf[type];
            if (!blocks.TryGetValue(file, out var list))
            {
                list = ["namespace Fixture.Property;"];
                blocks[file] = list;
            }

            list.Add(string.Empty);
            list.AddRange(lines);
        }

        static string Trailing(string? comment) => comment is null ? string.Empty : $" // {comment}";

        foreach (var type in state.Classes)
        {
            var lines = new List<string> { $"public class {type.Name}", "{", $"    // {type.Comment}" };
            foreach (var field in type.Fields)
            {
                var indent = new string(' ', field.Indent);
                var attribute = field.Attr is int value ? $"[Range(0, {value})]" : null;
                if (attribute is not null && field.AttrOwnLine)
                {
                    lines.Add(indent + attribute);
                }

                var prefix = attribute is not null && !field.AttrOwnLine ? attribute + " " : string.Empty;
                lines.Add($"{indent}{prefix}private {field.Type} {field.Name} = {field.Value};{Trailing(field.Trailing)}");
            }

            if (type.Shared.Count > 0)
            {
                lines.Add($"    private int {string.Join(", ", type.Shared.Select(item => $"{item.Name} = {item.Value}"))};");
            }

            if (type.BlankAfterFields)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"    public int Prop {{ get; set; }}{Trailing(type.PropertyTrailing)}");
            foreach (var method in type.Methods)
            {
                lines.AddRange([$"    public int {method.Name}()", "    {", $"        return {method.Value};", $"    }}{Trailing(method.Trailing)}"]);
            }

            if (type.NestedValue is int nestedValue)
            {
                lines.AddRange(["    public class Inner", "    {", $"        // {type.NestedComment}", $"        private int deep = {nestedValue};", "    }"]);
            }

            lines.Add("}");
            Add(type.Name, lines);
        }

        var tinyAttribute = state.Tiny.Attr is int tinyValue ? $"[X({tinyValue})] " : string.Empty;
        var tinyFields = string.Concat(state.Tiny.Fields.Select(field => $" private int {field.Name} = {field.Value};"));
        Add(state.Tiny.Name, [$"{tinyAttribute}public class {state.Tiny.Name} {{{tinyFields} }}"]);

        Add(state.Partial.Name, [$"public partial class {state.Partial.Name}", "{", $"    // {state.Partial.CommentA}", $"    private int ga = {state.Partial.ValueA};", "}"]);
        Add($"{state.Partial.Name}#2", [$"public partial class {state.Partial.Name}", "{", $"    // {state.Partial.CommentB}", $"    private int gb = {state.Partial.ValueB};", "}"]);

        foreach (var type in state.Enums)
        {
            var header = $"[Description(\"{type.Description}\")] public enum {type.Name}";
            Add(type.Name, type.OneLine
                ? [$"{header} {{ {string.Join(", ", type.Members)} }}"]
                : [header, "{", .. type.Members.Select((member, i) => $"    {member}{(i < type.Members.Count - 1 ? "," : string.Empty)}"), "}"]);
        }

        return blocks.ToDictionary(pair => pair.Key, pair => string.Join("\n", pair.Value) + "\n", StringComparer.Ordinal);
    }

    private sealed record OwnedToken(
        string Value, bool IsPlaceholder, string Path, int Line, string LineText, IReadOnlySet<string> LineMembers, bool InOwnSpan,
        int Declaration, bool Exempt = false);

    /// <summary>
    /// Independent oracle for "no change disappears silently" (owned-text redesign). It uses no line diff
    /// and no positional pairing, and none of the production code.
    ///
    /// S's owned text is its declaration lines with every other symbol's span — other than S's own enclosing
    /// types — replaced by one placeholder token (whitespace dropped). A token is exempt when it is a
    /// placeholder, or a separator (',' ';') outside S's own span that sits next to a placeholder or, for a
    /// leaf S, next to S's own span text (a list separator that comes and goes with a neighbouring member).
    ///
    /// Matched symbols (same ID on both sides, and the base/target pairs of rename-candidate and
    /// signature-changed entries): the owned token sequences are aligned with a character LCS. Any unmatched
    /// non-exempt token means S's owned text changed; then an entry for S or an enclosing type must exist,
    /// and each changed owned line (below) must appear as a '-' (base) or '+' (target) line of those
    /// entries' hunks — unless the evidence was truncated and the budget limitation is present. The line
    /// requirement applies to owned lines that hold an unmatched non-exempt token and whose exempt-stripped
    /// text occurs more often on that side (a line diff must then drop or add one of them).
    ///
    /// Symbols on one side only (added or deleted): every owned line holding non-exempt text outside S's own
    /// span (attribute, modifiers, a trailing comment after a closing brace) must appear with the matching
    /// prefix in the hunks of S's entry or an enclosing type's entry. Text inside S's own span beyond its
    /// header may be omitted — header_only evidence records it as omitted lines.
    ///
    /// Member traces (rule b'): see <see cref="OracleWithoutMemberTraceLines"/>.
    ///
    /// One whitespace allowance: when S has an enclosing type whose whole owned text is exempt-equal and S's
    /// own span text is whitespace-equal, S's owned lines can only differ because enclosing-type text moved
    /// between lines (an attribute moved onto its own line), which is a whitespace change.
    /// </summary>
    private static void VerifyOwnedTextOracle(RealDiffOutcome outcome)
    {
        var baseById = outcome.Base.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var targetById = outcome.Target.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var budgetLimited = outcome.Result.Contract.Limitations.Contains(DiffContract.EvidenceBudgetExhaustedLimitation, StringComparer.Ordinal);

        List<OwnedToken> Owned(SymbolContract symbol, bool baseSide)
        {
            var sideById = baseSide ? baseById : targetById;
            var leaf = !sideById.Values.Any(other => other.ContainerId == symbol.SymbolId);
            return OracleMarkExempt(OracleWithoutMemberTraceLines(
                OracleOwnedTokens(symbol, sideById, baseSide ? outcome.BaseFiles : outcome.TargetFiles),
                symbol.SymbolId, sideById, baseSide ? targetById : baseById, outcome.Result, baseSide ? '-' : '+'), leaf);
        }

        DiffEntryContract[] EntriesFor(IReadOnlySet<string> owners) => outcome.Result.Contract.Entries
            .Where(entry => (entry.BaseSymbolId is not null && owners.Contains(entry.BaseSymbolId)) ||
                            (entry.TargetSymbolId is not null && owners.Contains(entry.TargetSymbolId)))
            .ToArray();

        // A symbol's declarations form a set (partial declarations can live in any files): align the base
        // declarations with the order of target declarations that leaves the fewest unmatched tokens.
        (List<OwnedToken> Base, List<OwnedToken> Target, HashSet<int> BaseChanged, HashSet<int> TargetChanged) Align(
            SymbolContract before, SymbolContract after)
        {
            var baseTokens = Owned(before, baseSide: true);
            var targetDeclarations = Owned(after, baseSide: false).GroupBy(token => token.Declaration).Select(group => group.ToList()).ToList();
            (List<OwnedToken>, List<OwnedToken>, HashSet<int>, HashSet<int>)? best = null;
            foreach (var order in OraclePermutations(targetDeclarations.Count))
            {
                var targetTokens = order.SelectMany(index => targetDeclarations[index]).ToList();
                var (baseChanged, targetChanged) = OracleUnmatched(baseTokens, targetTokens);
                if (best is null || baseChanged.Count + targetChanged.Count < best.Value.Item3.Count + best.Value.Item4.Count)
                {
                    best = (baseTokens, targetTokens, baseChanged, targetChanged);
                }
            }

            return best ?? (baseTokens, [], OracleUnmatched(baseTokens, []).Base, []);
        }

        void CheckMatched(SymbolContract before, SymbolContract after)
        {
            var (baseTokens, targetTokens, baseChanged, targetChanged) = Align(before, after);
            if (baseChanged.Count == 0 && targetChanged.Count == 0)
            {
                return;
            }

            var ancestors = OracleAncestors(before, baseById).Concat(OracleAncestors(after, targetById)).ToHashSet(StringComparer.Ordinal);
            var spanEqual = OracleSpanTokens(before, outcome.BaseFiles) == OracleSpanTokens(after, outcome.TargetFiles);
            if (spanEqual && ancestors.Any(ancestor =>
                    baseById.TryGetValue(ancestor, out var baseAncestor) && targetById.TryGetValue(ancestor, out var targetAncestor) &&
                    Align(baseAncestor, targetAncestor) is { BaseChanged.Count: 0, TargetChanged.Count: 0 }))
            {
                return;
            }

            var owners = ancestors.Append(before.SymbolId).Append(after.SymbolId).ToHashSet(StringComparer.Ordinal);
            var entries = EntriesFor(owners);
            Assert(entries.Length > 0,
                $"Owned text of {before.QualifiedName} changed but no entry exists for it or an enclosing type.\n{Describe(outcome.Result)}");

            var hunks = string.Concat(entries.SelectMany(entry => entry.Evidence).Select(evidence => "\n" + evidence.TextualHunk));
            var truncated = entries.SelectMany(entry => entry.Evidence).Any(evidence => evidence.Truncated);
            foreach (var (tokens, changed, otherTokens, prefix) in new[]
                     { (baseTokens, baseChanged, targetTokens, '-'), (targetTokens, targetChanged, baseTokens, '+') })
            {
                var lineKeys = OracleLineKeys(tokens, changed);
                var otherCounts = OracleLineKeys(otherTokens, new HashSet<int>()).GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Count());
                foreach (var group in lineKeys.GroupBy(item => item.Key))
                {
                    // A line must be shown when it holds a token the alignment could not match and lines with
                    // its text occur more often on this side (so any line diff has to drop one of them).
                    if (!group.Any(item => item.HasChange) || group.Count() <= otherCounts.GetValueOrDefault(group.Key))
                    {
                        continue;
                    }

                    var shown = group.Any(item => hunks.Contains($"\n{prefix}{item.LineText}\n", StringComparison.Ordinal));
                    Assert(shown || (truncated && budgetLimited),
                        $"Changed owned line of {before.QualifiedName} is not shown as '{prefix}' in any entry for it or an enclosing type: " +
                        $"'{group.First().LineText}'.\n{Describe(outcome.Result)}");
                }
            }
        }

        void CheckOneSided(SymbolContract symbol, bool baseSide)
        {
            var prefix = baseSide ? '-' : '+';
            var required = Owned(symbol, baseSide)
                .Where(token => !token.Exempt && !token.IsPlaceholder && !token.InOwnSpan)
                .Select(token => token.LineText)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (required.Length == 0)
            {
                return;
            }

            var owners = OracleAncestors(symbol, baseSide ? baseById : targetById).Append(symbol.SymbolId).ToHashSet(StringComparer.Ordinal);
            var entries = EntriesFor(owners);
            var hunks = string.Concat(entries.SelectMany(entry => entry.Evidence).Select(evidence => "\n" + evidence.TextualHunk));
            var truncated = entries.SelectMany(entry => entry.Evidence).Any(evidence => evidence.Truncated);
            foreach (var lineText in required)
            {
                Assert(hunks.Contains($"\n{prefix}{lineText}\n", StringComparison.Ordinal) || (truncated && budgetLimited),
                    $"Owned text of {(baseSide ? "deleted" : "added")} {symbol.QualifiedName} outside its span is not shown as '{prefix}' " +
                    $"in its entry or an enclosing type's: '{lineText}'.\n{Describe(outcome.Result)}");
            }
        }

        var pairedBase = new HashSet<string>(StringComparer.Ordinal);
        var pairedTarget = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbolId in baseById.Keys.Where(targetById.ContainsKey).Order(StringComparer.Ordinal))
        {
            CheckMatched(baseById[symbolId], targetById[symbolId]);
        }

        foreach (var entry in outcome.Result.Contract.Entries.Where(entry =>
                     entry.BaseSymbolId is not null && entry.TargetSymbolId is not null &&
                     !string.Equals(entry.BaseSymbolId, entry.TargetSymbolId, StringComparison.Ordinal)))
        {
            pairedBase.Add(entry.BaseSymbolId!);
            pairedTarget.Add(entry.TargetSymbolId!);
            CheckMatched(baseById[entry.BaseSymbolId!], targetById[entry.TargetSymbolId!]);
        }

        foreach (var symbolId in baseById.Keys.Where(id => !targetById.ContainsKey(id) && !pairedBase.Contains(id)).Order(StringComparer.Ordinal))
        {
            CheckOneSided(baseById[symbolId], baseSide: true);
        }

        foreach (var symbolId in targetById.Keys.Where(id => !baseById.ContainsKey(id) && !pairedTarget.Contains(id)).Order(StringComparer.Ordinal))
        {
            CheckOneSided(targetById[symbolId], baseSide: false);
        }
    }

    /// <summary>All orders of 0..count-1 (only a few for a handful of declarations; beyond 4, file order).</summary>
    private static IEnumerable<int[]> OraclePermutations(int count)
    {
        if (count > 4)
        {
            yield return Enumerable.Range(0, count).ToArray();
            yield break;
        }

        IEnumerable<int[]> Extend(int[] prefix) =>
            prefix.Length == count
                ? [prefix]
                : Enumerable.Range(0, count).Where(index => !prefix.Contains(index)).SelectMany(index => Extend([.. prefix, index]));

        foreach (var order in Extend([]))
        {
            yield return order;
        }
    }

    /// <summary>Marks exempt tokens: placeholders, and separators outside a leaf's own span that sit next to a
    /// placeholder or (for a leaf) next to the leaf's own span text.</summary>
    private static List<OwnedToken> OracleMarkExempt(List<OwnedToken> tokens, bool leaf)
    {
        bool Anchor(int index) => index >= 0 && index < tokens.Count && (tokens[index].IsPlaceholder || (leaf && tokens[index].InOwnSpan));
        return tokens.Select((token, index) => token with
        {
            Exempt = token.IsPlaceholder ||
                     (token.Value is "," or ";" && !(leaf && token.InOwnSpan) && (Anchor(index - 1) || Anchor(index + 1)))
        }).ToList();
    }

    /// <summary>
    /// The one oracle condition added for rule (b'): an owned line of S that only members existing on this
    /// side alone touch (members nested in S, e.g. a whole field declaration added or removed with its
    /// attribute and trailing comment) is that member's trace, not S's owned text — provided one of those
    /// members' own entries shows the line's original text as a '+' (added) or '-' (removed) hunk line. If no
    /// such entry shows it, the line stays in S's owned text and S (or an ancestor) must report it. The first
    /// and last lines of S's declarations (S's own header and closing text) never qualify.
    /// </summary>
    private static List<OwnedToken> OracleWithoutMemberTraceLines(
        List<OwnedToken> tokens,
        string symbolId,
        IReadOnlyDictionary<string, SymbolContract> sideById,
        IReadOnlyDictionary<string, SymbolContract> otherById,
        SymbolDiffResult result,
        char prefix)
    {
        var boundaries = sideById[symbolId].Declarations
            .SelectMany(declaration => new[]
            {
                (Path: declaration.Location.Path!.Replace('\\', '/'), Line: declaration.Location.Span.StartLine),
                (Path: declaration.Location.Path!.Replace('\\', '/'), Line: declaration.Location.Span.EndLine),
            })
            .ToHashSet();
        bool IsTrace(OwnedToken token) =>
            token.LineMembers.Count > 0 && !boundaries.Contains((token.Path, token.Line)) &&
            token.LineMembers.All(member => !otherById.ContainsKey(member) &&
                sideById.TryGetValue(member, out var memberSymbol) && OracleAncestors(memberSymbol, sideById).Contains(symbolId)) &&
            result.Contract.Entries.Any(entry =>
                (prefix == '-' ? entry.BaseSymbolId : entry.TargetSymbolId) is { } id && token.LineMembers.Contains(id) &&
                entry.Evidence.Any(evidence => ("\n" + evidence.TextualHunk).Contains($"\n{prefix}{token.LineText}\n", StringComparison.Ordinal)));

        var traceLines = tokens.Where(IsTrace).Select(token => (token.Path, token.Line)).ToHashSet();
        return tokens.Where(token => !traceLines.Contains((token.Path, token.Line))).ToList();
    }

    private static HashSet<string> OracleAncestors(SymbolContract symbol, IReadOnlyDictionary<string, SymbolContract> byId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var current = symbol;
        while (current.ContainerId is not null && byId.TryGetValue(current.ContainerId, out var container) && result.Add(container.SymbolId))
        {
            current = container;
        }

        return result;
    }

    private static string OracleSpanTokens(SymbolContract symbol, IReadOnlyDictionary<string, string> files) =>
        string.Concat(symbol.Declarations.OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
            .Select(declaration => files[declaration.Location.Path!.Replace('\\', '/')]
                .Substring(declaration.Location.Span.Start, declaration.Location.Span.Length))
            .SelectMany(text => text.Where(character => !char.IsWhiteSpace(character))));

    private static List<OwnedToken> OracleOwnedTokens(
        SymbolContract symbol, IReadOnlyDictionary<string, SymbolContract> byId, IReadOnlyDictionary<string, string> files)
    {
        var ancestors = OracleAncestors(symbol, byId);
        var tokens = new List<OwnedToken>();
        var declarationIndex = -1;
        foreach (var declaration in symbol.Declarations.OrderBy(item => item.Location.Path, StringComparer.Ordinal).ThenBy(item => item.Location.Span.Start))
        {
            declarationIndex++;
            var path = declaration.Location.Path!.Replace('\\', '/');
            var text = files[path];
            var others = byId.Values
                .Where(other => other.SymbolId != symbol.SymbolId && !ancestors.Contains(other.SymbolId))
                .SelectMany(other => other.Declarations)
                .Where(other => other.Location.Path!.Replace('\\', '/') == path)
                .Select(other => (Id: other.SymbolId, Start: other.Location.Span.Start, End: other.Location.Span.Start + other.Location.Span.Length))
                .ToArray();
            bool Masked(int position) => others.Any(other => position >= other.Start && position < other.End);
            var ownSpans = symbol.Declarations.Where(own => own.Location.Path!.Replace('\\', '/') == path)
                .Select(own => (Start: own.Location.Span.Start, End: own.Location.Span.Start + own.Location.Span.Length))
                .ToArray();
            bool Own(int position) => ownSpans.Any(own => position >= own.Start && position < own.End);

            var lines = text.Split('\n');
            var lineStart = 0;
            for (var line = 1; line < declaration.Location.Span.StartLine; line++)
            {
                lineStart += lines[line - 1].Length + 1;
            }

            var first = true;
            for (var line = declaration.Location.Span.StartLine; line <= declaration.Location.Span.EndLine; line++)
            {
                var lineText = lines[line - 1];
                var lineEnd = lineStart + lineText.Length;
                var lineMembers = others.Where(other => other.Start < lineEnd && other.End > lineStart)
                    .Select(other => other.Id).ToHashSet(StringComparer.Ordinal);
                for (var column = 0; column < lineText.Length; column++)
                {
                    var position = lineStart + column;
                    if (Masked(position))
                    {
                        if (first || !Masked(position - 1))
                        {
                            tokens.Add(new OwnedToken("<member>", true, path, line, lineText, lineMembers, Own(position), declarationIndex));
                        }
                    }
                    else if (!char.IsWhiteSpace(lineText[column]))
                    {
                        tokens.Add(new OwnedToken(lineText[column].ToString(), false, path, line, lineText, lineMembers, Own(position), declarationIndex));
                    }

                    first = false;
                }

                lineStart += lineText.Length + 1;
            }
        }

        return tokens;
    }

    /// <summary>Aligns two owned token sequences with an LCS and returns the indices of unmatched tokens that
    /// are not exempt, per side.</summary>
    private static (HashSet<int> Base, HashSet<int> Target) OracleUnmatched(IReadOnlyList<OwnedToken> before, IReadOnlyList<OwnedToken> after)
    {
        var n = before.Count;
        var m = after.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = before[i].Value == after[j].Value ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var baseUnmatched = new HashSet<int>();
        var targetUnmatched = new HashSet<int>();
        var a = 0;
        var b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && before[a].Value == after[b].Value)
            {
                a++;
                b++;
            }
            else if (b >= m || (a < n && lcs[a + 1, b] >= lcs[a, b + 1]))
            {
                if (!before[a].Exempt) baseUnmatched.Add(a);
                a++;
            }
            else
            {
                if (!after[b].Exempt) targetUnmatched.Add(b);
                b++;
            }
        }

        return (baseUnmatched, targetUnmatched);
    }

    /// <summary>Per owned line (in file order): its text without exempt tokens, its original text, and whether
    /// it holds one of the <paramref name="changed"/> token indices.</summary>
    private static List<(string Key, string LineText, bool HasChange)> OracleLineKeys(IReadOnlyList<OwnedToken> tokens, IReadOnlySet<int> changed) =>
        tokens.Select((token, index) => (Token: token, Index: index))
            .GroupBy(item => (item.Token.Path, item.Token.Line))
            .Where(group => !group.All(item => item.Token.Exempt))
            .Select(group => (
                Key: string.Concat(group.Where(item => !item.Token.Exempt).Select(item => item.Token.Value)),
                LineText: group.First().Token.LineText,
                HasChange: group.Any(item => changed.Contains(item.Index))))
            .ToList();

    // -------------------------------------------------------------------------------------------------------

    private static SymbolContract AdHocSymbol(
        string source, string path, string projectIdentity, string kind, string name, string qualifiedName, string signature,
        string declarationText, string? containerId = null)
    {
        var location = FragmentLocation(source, path, declarationText);
        var identity = new SymbolIdentityContract(projectIdentity, AnalysisKey, kind, qualifiedName, 0, [], null);
        var symbolId = DeterministicSymbolId.Create(identity);
        return new SymbolContract(symbolId, "project-adhoc", AnalysisKey, kind, name, qualifiedName, signature, "public", containerId, 0,
            IdentityQuality.Semantic, [], null, [new DeclarationContract(symbolId, location, DocumentKind.Source)], []);
    }

    private static LocationContract FragmentLocation(string source, string path, string fragment)
    {
        var start = source.IndexOf(fragment, StringComparison.Ordinal);
        Assert(start >= 0, $"Fragment not found in '{path}': {fragment}");
        var bytes = new UTF8Encoding(false).GetBytes(source);
        var hash = HashUtf8Bytes(bytes);
        return new LocationContract($"file_{path}", hash, new TextSpanContract(start, fragment.Length, LineAt(source, start), LineAt(source, start + fragment.Length)), path, null);
    }

    private static SymbolDiffSnapshot BuildSnapshot(string snapshotId, string path, string source, IReadOnlyList<SymbolContract> symbols)
    {
        var bytes = new UTF8Encoding(false).GetBytes(source);
        var hash = HashUtf8Bytes(bytes);
        return new SymbolDiffSnapshot(snapshotId, hash, DateTimeOffset.UtcNow, Coverage(), symbols, [new DiffSourceDocument(path, bytes, "utf-8", hash)], []);
    }

    // ------------------------------------------------------------------------------------------------------

    private static void ContractRoundTripAndUnknownKind(DiffContract contract)
    {
        var json = ContractJson.Serialize(contract);
        var restored = ContractJson.Deserialize<DiffContract>(json);
        Assert(restored.Entries.Any(entry => entry.Kind == DiffKind.RemarkChanged) &&
               restored.Entries.Any(entry => entry.Kind == DiffKind.RenameCandidate),
            "New diff kinds must round-trip without changing schema version.");
        ThrowsContract(
            () => ContractJson.Deserialize<DiffContract>(json.Replace("\"rename_candidate\"", "\"unknown_future_kind\"", StringComparison.Ordinal)),
            "CONTRACT_INVALID");
    }

    private static void ErrorCases(
        GitBaselineProvider provider,
        SymbolDiffService service,
        GitDiffFixture fixture,
        SnapshotModel baseModel,
        SymbolDiffSnapshot current,
        SnapshotModel currentModel)
    {
        ThrowsDiff(() => provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "", Coverage(), baseModel.Symbols, baseModel.Remarks)), DiffErrorCodes.BaseRequired);
        ThrowsDiff(() => provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "branch-that-does-not-exist", Coverage(), baseModel.Symbols, baseModel.Remarks)), DiffErrorCodes.RevisionNotFound);
        ThrowsDiff(() => new SessionBaselineProvider().Capture(new SessionSnapshotDiffRequest(
            Path.Combine(fixture.RootPath, "missing-store"), "missing-session", Coverage(), baseModel.Symbols, baseModel.Remarks)), DiffErrorCodes.SessionBaseMissing);

        var staleSymbol = baseModel.Symbols[0];
        var staleDeclaration = staleSymbol.Declarations[0] with
        {
            Location = staleSymbol.Declarations[0].Location with { ContentHash = $"sha256:{new string('f', 64)}" }
        };
        var staleModel = baseModel with
        {
            Symbols = [staleSymbol with { Declarations = [staleDeclaration] }, .. baseModel.Symbols.Skip(1)]
        };
        var staleBase = provider.CaptureRevision(new GitRevisionSnapshotRequest(
            fixture.RepositoryPath, "base", Coverage(), staleModel.Symbols, staleModel.Remarks));
        ThrowsDiff(() => service.Compare(new SymbolDiffRequest(provider.CreateBaseline(staleBase, current), staleBase, current)), DiffErrorCodes.SourceStale);

        var invalidSymbol = currentModel.Symbols[0];
        var invalidDeclaration = invalidSymbol.Declarations[0] with
        {
            Location = invalidSymbol.Declarations[0].Location with
            {
                Span = invalidSymbol.Declarations[0].Location.Span with { Start = invalidSymbol.Declarations[0].Location.Span.Start + 1 }
            }
        };
        var invalidSnapshot = current with
        {
            Symbols = [invalidSymbol with { Declarations = [invalidDeclaration] }, .. currentModel.Symbols.Skip(1)]
        };
        ThrowsDiff(() => service.Compare(new SymbolDiffRequest(provider.CreateBaseline(current, invalidSnapshot), current, invalidSnapshot)), DiffErrorCodes.InvalidSnapshot);
    }

    private static async Task ModeSwitchRejectsStaleResponse(
        SymbolDiffRequest vcsRequest,
        SymbolDiffResult vcsResult,
        SymbolDiffRequest sessionRequest,
        SymbolDiffResult sessionResult)
    {
        var coordinator = new DiffRequestCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequest = coordinator.CompareAsync(vcsRequest, async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return vcsResult;
        });
        var current = await coordinator.CompareAsync(sessionRequest, (_, _) => Task.FromResult(sessionResult)).ConfigureAwait(false);
        Assert(current.Contract.Baseline.Kind == BaselineKind.Session, "The latest selected baseline mode must complete.");
        gate.SetResult();
        await ThrowsDiffAsync(async () => await oldRequest.ConfigureAwait(false), DiffErrorCodes.StaleResponse).ConfigureAwait(false);
    }

    private static bool Has(SymbolDiffResult result, DiffKind kind, string name) =>
        result.Changes.Any(change => change.Kind == kind && (change.BaseSymbol?.Name == name || change.TargetSymbol?.Name == name));

    private static SnapshotModel Model(
        string source,
        string existingValue,
        string remark,
        string bodyValue,
        bool includeRemoved,
        string signatureType,
        string renameName,
        bool includeAdded)
    {
        var symbols = new List<SymbolContract>
        {
            Symbol(source, "Existing", "public static string Existing()", $"public static string Existing()\n    {{\n        return \"{existingValue}\";\n    }}", []),
            Symbol(source, "Signature", $"public static string Signature({signatureType} value)", $"public static string Signature({signatureType} value) => value.ToString();",
                [new ParameterIdentityContract(signatureType == "int" ? "System.Int32" : "System.String", ParameterRefKind.None)]),
            Symbol(source, renameName, $"public static string {renameName}()", $"public static string {renameName}() => \"same\";", []),
            Symbol(source, "Body", "public static string Body()", $"public static string Body() => \"{bodyValue}\";", [])
        };
        if (includeRemoved) symbols.Add(Symbol(source, "Removed", "public static string Removed()", "public static string Removed() => \"gone\";", []));
        if (includeAdded) symbols.Add(Symbol(source, "AddedDuringSession", "public static string AddedDuringSession()", "public static string AddedDuringSession() => \"added\";", []));
        var existing = symbols.Single(symbol => symbol.Name == "Existing");
        var remarkText = $"/// <summary>{remark}</summary>";
        return new SnapshotModel(symbols.OrderBy(symbol => symbol.Name, StringComparer.Ordinal).ToArray(),
            [new DiffRemarkSnapshot(existing.SymbolId, Location(source, remarkText))]);
    }

    private static SymbolContract Symbol(
        string source,
        string name,
        string signature,
        string declaration,
        IReadOnlyList<ParameterIdentityContract> parameters)
    {
        var identity = new SymbolIdentityContract("project-identity", AnalysisKey, "method", $"Fixture.ReviewTarget.{name}", 0, parameters, null);
        var symbolId = DeterministicSymbolId.Create(identity);
        return new SymbolContract(symbolId, "project-001", AnalysisKey, "method", name, $"Fixture.ReviewTarget.{name}", signature,
            "public", null, 0, IdentityQuality.Semantic, parameters, null,
            [new DeclarationContract(symbolId, Location(source, declaration), DocumentKind.Source)], []);
    }

    private static LocationContract Location(string source, string value)
    {
        var start = source.IndexOf(value, StringComparison.Ordinal);
        Assert(start >= 0, $"Fixture fragment not found: {value}");
        return new LocationContract("file-review-target", HashUtf8(source),
            new TextSpanContract(start, value.Length, LineAt(source, start), LineAt(source, start + value.Length)), SourcePath, null);
    }

    private static string FixtureSource(
        string existingValue,
        string remark,
        string bodyValue,
        bool includeRemoved,
        string signatureType,
        string renameName,
        bool includeAdded)
    {
        var removed = includeRemoved ? "\n    public static string Removed() => \"gone\";\n" : string.Empty;
        var added = includeAdded ? "\n    public static string AddedDuringSession() => \"added\";\n" : string.Empty;
        return $$"""
            namespace Fixture;
            public static class ReviewTarget
            {
                public const string Emoji = "😀";
                /// <summary>{{remark}}</summary>
                public static string Existing()
                {
                    return "{{existingValue}}";
                }
            {{removed}}
                public static string Signature({{signatureType}} value) => value.ToString();
                public static string {{renameName}}() => "same";
                public static string Body() => "{{bodyValue}}";
            {{added}}}
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ContainerFixtureSource(string commentBeforeSecond, bool memberOrder, string firstValue = "same")
    {
        var firstLine = $"    public static string First() => \"{firstValue}\";";
        var secondLine = "    public static string Second() => \"same\";";
        var commentLine = $"    {commentBeforeSecond}";
        var members = memberOrder
            ? new[] { secondLine, commentLine, firstLine }
            : [firstLine, commentLine, secondLine];
        var lines = new List<string> { "namespace Fixture.Container;", "public static class ContainerTarget", "{" };
        lines.AddRange(members);
        lines.Add("}");
        lines.Add(string.Empty);
        return string.Join("\n", lines);
    }

    private static SnapshotModel ContainerModel(string source, string commentBeforeSecond, bool memberOrder, string firstValue = "same")
    {
        var firstDeclaration = $"public static string First() => \"{firstValue}\";";
        var secondDeclaration = "public static string Second() => \"same\";";
        var members = memberOrder
            ? $"{secondDeclaration}\n    {commentBeforeSecond}\n    {firstDeclaration}"
            : $"{firstDeclaration}\n    {commentBeforeSecond}\n    {secondDeclaration}";
        var classText = $"public static class ContainerTarget\n{{\n    {members}\n}}";

        var containerIdentity = new SymbolIdentityContract("container-project-identity", AnalysisKey, "class", "Fixture.Container.ContainerTarget", 0, [], null);
        var containerId = DeterministicSymbolId.Create(containerIdentity);
        var containerSymbol = new SymbolContract(containerId, "container-project", AnalysisKey, "class", "ContainerTarget",
            "Fixture.Container.ContainerTarget", "public static class ContainerTarget", "public", null, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(containerId, Location(source, classText), DocumentKind.Source)], []);

        var first = ContainerMember(source, "First", firstDeclaration, containerId);
        var second = ContainerMember(source, "Second", secondDeclaration, containerId);
        return new SnapshotModel([containerSymbol, first, second], []);
    }

    private static SymbolContract ContainerMember(string source, string name, string declarationText, string containerId)
    {
        var identity = new SymbolIdentityContract("container-project-identity", AnalysisKey, "method", $"Fixture.Container.ContainerTarget.{name}", 0, [], null);
        var symbolId = DeterministicSymbolId.Create(identity);
        return new SymbolContract(symbolId, "container-project", AnalysisKey, "method", name,
            $"Fixture.Container.ContainerTarget.{name}", declarationText.Split(" =>")[0].TrimEnd(),
            "public", containerId, 0, IdentityQuality.Semantic, [], null,
            [new DeclarationContract(symbolId, Location(source, declarationText), DocumentKind.Source)], []);
    }

    private static CoverageContract Coverage() => new(
        "static-csharp-selected-configuration", CoverageLevel.CompleteWithinScope, 1, 0, 0, 0, [], ["dynamic-references-not-covered"], false);

    private static string HashUtf8(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static string HashUtf8Bytes(byte[] bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

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
            else if (text[index] == '\n') line++;
        }
        return line;
    }

    private static void ThrowsDiff(Action action, string code)
    {
        try { action(); }
        catch (DiffException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected DiffException '{code}'.");
    }

    private static async Task ThrowsDiffAsync(Func<Task> action, string code)
    {
        try { await action().ConfigureAwait(false); }
        catch (DiffException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected DiffException '{code}'.");
    }

    private static void ThrowsContract(Action action, string code)
    {
        try { action(); }
        catch (ContractValidationException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected ContractValidationException '{code}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record SnapshotModel(IReadOnlyList<SymbolContract> Symbols, IReadOnlyList<DiffRemarkSnapshot> Remarks);

    private sealed class GitDiffFixture : IDisposable
    {
        public GitDiffFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"code-virtualize-diff-{Guid.NewGuid():N}");
            RepositoryPath = Path.Combine(RootPath, "repo");
            Directory.CreateDirectory(RepositoryPath);
            Git("init", "--initial-branch=main");
            Git("config", "user.email", "fixture@example.invalid");
            Git("config", "user.name", "Code Virtualize Fixture");
        }

        public string RootPath { get; }
        public string RepositoryPath { get; }

        public void Write(string source) =>
            File.WriteAllText(Path.Combine(RepositoryPath, SourcePath), source, new UTF8Encoding(false));

        public string Read() => File.ReadAllText(Path.Combine(RepositoryPath, SourcePath), new UTF8Encoding(false, true));

        public void CommitAndTag(string tag)
        {
            Git("add", "--", SourcePath);
            Git("commit", "-m", "fixture base");
            Git("tag", tag);
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath)) return;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!root.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Diff fixture cleanup escaped the temporary directory.");
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }

        private void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepositoryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git fixture command.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Git fixture command failed: {output} {error}");
        }
    }
}