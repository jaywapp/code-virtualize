using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Impact;
using CodeVirtualize.Core.Remarks;
using CodeVirtualize.CSharp;
using CodeVirtualize.CSharp.References;

namespace CodeVirtualize.CSharp.Tests.References;

public static class ReferenceFeatureTests
{
public static void Run(string fixtureRoot, string temporaryRoot)
{
    var workspace = Path.Combine(temporaryRoot, "impact-workspace");
    CopyDirectory(fixtureRoot, workspace);
    var store = Path.Combine(temporaryRoot, "impact-store");
    var build = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
    var load = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Load" &&
        symbol.Accessibility == "public" && symbol.Parameters.Count == 1 && declarationLine(symbol) == 12);
    var privateLoad = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Load" && symbol.GenericArity == 1);
    var use = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Use");
    var run = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Run" &&
        symbol.Declarations.Any(declaration => declaration.Location.Path?.EndsWith("CallSites.cs", StringComparison.Ordinal) == true));
    var worker = build.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Worker");
    var workerRun = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Run" &&
        symbol.Declarations.Any(declaration => declaration.Location.Path?.EndsWith("DynamicCandidates.cs", StringComparison.Ordinal) == true) &&
        declarationLine(symbol) == 12);

    var service = new CSharpImpactService();
    var loadImpact = service.Query(new ImpactQuery(
        workspace,
        store,
        [load.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 100, MaxSourceFiles: 100, PageSize: 100)));
    var expectedDirect = new HashSet<string>(StringComparer.Ordinal)
    {
        "navigation/CallSites.cs:7",
        "navigation/Contracts.cs:27"
    };
    var actualDirect = loadImpact.Results
        .Where(result => result.Kind == ReferenceKind.Static && result.Depth == 0)
        .Select(result => $"{result.Location.Path}:{result.Location.Span.StartLine}")
        .ToHashSet(StringComparer.Ordinal);
    var matched = expectedDirect.Count(item => actualDirect.Contains(item));
    var recall = (double)matched / expectedDirect.Count;
    var falsePositives = actualDirect.Except(expectedDirect, StringComparer.Ordinal).ToArray();
    Assert(recall == 1d, $"Static reference recall must be 1.0, actual {recall:F3}.");
    Assert(falsePositives.Length == 0, $"Static reference false positives must be zero: {string.Join(",", falsePositives)}");
    Assert(loadImpact.Results.Any(result => result.TargetSymbolId == use.SymbolId && result.SourceSymbolId == run.SymbolId && result.Depth == 1),
        "Caller expansion must traverse from Load to Use to Run.");
    Assert(loadImpact.Status == ResponseStatus.Partial && !loadImpact.Truncated,
        "Dynamic limitations must remain explicit without implying budget truncation.");
    Assert(loadImpact.Limitations.Contains("reflection-invocations-and-strings-may-be-missed", StringComparer.Ordinal) &&
           loadImpact.Limitations.Contains("dependency-injection-registrations-may-be-missed", StringComparer.Ordinal) &&
           loadImpact.Limitations.Contains("xaml-bindings-are-not-analyzed", StringComparer.Ordinal) &&
           loadImpact.Limitations.Contains("generated-documents-are-not-analyzed", StringComparer.Ordinal),
        "Impact coverage must expose reflection, DI, XAML, and generated-code limitations.");

    var repeat = service.Query(new ImpactQuery(workspace, store, [load.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 100, MaxSourceFiles: 100, PageSize: 100)));
    Assert(loadImpact.Results.Select(ImpactKey).SequenceEqual(repeat.Results.Select(ImpactKey)),
        "Repeated impact queries must be deterministically ordered.");

    var empty = service.Query(new ImpactQuery(workspace, store, [privateLoad.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 100, MaxSourceFiles: 100, PageSize: 100), IncludeLexicalCandidates: false));
    Assert(empty.Status == ResponseStatus.NotFound && empty.Results.Count == 0 && !empty.Truncated && empty.Coverage.Level == CoverageLevel.Partial,
        "Zero static references must remain distinct from complete no-impact and from truncation.");

    var depthBounded = service.Query(new ImpactQuery(workspace, store, [load.SymbolId],
        new ImpactBudget(MaxDepth: 0, MaxResults: 100, MaxSourceFiles: 100, PageSize: 100)));
    Assert(depthBounded.Truncated && depthBounded.ExhaustedBudgets.Contains("depth", StringComparer.Ordinal),
        "Caller depth exhaustion must be explicit.");
    var sourceBounded = service.Query(new ImpactQuery(workspace, store, [load.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 100, MaxSourceFiles: 1, PageSize: 100)));
    Assert(sourceBounded.Truncated && sourceBounded.ExhaustedBudgets.Contains("source", StringComparer.Ordinal),
        "Source-file budget exhaustion must be explicit.");
    var resultBounded = service.Query(new ImpactQuery(workspace, store, [load.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 1, MaxSourceFiles: 100, PageSize: 1)));
    Assert(resultBounded.Truncated && resultBounded.ExhaustedBudgets.Contains("result", StringComparer.Ordinal),
        "Result budget exhaustion must be explicit.");
    var firstPage = service.Query(new ImpactQuery(workspace, store, [load.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 100, MaxSourceFiles: 100, PageSize: 1)));
    Assert(firstPage.Truncated && firstPage.ExhaustedBudgets.Contains("page", StringComparer.Ordinal) && firstPage.NextCursor is not null,
        "Page budget exhaustion must return a generation-bound cursor.");
    var secondPage = service.Query(new ImpactQuery(workspace, store, [load.SymbolId],
        new ImpactBudget(MaxDepth: 1, MaxResults: 100, MaxSourceFiles: 100, PageSize: 1), Cursor: firstPage.NextCursor));
    Assert(secondPage.Results.Count == 1 && ImpactKey(firstPage.Results[0]) != ImpactKey(secondPage.Results[0]),
        "Impact paging must advance deterministically without repeating a result.");

    var workerImpact = service.Query(new ImpactQuery(workspace, store, [worker.SymbolId],
        new ImpactBudget(MaxDepth: 0, MaxResults: 100, MaxSourceFiles: 100, PageSize: 100)));
    Assert(workerImpact.Results.Any(result => result.Kind == ReferenceKind.Static && result.Location.Span.StartLine == 22),
        "typeof(Worker) must remain a confirmed static type reference.");
    Assert(workerImpact.Results.Any(result => result.Kind == ReferenceKind.LexicalCandidate && result.Provenance == "reflection-activation-typeof" && result.Location.Span.StartLine == 22),
        "Reflection activation must remain a separately provenanced candidate.");
    Assert(workerImpact.Results.Any(result => result.Kind == ReferenceKind.LexicalCandidate && result.Provenance == "reflection-string-qualified-type" && result.Location.Span.StartLine == 31),
        "Reflection string references must remain lexical candidates.");
    Assert(!workerImpact.Results.Any(result => result.Kind == ReferenceKind.Static && result.Location.Span.StartLine == 31),
        "Reflection strings must never be promoted to static references.");
    var workerRunImpact = service.Query(new ImpactQuery(workspace, store, [workerRun.SymbolId],
        new ImpactBudget(MaxDepth: 0, MaxResults: 100, MaxSourceFiles: 100, PageSize: 100)));
    Assert(workerRunImpact.Results.Any(result => result.Kind == ReferenceKind.LexicalCandidate && result.Provenance == "lexical-string-member-name" && result.Location.Span.StartLine == 27),
        "Member-name strings must carry lexical provenance.");

    var remarkResolver = new CSharpRemarkResolver();
    var remarks = remarkResolver.Resolve(new RemarkQuery(workspace, store, load.SymbolId));
    Assert(remarks.Freshness.ReturnedFiles == FreshnessState.Verified && remarks.Remarks.Count == 2,
        "Remark resolve must return the two comments only after current digest verification.");
    Assert(remarks.Remarks.Select(remark => remark.Location.Span.StartLine).SequenceEqual([10, 11]),
        "Remark locations must match the independent fixture ground truth.");
    Assert(remarks.Remarks.All(remark => remark.RemarkHash.StartsWith("sha256:", StringComparison.Ordinal)),
        "Every remark must include a content fingerprint.");

    var contractsPath = Path.Combine(workspace, "navigation", "Contracts.cs");
    var current = File.ReadAllText(contractsPath);
    Assert(current.Contains("Loads one value.", StringComparison.Ordinal), "Remark mutation fixture anchor is missing.");
    File.WriteAllText(contractsPath, current.Replace("Loads one value.", "Reads one value.", StringComparison.Ordinal));
    var stale = remarkResolver.Resolve(new RemarkQuery(workspace, store, load.SymbolId, build.Manifest.GenerationId));
    Assert(stale.Status == ResponseStatus.Error && stale.Freshness.ReturnedFiles == FreshnessState.Stale && stale.Remarks.Count == 0,
        "Same-length remark edits must not return old spans or text.");

    var rebuilt = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
    var rebuiltLoad = rebuilt.Symbols.Single(symbol => symbol.SymbolId == load.SymbolId);
    var refreshed = remarkResolver.Resolve(new RemarkQuery(workspace, store, rebuiltLoad.SymbolId, rebuilt.Manifest.GenerationId));
    Assert(refreshed.Remarks.Any(remark => remark.Text.Contains("Reads one value.", StringComparison.Ordinal)) &&
           refreshed.Remarks.All(remark => !remark.Text.Contains("Loads one value.", StringComparison.Ordinal)),
        "Rebuilt remark resolution must return only current verified text.");

    Console.WriteLine($"TASK-013 fixture recall={recall:F3}, false-positive={falsePositives.Length}, limitations={loadImpact.Limitations.Count}");

    static int declarationLine(SymbolContract symbol) => symbol.Declarations.Single().Location.Span.StartLine;
}

static string ImpactKey(ImpactMatch match) => string.Join('|', match.Kind, match.Depth, match.TargetSymbolId,
    match.SourceSymbolId, match.Location.Path, match.Location.Span.Start, match.Provenance);

static void CopyDirectory(string source, string destination)
{
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    }

    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

private static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
}
