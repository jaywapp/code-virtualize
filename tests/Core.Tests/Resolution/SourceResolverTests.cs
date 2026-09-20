using System.Text;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Tests.Resolution;

internal static class SourceResolverTests
{
    public static void Run()
    {
        ResolvePreservesCrlfBomAndUtf16();
        StaleSameSizeAndTimestampReturnsNoSource();
        MissingAmbiguousUnknownAndInvalidSpanAreExplicit();
    }

    private static void ResolvePreservesCrlfBomAndUtf16()
    {
        using var fixture = new Fixture("namespace 샘플;\r\npublic class 인사\r\n{\r\n    public string Say()\r\n    {\r\n        return \"안녕 👋\";\r\n    }\r\n}\r\n", true);
        var resolver = new SourceResolver();
        var header = resolver.Resolve(fixture.Request(SourcePart.Header));
        Assert(header.Source?.Content == "public string Say()", "Header must preserve exact source.");
        Assert(header.Source!.Span.Start == fixture.DeclarationStart, "Span must use UTF-16 offsets.");
        var body = resolver.Resolve(fixture.Request(SourcePart.Body));
        Assert(body.Source?.Content.Contains("안녕 👋", StringComparison.Ordinal) == true && body.Source.Content.Contains("\r\n", StringComparison.Ordinal), "Body must preserve Unicode and CRLF.");
        var context = resolver.Resolve(fixture.Request(SourcePart.Context));
        Assert(context.Source?.Content.StartsWith("public class", StringComparison.Ordinal) == true, "Context must expand by lines.");
        var truncated = resolver.Resolve(fixture.Request(SourcePart.Body, 8, 1));
        Assert(truncated.Status == ResponseStatus.Partial && truncated.Truncated && truncated.Source?.Truncated == true, "Small budgets must be explicit partial results.");
        Assert(truncated.Source!.Usage.Bytes <= 8 && truncated.Source.Usage.Lines <= 1, "Source must fit both budgets.");
        Throws<ContractValidationException>(() => resolver.Resolve(fixture.Request(SourcePart.Body) with { Budget = new(0, 1) }));
    }

    private static void StaleSameSizeAndTimestampReturnsNoSource()
    {
        using var fixture = new Fixture("class C { void M() { var s = \"one\"; } }", false);
        var stamp = File.GetLastWriteTimeUtc(fixture.SourcePath);
        File.WriteAllText(fixture.SourcePath, "class C { void M() { var s = \"two\"; } }", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(fixture.SourcePath, stamp);
        var response = new SourceResolver().Resolve(fixture.Request(SourcePart.Body));
        Assert(response.Freshness.ReturnedFiles == FreshnessState.Stale && response.Errors.Single().Code == ResolutionErrorCodes.SourceStale && response.Source is null,
            "Digest must detect same-size/same-mtime changes without returning old source.");
    }

    private static void MissingAmbiguousUnknownAndInvalidSpanAreExplicit()
    {
        using (var fixture = new Fixture("partial class C { void M() { } }", false, duplicate: true))
        {
            Assert(new SourceResolver().Resolve(fixture.Request(SourcePart.Header)).Errors.Single().Code == ResolutionErrorCodes.AmbiguousDeclaration, "Ambiguity must be explicit.");
            Assert(new SourceResolver().Resolve(fixture.Request(SourcePart.Header) with { DeclarationIndex = 0 }).Source is not null, "Index must select a declaration.");
            Assert(new SourceResolver().Resolve(fixture.Request(SourcePart.Header) with { SymbolId = $"sym_{new string('f', 64)}" }).Status == ResponseStatus.NotFound, "Unknown symbol must be not_found.");
            File.Delete(fixture.SourcePath);
            var missing = new SourceResolver().Resolve(fixture.Request(SourcePart.Header) with { DeclarationIndex = 0 });
            Assert(missing.Errors.Single().Code == ResolutionErrorCodes.FileMissing && missing.Source is null, "Deleted source must be explicit.");
        }
        using var invalid = new Fixture("class C { void M() { } }", false, spanStart: 999);
        var response = new SourceResolver().Resolve(invalid.Request(SourcePart.Header));
        Assert(response.Errors.Single().Code == ResolutionErrorCodes.SpanInvalid && response.Source is null, "Old spans must fail explicitly.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        public Fixture(string text, bool bom, bool duplicate = false, int? spanStart = null)
        {
            root = Path.Combine(Path.GetTempPath(), $"cv-resolve-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
            SourcePath = Path.Combine(root, "Sample.cs"); StorePath = Path.Combine(root, ".code-virtualize"); File.WriteAllText(SourcePath, text, new UTF8Encoding(bom));
            var bytes = File.ReadAllBytes(SourcePath); var hash = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}"; DeclarationStart = text.IndexOf("public string Say", StringComparison.Ordinal);
            if (DeclarationStart < 0) DeclarationStart = text.IndexOf("void M", StringComparison.Ordinal); if (DeclarationStart < 0) DeclarationStart = 0;
            var length = text.Length - DeclarationStart; var span = new TextSpanContract(spanStart ?? DeclarationStart, length, Line(text, DeclarationStart), Line(text, text.Length));
            SymbolId = $"sym_{new string('a', 64)}"; var location = new LocationContract("file-1", hash, span, "Sample.cs");
            var one = new DeclarationContract(SymbolId, location, DocumentKind.Source); IReadOnlyList<DeclarationContract> declarations = duplicate ? [one, one] : [one];
            var symbol = new SymbolContract(SymbolId, "project-1", "analysis-1", "method", "M", "C.M", "void M()", "public", null, 0, IdentityQuality.Semantic, [], null, declarations, []);
            var coverage = new CoverageContract("test", CoverageLevel.CompleteWithinScope, 1, 0, 0, 0, [], [], false);
            var manifest = new ManifestContract("gen-1", "workspace-1", "analysis-1", DateTimeOffset.UtcNow, "test-1", $"sha256:{new string('b', 64)}", GenerationState.Valid,
                [new("file-1", "Sample.cs", hash, bytes.Length, bom ? "utf-8-bom" : "utf-8", text.Contains("\r\n") ? "crlf" : "lf", ["project-1"])], [], [], coverage);
            new GenerationStore(StorePath).Publish(new(manifest, [new("symbol", "symbols.jsonl", ContractSchemas.Symbol, [ContractJson.Serialize(symbol)])]));
        }
        public string SourcePath { get; } public string StorePath { get; } public string SymbolId { get; } public int DeclarationStart { get; }
        public ResolveRequest Request(SourcePart part, int bytes = 65536, int lines = 400) => new(root, StorePath, SymbolId, part, new(bytes, lines));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static int Line(string text, int position) { var line = 1; for (var i = 0; i < position; i++) if (text[i] == '\n') line++; return line; }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
