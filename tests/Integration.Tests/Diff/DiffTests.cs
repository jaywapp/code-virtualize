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