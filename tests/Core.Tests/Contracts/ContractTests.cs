using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Contracts;

namespace CodeVirtualize.Core.Tests.Contracts;

internal static class ContractTests
{
    private static readonly string HashA = $"sha256:{new string('a', 64)}";
    private static readonly string HashB = $"sha256:{new string('b', 64)}";
    private static readonly string HashC = $"sha256:{new string('c', 64)}";

    public static void Run()
    {
        SchemasAreValidAndConsistent();
        ContractsRoundTrip();
        UnknownSchemasAreRejected();
        SymbolIdsAreDeterministic();
        ResponseStatesAreValidated();
        CursorGenerationIsValidated();
        BaselinesRoundTrip();
        SpanAndBudgetInvariantsAreValidated();
        DiffEvidenceRulesAreValidated();
    }

    private static void SchemasAreValidAndConsistent()
    {
        var schemaDirectory = Path.Combine(FindRepositoryRoot(), "schemas");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest.schema.json"] = ContractSchemas.Manifest,
            ["symbol.schema.json"] = ContractSchemas.Symbol,
            ["declaration.schema.json"] = ContractSchemas.Declaration,
            ["reference.schema.json"] = ContractSchemas.Reference,
            ["diff.schema.json"] = ContractSchemas.Diff,
            ["response.schema.json"] = ContractSchemas.Response
        };
        var paths = Directory.GetFiles(schemaDirectory, "*.schema.json");
        Assert(paths.Length == 7, "Six top-level schemas and one common schema must exist.");
        var names = paths.Select(path => Path.GetFileName(path)!).ToHashSet(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert(root.GetProperty("$schema").GetString() == "https://json-schema.org/draft/2020-12/schema", $"{path} must use draft 2020-12.");
            Assert(root.GetProperty("$id").GetString()?.EndsWith(Path.GetFileName(path), StringComparison.Ordinal) == true, $"{path} must have a matching $id.");
            ValidateReferenceFiles(root, Path.GetFileName(path), names);

            if (expected.TryGetValue(Path.GetFileName(path), out var schema))
            {
                var properties = root.GetProperty("properties");
                Assert(properties.GetProperty("schema").GetProperty("const").GetString() == schema, $"{path} schema name must match C#.");
                Assert(properties.GetProperty("schemaVersion").GetProperty("const").GetInt32() == ContractVersions.Current, $"{path} version must match C#.");
            }
        }
    }

    private static void ContractsRoundTrip()
    {
        var identity = Identity(1, "System.String", ParameterRefKind.None);
        var symbolId = DeterministicSymbolId.Create(identity);
        var declaration = Declaration(symbolId, "Catalog.Partial.cs", 10);
        var coverage = PartialCoverage(false);
        var manifest = new ManifestContract(
            "gen-001", "workspace-001", "analysis-001", DateTimeOffset.Parse("2026-09-20T00:00:00Z"), "1.0.0", HashA,
            GenerationState.Partial,
            [new ManifestFileContract("file-001", "Catalog.Partial.cs", HashB, 128, "utf-8", "lf", ["project-001"])],
            [new ManifestProjectContract("project-001", "net9.0", "Debug", ["DEBUG"], HashC, ProjectLoadStatus.Analyzed, ContractAnalysisLevel.SyntaxOnly, ["semantic-binding-not-requested"])],
            [new ManifestShardContract("symbol", "symbol.cv", HashA, 512, 1)], coverage);
        var symbol = new SymbolContract(
            symbolId, "project-001", "analysis-001", "method", "Load", "Example.Catalog.Load", "void Load(string value)", "private", null,
            1, IdentityQuality.Semantic, identity.Parameters, null,
            [declaration, Declaration(symbolId, "Catalog.Partial.Other.cs", 30)], []);
        var reference = new ReferenceContract(
            symbolId, null, declaration.Location, ReferenceKind.LexicalCandidate, "identifier-search", "analysis-001", coverage,
            ["lexical-candidate-not-confirmed"]);

        RoundTrip(manifest);
        RoundTrip(symbol);
        RoundTrip(declaration);
        RoundTrip(reference);
        RoundTrip(Diff(VcsBaseline()));
        RoundTrip(Response(ResponseStatus.Partial, coverage, false, []));
    }

    private static void UnknownSchemasAreRejected()
    {
        var json = ContractJson.Serialize(Response(ResponseStatus.Ok, CompleteCoverage(), false, []));
        Throws(() => ContractJson.Deserialize<ResponseContract>(json.Replace(ContractSchemas.Response, "code-virtualize/future", StringComparison.Ordinal)), "SCHEMA_UNSUPPORTED");
        Throws(() => ContractJson.Deserialize<ResponseContract>(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)), "SCHEMA_UNSUPPORTED");
        Throws(() => ContractJson.Deserialize<ResponseContract>(json.Replace("\"requestId\":", "\"unknown\":true,\"requestId\":", StringComparison.Ordinal)), "CONTRACT_INVALID");
        Throws(() => ContractJson.Deserialize<ResponseContract>(json.Replace("\"requestId\":\"req-001\"", "\"requestId\":\"first\",\"requestId\":\"req-001\"", StringComparison.Ordinal)), "CONTRACT_INVALID");
    }

    private static void SymbolIdsAreDeterministic()
    {
        var identity = Identity(1, "System.String", ParameterRefKind.None);
        var symbolId = DeterministicSymbolId.Create(identity);
        Assert(symbolId == DeterministicSymbolId.Create(identity with { }), "Partial declaration location must not affect ID.");
        Assert(symbolId != DeterministicSymbolId.Create(identity with { GenericArity = 2 }), "Generic arity must affect ID.");
        Assert(symbolId != DeterministicSymbolId.Create(Identity(1, "System.Int32", ParameterRefKind.None)), "Overload parameter type must affect ID.");
        Assert(symbolId != DeterministicSymbolId.Create(Identity(1, "System.String", ParameterRefKind.Ref)), "ref-kind must affect ID.");
        Assert(symbolId != DeterministicSymbolId.Create(identity with { ExplicitInterface = "Example.ILoader" }), "Explicit interface must affect ID.");
    }

    private static void ResponseStatesAreValidated()
    {
        RoundTrip(Response(ResponseStatus.Ok, CompleteCoverage(), false, []));
        RoundTrip(Response(ResponseStatus.NotFound, CompleteCoverage(), false, []));
        RoundTrip(Response(ResponseStatus.Partial, PartialCoverage(false), false, []));
        RoundTrip(Response(ResponseStatus.Partial, PartialCoverage(true), true, [], "cursor-page-2"));
        RoundTrip(Response(ResponseStatus.Error, PartialCoverage(false), false,
            [new ErrorContract("SOURCE_UNSTABLE", "Source changed while reading.", true, new Dictionary<string, string>())]));
        Throws(() => Response(ResponseStatus.Error, PartialCoverage(false), false, []).Validate(), "CONTRACT_INVALID");
        Throws(() => Response(ResponseStatus.Ok, CompleteCoverage(), true, [], "cursor").Validate(), "CONTRACT_INVALID");
    }

    private static void CursorGenerationIsValidated()
    {
        var encoded = PageCursorCodec.Encode(new PageCursorPayload("gen-001", HashA, 25));
        Assert(PageCursorCodec.Decode(encoded, "gen-001", HashA).Offset == 25, "Cursor offset must round-trip.");
        Throws(() => PageCursorCodec.Decode(encoded, "gen-002", HashA), "CURSOR_EXPIRED");
        Throws(() => PageCursorCodec.Decode(encoded, "gen-001", HashB), "CURSOR_INVALID");
    }

    private static void BaselinesRoundTrip()
    {
        var vcs = RoundTrip(Diff(VcsBaseline())).Baseline;
        Assert(vcs.Kind == BaselineKind.Vcs && vcs.SessionId is null, "VCS baseline semantics must survive JSON.");
        var session = new BaselineContract(BaselineKind.Session, "session", "snapshot-start-dirty", "snapshot-current-stable", "session-001", DateTimeOffset.Parse("2026-09-20T01:00:00Z"), HashC);
        var restored = RoundTrip(Diff(session)).Baseline;
        Assert(restored.Kind == BaselineKind.Session && restored.SessionId == "session-001", "Session baseline semantics must survive JSON.");
    }

    private static void SpanAndBudgetInvariantsAreValidated()
    {
        const string content = "A😀\r\nB";
        var contentHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";
        var source = new SourceSliceContract(content, new TextSpanContract(12, content.Length, 3, 4), new SourceBudgetContract(8, 2), new SourceUsageContract(8, 2), false, BudgetExhaustion.None, contentHash, false);
        source.Validate();
        Assert(source.Span.Length == 6, "Emoji must occupy two UTF-16 code units.");
        Throws(() => new TextSpanContract(0, 1, 0, 1).Validate(), "CONTRACT_INVALID");
        Throws(() => (source with { Budget = new SourceBudgetContract(7, 2) }).Validate(), "CONTRACT_INVALID");
        Throws(() => (source with { Truncated = true, ExhaustedBy = BudgetExhaustion.None }).Validate(), "CONTRACT_INVALID");

        var notModified = new SourceSliceContract(string.Empty, source.Span, source.Budget, new SourceUsageContract(0, 0), false, BudgetExhaustion.None, HashA, true);
        notModified.Validate();
        Throws(() => (notModified with { Content = "x" }).Validate(), "CONTRACT_INVALID");
        Throws(() => (notModified with { Usage = new SourceUsageContract(1, 0) }).Validate(), "CONTRACT_INVALID");
        Throws(() => (source with { ContentHash = "not-a-hash" }).Validate(), "CONTRACT_INVALID");

        // L6: a syntactically valid sha256 that simply does not match the returned content's bytes must be
        // rejected too, not just malformed hashes — content and contentHash must never silently diverge.
        Throws(() => (source with { ContentHash = HashA }).Validate(), "CONTRACT_INVALID");
    }

    private static void DiffEvidenceRulesAreValidated()
    {
        var lineHunk = new DiffEvidenceContract("body-changed", DiffEvidenceTextMode.LineHunks, "@@ -1,1 +1,1 @@\n-old\n+new\n", 1, 2, 2, false, HashA, HashB);
        lineHunk.Validate();
        Throws(() => (lineHunk with { TextualHunk = null }).Validate(), "CONTRACT_INVALID");

        var headerOnly = new DiffEvidenceContract("symbol-added", DiffEvidenceTextMode.HeaderOnly, "+++ b/x\n@@ -0,0 +1,1 @@\n+new\n", 0, 0, 3, false, null, HashB);
        headerOnly.Validate();
        Throws(() => (headerOnly with { TextualHunk = null }).Validate(), "CONTRACT_INVALID");
        Throws(() => (headerOnly with { ContextLines = 1 }).Validate(), "CONTRACT_INVALID");

        var fingerprintOnly = new DiffEvidenceContract("member-order-changed", DiffEvidenceTextMode.FingerprintOnly, null, 0, 0, 0, false, HashA, HashB);
        fingerprintOnly.Validate();
        Throws(() => (fingerprintOnly with { TextualHunk = "not allowed" }).Validate(), "CONTRACT_INVALID");
        Throws(() => (fingerprintOnly with { ContextLines = 1 }).Validate(), "CONTRACT_INVALID");
        Throws(() => (fingerprintOnly with { BaseContentHash = null, TargetContentHash = null }).Validate(), "CONTRACT_INVALID");

        var symbolId = DeterministicSymbolId.Create(Identity(1, "System.String", ParameterRefKind.None));
        var location = Declaration(symbolId, "Catalog.Partial.cs", 10).Location;
        var truncatedEntry = new DiffEntryContract(DiffKind.BodyChanged, symbolId, symbolId, [location], [location],
            [lineHunk with { Truncated = true }], 1m);
        var truncatedContract = new DiffContract(VcsBaseline(), [truncatedEntry], PartialCoverage(false),
            ["behavioral-equivalence-not-inferred", DiffContract.EvidenceBudgetExhaustedLimitation], false, null, true);
        truncatedContract.Validate();
        Throws(() => (truncatedContract with { EvidenceTruncated = false }).Validate(), "CONTRACT_INVALID");
        Throws(() => (truncatedContract with { Limitations = ["behavioral-equivalence-not-inferred"] }).Validate(), "CONTRACT_INVALID");

        RoundTrip(truncatedContract);
        var json = ContractJson.Serialize(truncatedContract);
        Throws(() => ContractJson.Deserialize<DiffContract>(json.Replace("\"line_hunks\"", "\"unknown_text_mode\"", StringComparison.Ordinal)), "CONTRACT_INVALID");
    }

    private static T RoundTrip<T>(T contract) where T : IVersionedContract
    {
        var json = ContractJson.Serialize(contract);
        var restored = ContractJson.Deserialize<T>(json);
        Assert(json == ContractJson.Serialize(restored), $"{typeof(T).Name} JSON must round-trip deterministically.");
        return restored;
    }

    private static SymbolIdentityContract Identity(int arity, string type, ParameterRefKind refKind) =>
        new("project-identity", "analysis-001", "method", "Example.Catalog.Load`1", arity, [new ParameterIdentityContract(type, refKind)], null);

    private static DeclarationContract Declaration(string symbolId, string path, int start) =>
        new(symbolId, new LocationContract("file-001", HashB, new TextSpanContract(start, 10, 2, 2), path, null), DocumentKind.Source);

    private static CoverageContract CompleteCoverage() =>
        new("static-csharp-selected-configuration", CoverageLevel.CompleteWithinScope, 2, 1, 0, 0, [], ["dynamic-references-not-covered"], false);

    private static CoverageContract PartialCoverage(bool truncated) =>
        new("static-csharp-selected-configuration", CoverageLevel.Partial, 2, 1, 1, 1, ["Failed.Project"], ["project-load-failed"], truncated);

    private static BaselineContract VcsBaseline() =>
        new(BaselineKind.Vcs, "git", "commit-base", "working-tree-snapshot", null, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), HashA);

    private static DiffContract Diff(BaselineContract baseline)
    {
        var symbolId = DeterministicSymbolId.Create(Identity(1, "System.String", ParameterRefKind.None));
        var location = Declaration(symbolId, "Catalog.Partial.cs", 10).Location;
        return new DiffContract(baseline,
            [new DiffEntryContract(DiffKind.BodyChanged, symbolId, symbolId, [location], [location],
                [new DiffEvidenceContract("body-changed", DiffEvidenceTextMode.LineHunks, "@@ -10,1 +10,1 @@\n-old\n+new\n", 1, 0, 0, false, HashA, HashB)], 1m)],
            PartialCoverage(false), ["behavioral-equivalence-not-inferred"], false, null, false);
    }

    private static ResponseContract Response(ResponseStatus status, CoverageContract coverage, bool truncated, IReadOnlyList<ErrorContract> errors, string? cursor = null)
    {
        IReadOnlyList<JsonElement> results = status == ResponseStatus.NotFound ? [] : [ContractJson.ToElement(new { symbolId = "example" })];
        return new ResponseContract("req-001", status == ResponseStatus.Error ? null : "gen-001", status,
            new FreshnessContract(FreshnessState.Verified, FreshnessState.Unknown), coverage, results, truncated, cursor,
            new FallbackContract(false, null), new RepairContract(RepairStatus.NotRequested, null), errors, null, null);
    }

    private static void ValidateReferenceFiles(JsonElement element, string currentFile, IReadOnlySet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("$ref"))
                {
                    var target = (property.Value.GetString() ?? string.Empty).Split('#', 2)[0];
                    Assert(target.Length == 0 || names.Contains(target), $"{currentFile} references missing schema {target}.");
                }
                ValidateReferenceFiles(property.Value, currentFile, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateReferenceFiles(item, currentFile, names);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CodeVirtualize.sln"))) return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static void Throws(Action action, string code)
    {
        try { action(); }
        catch (ContractValidationException exception) when (exception.ErrorCode == code) { return; }
        throw new InvalidOperationException($"Expected ContractValidationException '{code}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}