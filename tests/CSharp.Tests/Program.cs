using System.Text.Json;
using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Search;
using CodeVirtualize.Core.Storage;
using CodeVirtualize.CSharp;
using CodeVirtualize.CSharp.Tests.References;

var repositoryRoot = Directory.GetCurrentDirectory();
var navigationRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "csharp");
var temporaryRoot = Path.Combine(Path.GetTempPath(), $"code-virtualize-csharp-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryRoot);
try
{
    TrustBoundaryTests(temporaryRoot);
    NavigationAndSearchTests(navigationRoot, temporaryRoot);
    ReferenceFeatureTests.Run(navigationRoot, temporaryRoot);
    InventoryFailureAndPagingTests(temporaryRoot);
    SentinelAndCliTests(repositoryRoot, navigationRoot, temporaryRoot);
    Console.WriteLine("C# build/search/trust/coverage/paging tests passed.");
}
finally
{
    Directory.Delete(temporaryRoot, recursive: true);
}

static void TrustBoundaryTests(string temporaryRoot)
{
    var workspace = Path.Combine(temporaryRoot, "trust-workspace");
    var sentinel = Path.Combine(workspace, "semantic-loader-sentinel.txt");
    Directory.CreateDirectory(workspace);

    var loader = new SentinelSemanticLoader(sentinel);
    var untrustedAdapter = new CSharpAdapter(DenyAllWorkspaceTrustPolicy.Instance, loader);
    var syntaxResult = untrustedAdapter.Analyze(new WorkspaceAnalysisRequest(workspace));
    Assert(!syntaxResult.SemanticLoadAttempted, "Syntax-only analysis must not invoke the semantic loader.");
    Assert(loader.CallCount == 0 && !File.Exists(sentinel), "An untrusted workspace must not run the semantic sentinel.");

    var deniedSemanticResult = untrustedAdapter.Analyze(new WorkspaceAnalysisRequest(workspace, AnalysisMode.TrustedSemantic));
    Assert(!deniedSemanticResult.SemanticLoadAttempted, "Semantic mode without explicit trust must be denied.");
    Assert(loader.CallCount == 0 && !File.Exists(sentinel), "Denied trust must not invoke the semantic loader.");

    var trustedAdapter = new CSharpAdapter(new ExplicitWorkspaceTrustPolicy([workspace]), loader);
    var trustedResult = trustedAdapter.Analyze(new WorkspaceAnalysisRequest(workspace, AnalysisMode.TrustedSemantic));
    Assert(trustedResult.SemanticLoadAttempted, "Exact explicit trust must enter the semantic boundary.");
    Assert(loader.CallCount == 1 && File.Exists(sentinel), "Only exact explicit trust may cross the semantic boundary.");
}

static void NavigationAndSearchTests(string navigationRoot, string temporaryRoot)
{
    var store = Path.Combine(temporaryRoot, "navigation-store");
    var builder = new CSharpIndexBuilder();
    var first = builder.Build(new CSharpBuildRequest(navigationRoot, store));
    Assert(first.Manifest.State == GenerationState.Partial, "Syntax-only generation must be partial.");
    Assert(first.Manifest.Coverage.Scope.Contains("syntax-only", StringComparison.Ordinal), "Syntax-only scope must be explicit.");
    Assert(first.Manifest.Coverage.Limitations.Contains("syntax-only-analysis-does-not-evaluate-msbuild-restore-build-analyzers-or-generators", StringComparer.Ordinal), "Syntax limitations must be explicit.");
    Assert(!first.SemanticLoadAttempted, "Default build must not attempt semantic loading.");
    Assert(first.Manifest.Files.Any(file => file.Path.EndsWith("shared/LinkedHelper.cs", StringComparison.Ordinal)), "Physical linked source must be inventoried.");
    Assert(first.Manifest.Files.Single(file => file.Path.EndsWith("shared/LinkedHelper.cs", StringComparison.Ordinal)).ProjectIds.Count == 2, "Linked source inventory must retain both project memberships.");
    using (var reader = new GenerationStore(store).OpenCurrent())
    {
        var symbolShard = reader.Manifest.Shards.Single(shard => shard.Kind == "symbol");
        var storedSymbols = string.Join("\n", reader.ReadShardRecords(symbolShard.Path));
        Assert(!storedSymbols.Contains("return \"linked\"", StringComparison.Ordinal), "Symbol shards must not store source bodies.");
    }

    var second = builder.Build(new CSharpBuildRequest(navigationRoot, store));
    Assert(!second.Published && second.Manifest.GenerationId == first.Manifest.GenerationId, "Unchanged rebuild must reuse the deterministic generation.");
    Assert(first.Symbols.Select(symbol => symbol.SymbolId).SequenceEqual(second.Symbols.Select(symbol => symbol.SymbolId)), "Unchanged rebuild must preserve symbol ordering and IDs.");

    var catalog = first.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Catalog");
    Assert(catalog.Declarations.Count == 3, "Partial Catalog declarations must merge into one symbol.");
    Assert(catalog.Declarations.Select(item => item.Location.Span.StartLine).Order().SequenceEqual([3, 3, 8]), "Partial declarations must retain exact source lines.");

    var loads = first.Symbols.Where(symbol => symbol.Name == "Load").ToArray();
    Assert(loads.Length == 5, "Interface, overload, generic, and explicit Load declarations must remain distinct.");
    Assert(loads.Any(symbol => symbol.GenericArity == 1 && symbol.Accessibility == "private" && symbol.Parameters.Count == 1), "Private generic overload must retain arity and accessibility.");
    Assert(loads.Any(symbol => symbol.Parameters.Count == 2), "Two-parameter overload must retain its identity.");
    Assert(loads.Any(symbol => symbol.ExplicitInterface is not null), "Explicit interface implementation must have a distinct identity.");
    Assert(loads.Select(symbol => symbol.SymbolId).Distinct(StringComparer.Ordinal).Count() == 5, "Overload/ref-kind/generic/explicit interface inputs must produce distinct IDs.");

    var mutate = first.Symbols.Where(symbol => symbol.Name == "Mutate").OrderBy(symbol => symbol.Signature, StringComparer.Ordinal).ToArray();
    Assert(mutate.Length == 2 && mutate.Select(symbol => symbol.SymbolId).Distinct(StringComparer.Ordinal).Count() == 2, "Ref-kind overloads must have distinct IDs.");
    Assert(mutate.Any(symbol => symbol.Parameters.Single().RefKind == ParameterRefKind.Ref && symbol.Accessibility == "protected"), "Ref-kind and protected accessibility must be preserved.");
    Assert(mutate.Any(symbol => symbol.Parameters.Single().RefKind == ParameterRefKind.None && symbol.Accessibility == "internal"), "Non-ref overload and internal accessibility must be preserved.");

    var linkedTypes = first.Symbols.Where(symbol => symbol.Kind == "class" && symbol.Name == "LinkedHelper").ToArray();
    Assert(linkedTypes.Length == 2 && linkedTypes.Select(symbol => symbol.ProjectId).Distinct().Count() == 2, "A linked file must preserve project-specific symbol identity.");
    Assert(linkedTypes.All(symbol => symbol.Declarations.Single().Location.Span.StartLine == 3), "Linked declarations must retain their physical source location.");

    var service = new SymbolSearchService();
    var privateGeneric = service.Search(store, new SymbolSearchQuery(Name: "Load", Kind: "method", Accessibility: "private", Limit: 20));
    Assert(privateGeneric.Results.Count == 2, "Accessibility/name/kind filters must find private generic and explicit methods.");
    var qualified = service.Search(store, new SymbolSearchQuery(QualifiedName: "Fixture.Navigation.Catalog.Name", Limit: 20));
    Assert(qualified.Results.Count == 1, "Qualified-name filter must return the exact property.");
    var exact = service.Search(store, new SymbolSearchQuery(Exact: "LinkedHelper", Limit: 20));
    Assert(exact.Results.Count == 2, "Exact simple-name filter must retain same-name project candidates.");

    var deniedStore = Path.Combine(temporaryRoot, "denied-semantic-store");
    var denied = new CSharpIndexBuilder(DenyAllWorkspaceTrustPolicy.Instance)
        .Build(new CSharpBuildRequest(navigationRoot, deniedStore, AnalysisMode.TrustedSemantic));
    Assert(!denied.SemanticLoadAttempted && denied.Manifest.Coverage.Limitations.Contains("semantic-load-requires-explicit-exact-workspace-trust", StringComparer.Ordinal), "Denied semantic build must fall back visibly to syntax-only.");

    var semanticStore = Path.Combine(temporaryRoot, "trusted-semantic-store");
    var semantic = new CSharpIndexBuilder(new ExplicitWorkspaceTrustPolicy([navigationRoot]))
        .Build(new CSharpBuildRequest(navigationRoot, semanticStore, AnalysisMode.TrustedSemantic));
    Assert(semantic.SemanticLoadAttempted, "Exact trusted workspace must enter semantic compilation.");
    Assert(semantic.Manifest.Coverage.Limitations.Contains("trusted-semantic-in-process-compilation-does-not-evaluate-msbuild-or-project-references", StringComparer.Ordinal), "Semantic limitations must state the unevaluated project scope.");
}

static void InventoryFailureAndPagingTests(string temporaryRoot)
{
    var empty = Path.Combine(temporaryRoot, "empty");
    Directory.CreateDirectory(empty);
    var emptyStore = Path.Combine(temporaryRoot, "empty-store");
    var emptyResult = new CSharpIndexBuilder().Build(new CSharpBuildRequest(empty, emptyStore));
    Assert(emptyResult.Manifest.Files.Count == 0 && emptyResult.Manifest.Coverage.Limitations.Contains("workspace-contains-no-csharp-source-files", StringComparer.Ordinal), "Empty workspace must be distinct from a complete empty result.");
    var emptyFind = new SymbolSearchService().Search(emptyStore, new SymbolSearchQuery(Query: "Anything"));
    Assert(emptyFind.Status == ResponseStatus.NotFound && emptyFind.Coverage.Level == CoverageLevel.Partial, "Empty workspace find must preserve partial coverage.");

    var malformed = Path.Combine(temporaryRoot, "malformed");
    Directory.CreateDirectory(malformed);
    File.WriteAllText(Path.Combine(malformed, "Broken.csproj"), "<Project><PropertyGroup>");
    File.WriteAllText(Path.Combine(malformed, "HiddenByFailure.cs"), "public class HiddenByFailure { }");
    var malformedStore = Path.Combine(temporaryRoot, "malformed-store");
    var malformedResult = new CSharpIndexBuilder().Build(new CSharpBuildRequest(malformed, malformedStore));
    Assert(malformedResult.Manifest.Coverage.FailedProjects.Count == 1 && malformedResult.Manifest.Coverage.FailedFiles > 0, "Malformed project must report failed project and files.");
    Assert(malformedResult.Symbols.Count == 0, "Failed project declarations must not be presented as analyzed.");

    var workspace = Path.Combine(temporaryRoot, "paging");
    Directory.CreateDirectory(workspace);
    File.WriteAllText(Path.Combine(workspace, "Paging.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
    File.WriteAllText(Path.Combine(workspace, "Symbols.cs"), "public class Alpha { public void One() { } public void Two() { } } public class Beta { }");
    Directory.CreateDirectory(Path.Combine(workspace, "obj"));
    File.WriteAllText(Path.Combine(workspace, "obj", "ExcludedGenerated.cs"), "public class MustStayExcluded { }");
    var store = Path.Combine(temporaryRoot, "paging-store");
    var builder = new CSharpIndexBuilder();
    var initial = builder.Build(new CSharpBuildRequest(workspace, store));
    Assert(initial.Manifest.Coverage.ExcludedFiles == 1 && initial.Symbols.All(symbol => symbol.Name != "MustStayExcluded"), "Excluded inventory must be counted and remain outside analyzed symbols.");
    var service = new SymbolSearchService();
    var firstPage = service.Search(store, new SymbolSearchQuery(Limit: 2));
    var repeatPage = service.Search(store, new SymbolSearchQuery(Limit: 2));
    Assert(firstPage.Truncated && firstPage.NextCursor is not null, "Limited results must return a continuation cursor.");
    Assert(RawIds(firstPage).SequenceEqual(RawIds(repeatPage)), "Repeated first page must be deterministic.");

    var allIds = new List<string>(RawIds(firstPage));
    var cursor = firstPage.NextCursor;
    while (cursor is not null)
    {
        var page = service.Search(store, new SymbolSearchQuery(Limit: 2, Cursor: cursor));
        allIds.AddRange(RawIds(page));
        cursor = page.NextCursor;
    }
    Assert(allIds.Count == initial.Symbols.Count && allIds.Distinct(StringComparer.Ordinal).Count() == allIds.Count, "Paging must visit every symbol exactly once.");

    var changedQuery = service.Search(store, new SymbolSearchQuery(Query: "Alpha", Limit: 2, Cursor: firstPage.NextCursor));
    Assert(changedQuery.Status == ResponseStatus.Error && changedQuery.Errors.Single().Code == "CURSOR_INVALID", "Cursor query mismatch must be rejected.");

    File.WriteAllText(Path.Combine(workspace, "NewFile.cs"), "public class NewlyInventoried { }");
    var rebuilt = builder.Build(new CSharpBuildRequest(workspace, store));
    Assert(rebuilt.Manifest.GenerationId != initial.Manifest.GenerationId && rebuilt.Manifest.Files.Any(file => file.Path == "NewFile.cs"), "New source files must enter inventory and create a new generation.");
    Assert(service.Search(store, new SymbolSearchQuery(Exact: "NewlyInventoried")).Results.Count == 1, "Newly inventoried declaration must be searchable after rebuild.");
    var expired = service.Search(store, new SymbolSearchQuery(Limit: 2, Cursor: firstPage.NextCursor));
    Assert(expired.Status == ResponseStatus.Error && expired.Errors.Single().Code == "CURSOR_EXPIRED", "Cursor from an older generation must expire.");

    File.Delete(Path.Combine(workspace, "NewFile.cs"));
    var reverted = builder.Build(new CSharpBuildRequest(workspace, store));
    Assert(reverted.Manifest.InputFingerprint == initial.Manifest.InputFingerprint, "Reverted input must recover the deterministic input fingerprint.");
    Assert(reverted.Manifest.GenerationId != initial.Manifest.GenerationId, "Immutable existing generation IDs must not be overwritten when input reverts.");
    Assert(reverted.Symbols.Select(symbol => symbol.SymbolId).SequenceEqual(initial.Symbols.Select(symbol => symbol.SymbolId)), "Reverted input must recover deterministic symbol IDs and ordering.");
}

static void SentinelAndCliTests(string repositoryRoot, string navigationRoot, string temporaryRoot)
{
    var untrusted = Path.Combine(repositoryRoot, "tests", "Integration.Tests", "Fixtures", "UntrustedWorkspace");
    var sentinel = Path.Combine(untrusted, "generator-or-build-sentinel.txt");
    File.Delete(sentinel);
    var sentinelStore = Path.Combine(temporaryRoot, "sentinel-store");
    var result = new CSharpIndexBuilder().Build(new CSharpBuildRequest(untrusted, sentinelStore));
    Assert(!result.SemanticLoadAttempted && !File.Exists(sentinel), "Syntax-only inventory/parser must not execute Directory.Build.targets.");

    var cliStore = Path.Combine(temporaryRoot, "cli-store");
    var buildText = new StringWriter();
    var buildExit = CliApplication.Run(["cv-build", "--workspace", navigationRoot, "--store", cliStore, "--format", "text"], buildText, TextWriter.Null);
    Assert(buildExit == 3 && buildText.ToString().Contains("Symbols", StringComparison.Ordinal), "Actual cv-build text command must report partial syntax coverage and symbol count.");
    var buildJson = new StringWriter();
    var jsonExit = CliApplication.Run(["cv-build", "--workspace", navigationRoot, "--store", cliStore, "--format", "json"], buildJson, TextWriter.Null);
    var manifest = ContractJson.Deserialize<ManifestContract>(buildJson.ToString());
    Assert(jsonExit == 3 && manifest.GenerationId.Length > 0, "Actual cv-build JSON command must emit a valid manifest.");

    var findText = new StringWriter();
    var findExit = CliApplication.Run(["cv-find", "Load", "--store", cliStore, "--format", "text"], findText, TextWriter.Null);
    Assert(findExit == 3 && findText.ToString().Contains("Catalog.Load", StringComparison.Ordinal), "Actual cv-find text command must render matching declarations.");
    var findJson = new StringWriter();
    var findJsonExit = CliApplication.Run(["cv-find", "--exact", "Catalog", "--store", cliStore, "--format", "json"], findJson, TextWriter.Null);
    var response = ContractJson.Deserialize<ResponseContract>(findJson.ToString());
    Assert(findJsonExit == 3 && response.Results.Count == 1, "Actual cv-find JSON command must emit a valid response.");
}

static IReadOnlyList<string> RawIds(ResponseContract response) => response.Results
    .Select(result => ContractJson.Deserialize<SymbolContract>(result.GetRawText()).SymbolId)
    .ToArray();

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class SentinelSemanticLoader(string sentinel) : ITrustedSemanticLoader
{
    public int CallCount { get; private set; }

    public AnalysisResult Load(WorkspaceAnalysisRequest request)
    {
        CallCount++;
        File.WriteAllText(sentinel, "semantic loader invoked");
        return new AnalysisResult(request.Mode, "semantic", true, "partial", ["test-semantic-loader"]);
    }
}






