using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.CSharp.Tests;

/// <summary>
/// Verifies TASK-026 follow-up: the extractor populates <see cref="SymbolContract.ContainerId"/> for
/// members and nested types (so the Diff module's container-cascade suppression, U2, actually activates
/// on real <c>cv-build</c> output), and that upgrading past this change is safe for existing generations.
/// </summary>
internal static class ContainerIdentityTests
{
    public static void Run(string temporaryRoot)
    {
        NestedPartialAndTopLevelContainerIdsAreConsistent(temporaryRoot);
        FullAndIncrementalBuildsAgreeOnContainerId(temporaryRoot);
        AdapterVersionBumpChangesAnalysisKey();
        StaleGenerationWithMissingContainerIdForcesFullRebuild(temporaryRoot);
    }

    private static void NestedPartialAndTopLevelContainerIdsAreConsistent(string temporaryRoot)
    {
        var workspace = Path.Combine(temporaryRoot, "container-workspace");
        Directory.CreateDirectory(workspace);
        WriteProject(workspace);
        File.WriteAllText(Path.Combine(workspace, "Outer.First.cs"), """
            namespace Fixture.Container;

            public partial class Outer
            {
                public int A() => 1;

                public class Nested
                {
                    public int B() => 1;
                }
            }
            """);
        File.WriteAllText(Path.Combine(workspace, "Outer.Second.cs"), """
            namespace Fixture.Container;

            public partial class Outer
            {
                public int C() => 1;
            }
            """);

        var store = Path.Combine(temporaryRoot, "container-store");
        var build = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
        var outer = build.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Outer");
        var nested = build.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Nested");
        var a = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "A");
        var b = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "B");
        var c = build.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "C");

        Assert(outer.Declarations.Count == 2, "The partial Outer class must merge two declarations into one symbol.");
        Assert(outer.ContainerId is null,
            "A top-level type's container is a namespace; namespaces are not modeled as symbols here, so containerId must be null.");
        Assert(nested.ContainerId == outer.SymbolId, "A nested type's containerId must be its containing type's symbol ID.");
        Assert(a.ContainerId == outer.SymbolId, "A member declared in the first partial file must reference the merged Outer symbol ID.");
        Assert(c.ContainerId == outer.SymbolId,
            "A member declared in the second partial file must resolve to the exact same containerId as a member declared in the first file.");
        Assert(b.ContainerId == nested.SymbolId, "A member of a nested type must reference the nested type, not the outer type.");
        Assert(a.ContainerId != b.ContainerId, "Members of different nesting levels must not collapse onto the same containerId.");
    }

    private static void FullAndIncrementalBuildsAgreeOnContainerId(string temporaryRoot)
    {
        var workspace = Path.Combine(temporaryRoot, "container-incremental-workspace");
        Directory.CreateDirectory(workspace);
        WriteProject(workspace);
        var changedPath = Path.Combine(workspace, "Changed.cs");
        File.WriteAllText(Path.Combine(workspace, "Unchanged.cs"), "namespace Fixture.Container; public class Unchanged { public int Kept() => 1; }");
        File.WriteAllText(changedPath, "namespace Fixture.Container; public class Changed { public int Edited() => 1; }");

        var store = Path.Combine(temporaryRoot, "container-incremental-store");
        var initial = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
        var initialKept = initial.Symbols.Single(symbol => symbol.Name == "Kept");
        var initialUnchangedType = initial.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Unchanged");
        var initialChangedType = initial.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Changed");
        Assert(initialKept.ContainerId == initialUnchangedType.SymbolId, "Sanity: a full build must populate containerId.");

        File.WriteAllText(changedPath, "namespace Fixture.Container; public class Changed { public int Edited() => 2; }");
        var service = new CSharpLifecycleService();
        var update = service.Update(new CSharpUpdateRequest(workspace, store, ChangedPathHints: ["Changed.cs"]));
        Assert(!update.UsedFullRebuild, "A single-file content edit with an accurate hint must stay on the incremental path (this test wants to exercise reuse, not a forced rebuild).");

        using var reader = new GenerationStore(store).Open(update.Manifest.GenerationId);
        var shard = reader.Manifest.Shards.Single(item => item.Kind == "symbol");
        var symbols = reader.ReadShardRecords(shard.Path).Select(ContractJson.Deserialize<SymbolContract>).ToArray();
        var reusedKept = symbols.Single(symbol => symbol.Name == "Kept");
        var reExtractedEdited = symbols.Single(symbol => symbol.Name == "Edited");
        var reusedUnchangedType = symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Unchanged");
        var reExtractedChangedType = symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Changed");

        Assert(reusedKept.SymbolId == initialKept.SymbolId, "A reused symbol's identity must be stable across the incremental update.");
        Assert(reusedKept.ContainerId == reusedUnchangedType.SymbolId,
            "A symbol reused unchanged from a prior generation must retain the containerId the full build already populated.");
        Assert(reExtractedChangedType.SymbolId == initialChangedType.SymbolId, "Editing a method body must not change the containing type's identity.");
        Assert(reExtractedEdited.ContainerId == reExtractedChangedType.SymbolId,
            "A symbol freshly re-extracted from a changed file must get the same containerId the full build would have produced for it.");
    }

    private static void AdapterVersionBumpChangesAnalysisKey()
    {
        var oldKey = SemanticConfigFingerprint.CreateAnalysisKey(new AnalysisConfigFingerprintInput(
            ContractVersions.Current, "csharp-1", "compiler-1", ["proj"], ["refs"], ["net9.0"], [], AnalysisMode.SyntaxOnly));
        var newKey = SemanticConfigFingerprint.CreateAnalysisKey(new AnalysisConfigFingerprintInput(
            ContractVersions.Current, CSharpIndexBuilder.AdapterVersion, "compiler-1", ["proj"], ["refs"], ["net9.0"], [], AnalysisMode.SyntaxOnly));
        Assert(CSharpIndexBuilder.AdapterVersion != "csharp-1",
            "This test assumes AdapterVersion was bumped past the pre-containerId value; update the literal above if it is bumped again.");
        Assert(oldKey != newKey,
            "Bumping AdapterVersion must change analysisKey for otherwise-identical inputs, so an on-disk generation from the old adapter is never mistaken for current.");
    }

    private static void StaleGenerationWithMissingContainerIdForcesFullRebuild(string temporaryRoot)
    {
        var workspace = Path.Combine(temporaryRoot, "container-compat-workspace");
        Directory.CreateDirectory(workspace);
        WriteProject(workspace);
        File.WriteAllText(Path.Combine(workspace, "Target.cs"), "namespace Fixture.Container; public class Target { public int Value() => 1; }");

        var store = Path.Combine(temporaryRoot, "container-compat-store");
        var fresh = new CSharpIndexBuilder().Build(new CSharpBuildRequest(workspace, store));
        var type = fresh.Symbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Target");
        var member = fresh.Symbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Value");
        Assert(member.ContainerId == type.SymbolId, "Sanity: the freshly built member must reference its container.");

        // Simulate a generation left over from before this fix: an older adapterVersion (which changes
        // analysisKey) and containerId stripped from every symbol, the way "csharp-1" always produced them.
        var staleAnalysisKey = SemanticConfigFingerprint.CreateAnalysisKey(new AnalysisConfigFingerprintInput(
            ContractVersions.Current, "csharp-1", "stale-compiler", ["stale-project-graph"], [], ["net9.0"], [], AnalysisMode.SyntaxOnly));
        var staleManifest = fresh.Manifest with
        {
            GenerationId = $"gen_stale_{Guid.NewGuid():N}",
            AnalysisKey = staleAnalysisKey,
            AdapterVersion = "csharp-1",
            Shards = []
        };
        var staleSymbols = fresh.Symbols.Select(symbol => symbol with { ContainerId = null, AnalysisKey = staleAnalysisKey }).ToArray();
        new GenerationStore(store).Publish(new GenerationPublishRequest(
            staleManifest,
            [new GenerationShardWriteRequest("symbol", "symbols.jsonl", ContractSchemas.Symbol, staleSymbols.Select(ContractJson.Serialize).ToArray())]));

        var service = new CSharpLifecycleService();
        var update = service.Update(new CSharpUpdateRequest(workspace, store));
        Assert(update.UsedFullRebuild,
            "An on-disk generation carrying an older adapterVersion (and therefore a mismatched analysisKey) must force a full rebuild, never an incremental reuse of its containerId-less symbols.");

        using var reader = new GenerationStore(store).Open(update.Manifest.GenerationId);
        var shard = reader.Manifest.Shards.Single(item => item.Kind == "symbol");
        var rebuiltSymbols = reader.ReadShardRecords(shard.Path).Select(ContractJson.Deserialize<SymbolContract>).ToArray();
        var rebuiltType = rebuiltSymbols.Single(symbol => symbol.Kind == "class" && symbol.Name == "Target");
        var rebuiltMember = rebuiltSymbols.Single(symbol => symbol.Kind == "method" && symbol.Name == "Value");
        Assert(rebuiltMember.ContainerId == rebuiltType.SymbolId,
            "After the forced full rebuild, every symbol must carry a freshly populated containerId, not the stale null value carried over from the pre-fix generation.");
    }

    private static void WriteProject(string workspace) =>
        File.WriteAllText(Path.Combine(workspace, "Container.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
