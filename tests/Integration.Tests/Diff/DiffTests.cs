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
        ChangedLineCoverageSafetyNetPropertyTest();
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

        Assert(change.Contract.Evidence.Count == 2, $"Expected exactly two evidence items (B.cs removed, C.cs added); got {change.Contract.Evidence.Count}.");
        Assert(change.Contract.Evidence.All(item => item.TextMode == DiffEvidenceTextMode.HeaderOnly),
            "B.cs's removal and C.cs's addition must each be reported as header_only, never as a line-hunk comparing one unrelated file's text to the other's (M4).");
        Assert(change.Contract.Evidence.Any(item => item.Kind == "declaration-removed" && item.BaseContentHash is not null && item.TargetContentHash is null),
            "B.cs's declaration must be reported as removed.");
        Assert(change.Contract.Evidence.Any(item => item.Kind == "declaration-added" && item.TargetContentHash is not null && item.BaseContentHash is null),
            "C.cs's declaration must be reported as added.");
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
        // attribute on the same physical line is outside it, and there is no container symbol here to catch
        // it via self text either. Before the changed-line safety net, this edit produced zero entries even
        // though the file's digest genuinely changed.
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
        Assert(evidence.TextMode == DiffEvidenceTextMode.LineHunks && evidence.TextualHunk!.Contains("Range(0, 99)", StringComparison.Ordinal),
            "The safety-net entry's evidence must show the actual changed text, not just a bare fingerprint.");
        Assert(result.Contract.Limitations.Contains("diff-span-adjacent-line-attributed", StringComparer.Ordinal),
            "The diff must flag that this entry came from the changed-line safety net, not an ordinary per-symbol comparison.");
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
        // Recorded outcome: unlike the same-line cases above, an attribute on its OWN line does not fall
        // within the field's own (single-line) span, but it DOES fall within the containing class's self
        // text range (member line-exclusion only removes the field's own line, not the attribute's line
        // above it). So this case is already caught by the pre-existing container self-text mechanism
        // (M3), and is attributed to the class, not to the field itself.
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

        // No symbols at all reference this file, so no ordinary per-symbol entry will ever mention it - the
        // safety net's own whole-file diff is the only thing that could say anything about it, and here that
        // diff itself exceeds the line-diff budget (same near-total-rewrite shape as M1's regression test).
        var baseSnapshot = new SymbolDiffSnapshot("n1-budget-base", HashUtf8Bytes(baseBytes), DateTimeOffset.UtcNow, Coverage(), [],
            [new DiffSourceDocument("Untracked.cs", baseBytes, "utf-8", HashUtf8Bytes(baseBytes))], []);
        var targetSnapshot = new SymbolDiffSnapshot("n1-budget-target", HashUtf8Bytes(targetBytes), DateTimeOffset.UtcNow, Coverage(), [],
            [new DiffSourceDocument("Untracked.cs", targetBytes, "utf-8", HashUtf8Bytes(targetBytes))], []);
        var baseline = new BaselineContract(BaselineKind.Vcs, "git", "n1-budget-base", "n1-budget-target", null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);

        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count == 0, "Sanity: no symbols reference this file, so no ordinary entry exists for it.");
        Assert(result.Contract.Limitations.Contains("diff-unattributed-change-budget-exceeded", StringComparer.Ordinal),
            "When the whole-file line diff itself hits the edit-distance/trace budget and no entry already covers that file, this must be surfaced explicitly as a limitation, not a silently complete-looking empty diff.");
        Assert(result.Contract.Coverage.Truncated && result.Contract.Coverage.Level == CoverageLevel.Partial,
            "Coverage must also reflect the truncation explicitly, not just the limitation string.");
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

        // The attribute and the field are on the SAME physical line, so even the container's self text
        // excludes this whole line (member-line exclusion is line-granular) - neither the leaf field nor the
        // containing class's self-text entry can see this change without the safety net.
        fixture.Write(Source(99));
        var targetBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(fixture.RepositoryPath, store));
        var targetSpeed = targetBuild.Symbols.Single(symbol => symbol.Kind == "field" && symbol.Name == "speed");
        Assert(targetSpeed.SymbolId == baseSpeed.SymbolId, "Sanity: only the attribute argument changed, so the field's identity must be stable.");
        var targetSnapshot = provider.CaptureWorkingTree(new GitWorkingTreeSnapshotRequest(
            fixture.RepositoryPath, targetBuild.Manifest.Coverage, targetBuild.Symbols, []));

        var result = service.Compare(new SymbolDiffRequest(provider.CreateBaseline(baseSnapshot, targetSnapshot), baseSnapshot, targetSnapshot));

        Assert(result.Changes.Count != 0,
            "With symbols produced by the real C# analyzer (not a hand-built test double), a same-line attribute-only edit must not silently vanish end to end (N1), even though the field is inside an indexed container class.");
        Assert(result.Changes.Any(change => change.BaseSymbol?.SymbolId == baseSpeed.SymbolId || change.TargetSymbol?.SymbolId == baseSpeed.SymbolId),
            "The change must be attributed to the speed field specifically (the innermost symbol covering that line).");
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
        Assert(result.Contract.Limitations.Contains("diff-unattributed-change-budget-exceeded", StringComparer.Ordinal),
            "X2: the whole-file safety-net diff hitting its own budget must be flagged even when an ordinary entry (M) already exists for the file - " +
            "that entry says nothing about whether OTHER changes (speed's attribute) were also missed.");
    }

    private static void RenamedFileAttributeChangeIsStillAttributed()
    {
        // X3: Old.cs -> New.cs (same class/field, moved to a differently-named file) while ALSO changing the
        // attribute argument on the same line as the field. Before the fix, the safety net only considered
        // the INTERSECTION of base and target paths, so a moved file's changes were entirely invisible.
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
            "X3: an attribute-only edit on a field whose file was also renamed/moved must not be silently dropped just because the safety net only looked at same-named paths.");
        var change = result.Changes.Single();
        Assert(change.BaseSymbol?.SymbolId == baseSpeed.SymbolId && change.TargetSymbol?.SymbolId == baseSpeed.SymbolId,
            "The change must still be attributed to speed even though its file moved.");
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
            "Formatting-only safety-net evidence must be fingerprint-only, not a rendered hunk.");
    }

    // --- Mandatory invariant property test (X1-X4 confirmation) -------------------------------------------

    private sealed record PropertyTemplateState(
        int AttrValue, string Comment, int AValue, int BValue, int MethodValue,
        IReadOnlyList<string> EnumMembers, bool EnumOneLine, bool BlankLineAfterFieldB,
        int Line3IndentSpaces, int Line4IndentSpaces, string FileName);

    private sealed record PropertyEdit(string Name, bool WhitespaceOnly, Func<PropertyTemplateState, Random, PropertyTemplateState> Apply);

    private static void ChangedLineCoverageSafetyNetPropertyTest()
    {
        const int seed = 20260924;
        const int iterations = 520;
        var rng = new Random(seed);
        var edits = BuildPropertyEditCatalog();
        var failures = new List<string>();

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cv-safety-net-property-{Guid.NewGuid():N}");
        var workspace = Path.Combine(temporaryRoot, "workspace");
        var store = Path.Combine(temporaryRoot, "store");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "Property.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");

        try
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var initial = new PropertyTemplateState(
                    AttrValue: rng.Next(0, 100),
                    Comment: $"units-{rng.Next(0, 100)}",
                    AValue: rng.Next(0, 100),
                    BValue: rng.Next(0, 100),
                    MethodValue: rng.Next(0, 100),
                    EnumMembers: rng.Next(2) == 0 ? ["Red", "Green"] : ["Red", "Green", "Blue"],
                    EnumOneLine: rng.Next(2) == 0,
                    BlankLineAfterFieldB: true,
                    Line3IndentSpaces: 4,
                    Line4IndentSpaces: 4,
                    FileName: "Property.cs");

                var editCount = rng.Next(1, 4);
                var chosenEdits = new List<PropertyEdit>();
                for (var e = 0; e < editCount; e++)
                {
                    chosenEdits.Add(edits[rng.Next(edits.Count)]);
                }

                var target = initial;
                foreach (var edit in chosenEdits)
                {
                    target = edit.Apply(target, rng);
                }

                var allWhitespaceOnly = chosenEdits.All(edit => edit.WhitespaceOnly);
                var editNames = string.Join(",", chosenEdits.Select(edit => edit.Name));

                try
                {
                    RunPropertyIteration(workspace, store, initial, target, allWhitespaceOnly);
                }
                catch (Exception exception)
                {
                    failures.Add($"iteration={iteration} seed={seed} edits=[{editNames}]: {exception.Message}");
                    if (failures.Count >= 5)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }

        Assert(failures.Count == 0,
            $"Changed-line coverage safety net property test found {failures.Count} counterexample(s) out of {iterations} deterministic (seed={seed}) iterations:\n{string.Join("\n", failures)}");
    }

    private static List<PropertyEdit> BuildPropertyEditCatalog() =>
    [
        new PropertyEdit("change-attr-value", false, (s, rng) => s with { AttrValue = rng.Next(0, 1000) }),
        new PropertyEdit("change-comment", false, (s, rng) => s with { Comment = $"units-{rng.Next(0, 1000)}" }),
        new PropertyEdit("change-field-a-value", false, (s, rng) => s with { AValue = rng.Next(0, 1000) }),
        new PropertyEdit("change-method-body", false, (s, rng) => s with { MethodValue = rng.Next(0, 1000) }),
        new PropertyEdit("add-enum-member", false, (s, rng) => s with { EnumMembers = [.. s.EnumMembers, $"Member{rng.Next(1000, 9999)}"] }),
        new PropertyEdit("remove-enum-member", false, (s, _) => s.EnumMembers.Count > 1 ? s with { EnumMembers = s.EnumMembers.Take(s.EnumMembers.Count - 1).ToArray() } : s),
        new PropertyEdit("delete-blank-line", true, (s, _) => s with { BlankLineAfterFieldB = false }),
        new PropertyEdit("add-blank-line", true, (s, _) => s with { BlankLineAfterFieldB = true }),
        new PropertyEdit("reindent-line3", true, (s, _) => s with { Line3IndentSpaces = s.Line3IndentSpaces == 4 ? 8 : 4 }),
        new PropertyEdit("reindent-line4", true, (s, _) => s with { Line4IndentSpaces = s.Line4IndentSpaces == 4 ? 8 : 4 }),
        new PropertyEdit("rename-file", true, (s, rng) => s with { FileName = $"Renamed{rng.Next(0, 100000)}.cs" }),
    ];

    private static string RenderPropertyTemplate(PropertyTemplateState state)
    {
        var indent3 = new string(' ', state.Line3IndentSpaces);
        var indent4 = new string(' ', state.Line4IndentSpaces);
        var lines = new List<string>
        {
            "namespace Fixture.Property;",
            "public class PropertyTarget",
            "{",
            $"{indent3}[Range(0, {state.AttrValue})] private int speed; // {state.Comment}",
            $"{indent4}private int a = {state.AValue}, b = {state.BValue};",
        };
        if (state.BlankLineAfterFieldB)
        {
            lines.Add(string.Empty);
        }
        lines.Add("    public static int Compute()");
        lines.Add("    {");
        lines.Add($"        return {state.MethodValue};");
        lines.Add("    }");
        lines.Add("}");
        lines.Add(string.Empty);
        if (state.EnumOneLine)
        {
            lines.Add($"public enum ColorProperty {{ {string.Join(", ", state.EnumMembers)} }}");
        }
        else
        {
            lines.Add("public enum ColorProperty");
            lines.Add("{");
            for (var i = 0; i < state.EnumMembers.Count; i++)
            {
                lines.Add($"    {state.EnumMembers[i]}{(i < state.EnumMembers.Count - 1 ? "," : string.Empty)}");
            }
            lines.Add("}");
        }
        lines.Add(string.Empty);
        return string.Join("\n", lines);
    }

    private static void RunPropertyIteration(string workspace, string store, PropertyTemplateState initial, PropertyTemplateState target, bool allWhitespaceOnly)
    {
        foreach (var existing in Directory.GetFiles(workspace, "*.cs"))
        {
            File.Delete(existing);
        }

        var baseSourceText = RenderPropertyTemplate(initial);
        File.WriteAllText(Path.Combine(workspace, initial.FileName), baseSourceText, new UTF8Encoding(false));
        var baseBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
        var baseSnapshot = new SymbolDiffSnapshot(
            $"prop-base-{Guid.NewGuid():N}", baseBuild.Manifest.InputFingerprint, DateTimeOffset.UtcNow, baseBuild.Manifest.Coverage,
            baseBuild.Symbols, ReadSourceDocuments(workspace, baseBuild.Manifest), []);

        if (!string.Equals(initial.FileName, target.FileName, StringComparison.Ordinal))
        {
            File.Delete(Path.Combine(workspace, initial.FileName));
        }

        var targetSourceText = RenderPropertyTemplate(target);
        File.WriteAllText(Path.Combine(workspace, target.FileName), targetSourceText, new UTF8Encoding(false));
        var targetBuild = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
        var targetSnapshot = new SymbolDiffSnapshot(
            $"prop-target-{Guid.NewGuid():N}", targetBuild.Manifest.InputFingerprint, DateTimeOffset.UtcNow, targetBuild.Manifest.Coverage,
            targetBuild.Symbols, ReadSourceDocuments(workspace, targetBuild.Manifest), []);

        var baseline = new BaselineContract(
            BaselineKind.Vcs, "git", baseSnapshot.SnapshotId, targetSnapshot.SnapshotId, null, DateTimeOffset.UtcNow, baseSnapshot.InputFingerprint);
        var result = new SymbolDiffService().Compare(new SymbolDiffRequest(baseline, baseSnapshot, targetSnapshot));
        result.Contract.Validate();

        VerifyChangedLineCoverage(result, initial.FileName, baseSourceText, target.FileName, targetSourceText);

        if (allWhitespaceOnly)
        {
            Assert(!result.Changes.Any(change => change.Kind == DiffKind.BodyChanged),
                "Whitespace-only edits alone must never produce a body_changed entry.");
        }
    }

    private static IReadOnlyList<DiffSourceDocument> ReadSourceDocuments(string workspace, ManifestContract manifest) =>
        manifest.Files.Select(file =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(workspace, file.Path));
            return new DiffSourceDocument(file.Path, bytes, file.Encoding, file.ContentHash);
        }).ToArray();

    /// <summary>
    /// Invariant 1 (X1-X4 confirmation): every SUBSTANTIVE line-level change between the base and target file
    /// text is covered by some reported entry's declaration range on at least one side, or the file carries
    /// the diff-unattributed-change-budget-exceeded limitation. Uses an independent (non-Myers, DP-based LCS)
    /// line diff so this check does not share an implementation with the code under test, but deliberately
    /// mirrors the production ATTRIBUTION semantics it is verifying (X1's per-pair positional delete/insert
    /// pairing within each changed run; "covered on either side is sufficient", not both):
    ///
    /// - A whitespace-only pair (or a lone blank line, added or removed) is exempt from the coverage
    ///   requirement entirely - production may legitimately suppress it outright (a container's own
    ///   self-text ignores whitespace-only differences, X4/U2 rule 3) or report it as formatting_only; either
    ///   is acceptable and neither is required by this invariant.
    /// - A substantive pair (or lone line) only needs coverage on ONE side, not both: e.g. appending a new
    ///   enum member on a line shared with unrelated existing members is already fully explained by that
    ///   member's own Added entry (target side only) - the untouched siblings on the same line correctly get
    ///   no entry of their own (M2), so the base side of that same line is never independently covered, and
    ///   must not be required to be.
    /// </summary>
    private static void VerifyChangedLineCoverage(SymbolDiffResult result, string basePath, string baseSourceText, string targetPath, string targetSourceText)
    {
        var baseLines = baseSourceText.Split('\n');
        var targetLines = targetSourceText.Split('\n');
        var edits = IndependentLineDiff(baseLines, targetLines);

        var hasBudgetLimitation = result.Contract.Limitations.Contains("diff-unattributed-change-budget-exceeded", StringComparer.Ordinal);

        var coveredBase = new HashSet<int>();
        var coveredTarget = new HashSet<int>();
        foreach (var entry in result.Contract.Entries)
        {
            foreach (var location in entry.BaseLocations.Where(item => string.Equals(item.Path, basePath, StringComparison.Ordinal)))
            {
                for (var line = location.Span.StartLine; line <= location.Span.EndLine; line++)
                {
                    coveredBase.Add(line);
                }
            }

            foreach (var location in entry.TargetLocations.Where(item => string.Equals(item.Path, targetPath, StringComparison.Ordinal)))
            {
                for (var line = location.Span.StartLine; line <= location.Span.EndLine; line++)
                {
                    coveredTarget.Add(line);
                }
            }
        }

        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == IndependentLineEditKind.Equal)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < edits.Count && edits[i].Kind != IndependentLineEditKind.Equal)
            {
                i++;
            }

            var deletes = new List<IndependentLineEdit>();
            var inserts = new List<IndependentLineEdit>();
            for (var j = start; j < i; j++)
            {
                (edits[j].Kind == IndependentLineEditKind.Delete ? deletes : inserts).Add(edits[j]);
            }

            var pairCount = Math.Min(deletes.Count, inserts.Count);
            for (var k = 0; k < pairCount; k++)
            {
                VerifyPair(deletes[k], inserts[k]);
            }
            for (var k = pairCount; k < deletes.Count; k++)
            {
                VerifyPair(deletes[k], null);
            }
            for (var k = pairCount; k < inserts.Count; k++)
            {
                VerifyPair(null, inserts[k]);
            }
        }

        void VerifyPair(IndependentLineEdit? delete, IndependentLineEdit? insert)
        {
            var baseLine = delete is { } d ? d.BaseIndex + 1 : (int?)null;
            var targetLine = insert is { } ins ? ins.TargetIndex + 1 : (int?)null;
            var baseText = delete is { } dt ? baseLines[dt.BaseIndex] : null;
            var targetText = insert is { } it ? targetLines[it.TargetIndex] : null;

            var whitespaceOnly = baseText is not null && targetText is not null
                ? PropertyNormalizeWhitespace(baseText) == PropertyNormalizeWhitespace(targetText)
                : PropertyNormalizeWhitespace(baseText ?? targetText ?? string.Empty).Length == 0;
            if (whitespaceOnly)
            {
                return;
            }

            var covered = (baseLine is int bl && coveredBase.Contains(bl)) || (targetLine is int tl && coveredTarget.Contains(tl));
            Assert(covered || hasBudgetLimitation,
                $"Changed line not covered by any entry and no budget limitation is present. " +
                $"BaseLine={baseLine?.ToString() ?? "-"} ('{baseText}'), TargetLine={targetLine?.ToString() ?? "-"} ('{targetText}').");
        }
    }

    private static string PropertyNormalizeWhitespace(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private enum IndependentLineEditKind { Equal, Delete, Insert }

    private sealed record IndependentLineEdit(IndependentLineEditKind Kind, int BaseIndex, int TargetIndex);

    /// <summary>Simple O(N*M) LCS-based line diff, deliberately independent of the production Myers
    /// implementation under test. Returns an ordered edit script (equal/delete/insert) covering every line
    /// of both files, so callers can group changes into runs and pair deletes/inserts positionally exactly
    /// as the production safety net does (X1), without sharing any code with it.</summary>
    private static List<IndependentLineEdit> IndependentLineDiff(IReadOnlyList<string> baseLines, IReadOnlyList<string> targetLines)
    {
        var n = baseLines.Count;
        var m = targetLines.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = baseLines[i] == targetLines[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<IndependentLineEdit>();
        var a = 0;
        var b = 0;
        while (a < n && b < m)
        {
            if (baseLines[a] == targetLines[b])
            {
                result.Add(new IndependentLineEdit(IndependentLineEditKind.Equal, a, b));
                a++;
                b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
            {
                result.Add(new IndependentLineEdit(IndependentLineEditKind.Delete, a, -1));
                a++;
            }
            else
            {
                result.Add(new IndependentLineEdit(IndependentLineEditKind.Insert, -1, b));
                b++;
            }
        }
        while (a < n)
        {
            result.Add(new IndependentLineEdit(IndependentLineEditKind.Delete, a, -1));
            a++;
        }
        while (b < m)
        {
            result.Add(new IndependentLineEdit(IndependentLineEditKind.Insert, -1, b));
            b++;
        }

        return result;
    }

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