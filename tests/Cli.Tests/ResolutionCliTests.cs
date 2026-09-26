using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;

internal static class ResolutionCliTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cv-cli-resolve-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>", new UTF8Encoding(false));
            var sourcePath = Path.Combine(root, "Fixture.cs"); var source = "class Fixture\r\n{\r\n    public string Say()\r\n    {\r\n        return \"안녕 👋\";\r\n    }\r\n}\r\n";
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
            var buildExit = CliApplication.Run(["cv-build", "--workspace", root], TextWriter.Null, TextWriter.Null);
            Assert(buildExit == 3, "Syntax-only build must retain the partial-success exit code.");
            using var reader = new GenerationStore(Path.Combine(root, ".code-virtualize")).OpenCurrent();
            var shard = reader.Manifest.Shards.Single(x => x.Kind == "symbol");
            var symbol = reader.ReadShardRecords(shard.Path).Select(ContractJson.Deserialize<SymbolContract>).Single(x => x.Name == "Say");

            var jsonOut = new StringWriter(); var jsonErr = new StringWriter();
            var resolveExit = CliApplication.Run(["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "body", "--format", "json"], jsonOut, jsonErr);
            Assert(resolveExit == 3 && jsonErr.ToString().Length == 0, "Fresh partial resolve must keep stdout JSON and empty diagnostics.");
            var roundTrip = ContractJson.Deserialize<ResponseContract>(jsonOut.ToString());
            Assert(roundTrip.Source is not null && roundTrip.Source.Content.Contains("안녕 👋", StringComparison.Ordinal), "JSON response contract must round-trip.");
            using (var document = JsonDocument.Parse(jsonOut.ToString()))
            {
                Assert(document.RootElement.GetProperty("source").GetProperty("content").GetString()!.Contains("안녕 👋", StringComparison.Ordinal), "JSON must round-trip Unicode source.");
                Assert(document.RootElement.GetProperty("freshness").GetProperty("returnedFiles").GetString() == "verified", "JSON must expose verified freshness.");
            }

            var textOut = new StringWriter(); var textErr = new StringWriter();
            var textExit = CliApplication.Run(["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "header"], textOut, textErr);
            Assert(textExit == 3 && textOut.ToString().Contains("Source", StringComparison.Ordinal) && textOut.ToString().Contains("public string Say()", StringComparison.Ordinal), "Piped text output must preserve status and source.");
            Assert(textErr.ToString().Length == 0, "Fresh text output must not emit diagnostics.");

            var declarationOut = new StringWriter(); var declarationErr = new StringWriter();
            var declarationExit = CliApplication.Run(["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "declaration", "--format", "json"], declarationOut, declarationErr);
            Assert(declarationExit == 3 && declarationErr.ToString().Length == 0, "--part declaration must resolve like any other part.");
            var declarationResponse = ContractJson.Deserialize<ResponseContract>(declarationOut.ToString());
            Assert(declarationResponse.Source is not null && declarationResponse.Source.Content.Contains("안녕 👋", StringComparison.Ordinal) &&
                   declarationResponse.Source.Content.Contains("return", StringComparison.Ordinal) && declarationResponse.Source.NotModified == false,
                "--part declaration must return the whole declaration span, including the body, in one call.");
            var declarationHash = declarationResponse.Source!.ContentHash;

            var notModifiedOut = new StringWriter(); var notModifiedErr = new StringWriter();
            var notModifiedExit = CliApplication.Run(
                ["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "declaration", "--if-none-match", declarationHash, "--format", "json"],
                notModifiedOut, notModifiedErr);
            Assert(notModifiedExit == 3 && notModifiedErr.ToString().Length == 0, "A matching --if-none-match must still resolve successfully.");
            var notModifiedResponse = ContractJson.Deserialize<ResponseContract>(notModifiedOut.ToString());
            Assert(notModifiedResponse.Source is { NotModified: true, Content.Length: 0 } && notModifiedResponse.Source.ContentHash == declarationHash,
                "A matching --if-none-match must return notModified=true with empty content, not the old body again.");

            var notModifiedTextOut = new StringWriter();
            CliApplication.Run(["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "declaration", "--if-none-match", declarationHash], notModifiedTextOut, TextWriter.Null);
            Assert(notModifiedTextOut.ToString().Contains("NotModified   true", StringComparison.Ordinal) && !notModifiedTextOut.ToString().Contains("Source\n", StringComparison.Ordinal),
                "Text output must surface NotModified and omit the Source block when the body is unchanged.");

            var staleIfNoneMatchOut = new StringWriter(); var staleIfNoneMatchErr = new StringWriter();
            var staleIfNoneMatchExit = CliApplication.Run(
                ["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "declaration", "--if-none-match", $"sha256:{new string('0', 64)}", "--format", "json"],
                staleIfNoneMatchOut, staleIfNoneMatchErr);
            Assert(staleIfNoneMatchExit == 3 && staleIfNoneMatchErr.ToString().Length == 0, "A mismatched --if-none-match must still resolve successfully.");
            var mismatchResponse = ContractJson.Deserialize<ResponseContract>(staleIfNoneMatchOut.ToString());
            Assert(mismatchResponse.Source is { NotModified: false } && mismatchResponse.Source.Content.Length > 0,
                "A mismatched --if-none-match must return the full content, not a false notModified.");

            var invalidOut = new StringWriter(); var invalidErr = new StringWriter();
            Assert(CliApplication.Run(["cv-resolve", symbol.SymbolId, "--workspace", root, "--max-bytes", "0"], invalidOut, invalidErr) == 2,
                "Zero budget must be an input error.");
            Assert(invalidOut.ToString().Length == 0 && invalidErr.ToString().Contains("positive", StringComparison.Ordinal), "Input diagnostics belong on stderr.");

            var inspectOut = new StringWriter(); var inspectErr = new StringWriter();
            Assert(CliApplication.Run(["cv-inspect", "--workspace", root, "--format", "json"], inspectOut, inspectErr) == 3, "Inspect must preserve partial coverage exit meaning.");
            Assert(!inspectOut.ToString().Contains("안녕", StringComparison.Ordinal) && inspectErr.ToString().Length == 0, "Inspect must not expose source.");

            var validateOut = new StringWriter();
            Assert(CliApplication.Run(["cv-validate", "--workspace", root, "--format", "json"], validateOut, TextWriter.Null) == 3, "Validate must preserve generation coverage.");
            Assert(!validateOut.ToString().Contains("안녕", StringComparison.Ordinal), "Validate must not expose source.");

            var stamp = File.GetLastWriteTimeUtc(sourcePath); File.WriteAllText(sourcePath, source.Replace("안녕", "반가", StringComparison.Ordinal), new UTF8Encoding(false)); File.SetLastWriteTimeUtc(sourcePath, stamp);
            var staleOut = new StringWriter(); var staleErr = new StringWriter();
            Assert(CliApplication.Run(["cv-resolve", symbol.SymbolId, "--workspace", root, "--part", "body", "--format", "json"], staleOut, staleErr) == 3, "Stale source must use exit 3.");
            using (var staleDocument = JsonDocument.Parse(staleOut.ToString())) Assert(staleErr.ToString().Contains(ResolutionErrorCodes.SourceStale, StringComparison.Ordinal) && staleDocument.RootElement.GetProperty("source").ValueKind == JsonValueKind.Null, "Stale resolve must diagnose on stderr and return no old source.");
            File.Delete(sourcePath); var missingOut = new StringWriter(); var missingErr = new StringWriter();
            Assert(CliApplication.Run(["cv-validate", "--workspace", root, "--format", "json"], missingOut, missingErr) == 3, "Missing source validation must use exit 3.");
            Assert(missingErr.ToString().Contains(ResolutionErrorCodes.FileMissing, StringComparison.Ordinal) && !missingOut.ToString().Contains("안녕", StringComparison.Ordinal), "Missing validation must be explicit and source-free.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
