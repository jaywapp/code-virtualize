using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Snapshots;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Integration.Tests.AgentAdapter;

internal static class McpAgentAdapterTests
{
    public static void Run()
    {
        using var fixture = new Fixture();
        RoundTripAndFreshness(fixture);
        TimeoutCancellationCrashAndMalformed(fixture);
        SettingsAndHooks(fixture);
    }

    private static void RoundTripAndFreshness(Fixture fixture)
    {
        using var client = fixture.StartMcp();
        var initialize = client.Request(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "fixture-client", version = "1.0.0" } } });
        Assert(initialize.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-06-18", "MCP must negotiate the pinned handshake version.");
        client.Notify(new { jsonrpc = "2.0", method = "notifications/initialized" });
        var list = client.Request(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
        var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
        Assert(names.SequenceEqual(["cv_find", "cv_get", "cv_impact"]), "tools/list must expose only the three minimal tools.");
        var cvGetTool = list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "cv_get");
        var partEnum = cvGetTool.GetProperty("inputSchema").GetProperty("properties").GetProperty("part").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert(partEnum.SequenceEqual(["header", "declaration", "body", "context"]), "cv_get's part enum must include declaration alongside header/body/context.");
        Assert(cvGetTool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("ifNoneMatch", out var ifNoneMatchSchema) &&
               ifNoneMatchSchema.GetProperty("pattern").GetString() == "^sha256:[0-9a-f]{64}$",
            "cv_get must declare an ifNoneMatch input matching the sha256 slice content hash format.");

        var find = client.Request(Call(3, "cv_find", new { exact = "Hello" }));
        var findResult = find.GetProperty("result");
        Assert(!findResult.GetProperty("isError").GetBoolean(), "cv_find must return an engine result.");
        var symbolId = findResult.GetProperty("structuredContent").GetProperty("results")[0].GetProperty("symbolId").GetString()!;
        var get = client.Request(Call(4, "cv_get", new { symbolId, part = "body" }));
        Assert(get.GetProperty("result").GetProperty("structuredContent").GetProperty("source").GetProperty("content").GetString()!.Contains("hello", StringComparison.Ordinal), "cv_get must return verified source.");

        var declaration = client.Request(Call(41, "cv_get", new { symbolId, part = "declaration" }));
        var declarationSource = declaration.GetProperty("result").GetProperty("structuredContent").GetProperty("source");
        Assert(declarationSource.GetProperty("notModified").GetBoolean() == false, "The first declaration request must not be notModified.");
        var declarationHash = declarationSource.GetProperty("contentHash").GetString()!;
        var notModified = client.Request(Call(42, "cv_get", new { symbolId, part = "declaration", ifNoneMatch = declarationHash }));
        var notModifiedSource = notModified.GetProperty("result").GetProperty("structuredContent").GetProperty("source");
        Assert(notModifiedSource.GetProperty("notModified").GetBoolean() && notModifiedSource.GetProperty("content").GetString() == string.Empty,
            "A matching ifNoneMatch must return notModified=true with empty content over MCP.");

        var impact = client.Request(Call(5, "cv_impact", new { symbolIds = new[] { symbolId }, maxDepth = 1, maxResults = 20, maxSourceFiles = 20, pageSize = 20 }));
        Assert(impact.GetProperty("result").TryGetProperty("structuredContent", out _), "cv_impact must return structured content.");

        var stamp = File.GetLastWriteTimeUtc(fixture.SourcePath);
        File.WriteAllText(fixture.SourcePath, fixture.Source.Replace("hello", "jello", StringComparison.Ordinal), new UTF8Encoding(false)); File.SetLastWriteTimeUtc(fixture.SourcePath, stamp);
        var stale = client.Request(Call(6, "cv_get", new { symbolId, part = "body" }));
        Assert(stale.GetProperty("result").GetProperty("isError").GetBoolean() && stale.GetRawText().Contains("SOURCE_STALE", StringComparison.Ordinal), "cv_get freshness must remain digest-based when hooks are disabled.");
        File.WriteAllText(fixture.SourcePath, fixture.Source, new UTF8Encoding(false));
    }

    private static void TimeoutCancellationCrashAndMalformed(Fixture fixture)
    {
        using (var malformed = fixture.StartMcp())
        {
            malformed.Raw("{");
            Assert(malformed.Read().GetProperty("error").GetProperty("code").GetInt32() == -32700, "Malformed JSON must return Parse error.");
        }
        using (var timeout = fixture.StartMcp(10, 200))
        {
            timeout.Initialize();
            var result = timeout.Request(Call(10, "cv_find", new { exact = "Hello" }));
            Assert(result.GetProperty("result").GetProperty("isError").GetBoolean() && result.GetRawText().Contains("TIMEOUT", StringComparison.Ordinal), "Bounded timeout must be an explicit tool error.");
        }
        using (var cancel = fixture.StartMcp(1000, 200))
        {
            cancel.Initialize();
            cancel.Raw(JsonSerializer.Serialize(Call(11, "cv_find", new { exact = "Hello" })));
            cancel.Notify(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 11, reason = "fixture" } });
            Assert(cancel.Read().GetProperty("error").GetProperty("code").GetInt32() == -32800, "Cancellation must stop the pending call.");
        }
        using var crashed = fixture.StartMcp();
        crashed.Kill();
        Assert(crashed.HasExited, "A killed MCP worker must be observable as a process failure.");
    }

    private static void SettingsAndHooks(Fixture fixture)
    {
        var settings = Path.Combine(fixture.Root, ".claude", "settings.json"); var mcp = Path.Combine(fixture.Root, ".mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var originalSettings = "{\"permissions\":{\"allow\":[\"Read\"]},\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\"}]}}";
        var originalMcp = "{\"mcpServers\":{\"existing\":{\"command\":\"existing-command\"}}}";
        File.WriteAllText(settings, originalSettings, new UTF8Encoding(false)); File.WriteAllText(mcp, originalMcp, new UTF8Encoding(false));
        fixture.Manage("Install", dryRun: true);
        Assert(File.ReadAllText(settings) == originalSettings && File.ReadAllText(mcp) == originalMcp, "Dry-run must not mutate settings.");
        fixture.Manage("Install", dryRun: false);
        using (var settingsJson = JsonDocument.Parse(File.ReadAllText(settings)))
            Assert(settingsJson.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString() == "Read" && settingsJson.RootElement.GetProperty("hooks").TryGetProperty("SessionStart", out _), "Install must merge hooks without dropping settings.");
        using (var mcpJson = JsonDocument.Parse(File.ReadAllText(mcp)))
            Assert(mcpJson.RootElement.GetProperty("mcpServers").TryGetProperty("existing", out _) && mcpJson.RootElement.GetProperty("mcpServers").TryGetProperty("code-virtualize", out _), "Install must preserve existing MCP servers.");
        fixture.Manage("Uninstall", dryRun: true);
        Assert(File.Exists(settings), "Uninstall dry-run must preserve files.");
        fixture.Manage("Uninstall", dryRun: false);
        Assert(File.ReadAllText(settings) == originalSettings && File.ReadAllText(mcp) == originalMcp, "Uninstall must byte-restore prior settings.");

        fixture.Hook("SessionStart", new { session_id = "adapter-session" });
        Assert(new SessionSnapshotStore().IsPinned(fixture.StorePath, "adapter-session"), "SessionStart hook must pin a snapshot.");
        File.WriteAllText(fixture.SourcePath, fixture.Source.Replace("hello", "updated", StringComparison.Ordinal), new UTF8Encoding(false));
        fixture.Hook("PostToolUse", new { session_id = "adapter-session", tool_input = new { file_path = fixture.SourcePath } });
        fixture.Hook("SessionEnd", new { session_id = "adapter-session" });
        Assert(!new SessionSnapshotStore().IsPinned(fixture.StorePath, "adapter-session"), "SessionEnd hook must close the session.");
    }

    private static object Call(int id, string name, object arguments) => new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } };
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"cv-agent-adapter-{Guid.NewGuid():N}"); Directory.CreateDirectory(Root);
            Source = "namespace Demo; public class Greeter { public string Hello() => \"hello\"; }";
            SourcePath = Path.Combine(Root, "Greeter.cs"); File.WriteAllText(SourcePath, Source, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>", new UTF8Encoding(false));
            StorePath = Path.Combine(Root, ".code-virtualize"); new CSharpIndexBuilder().Build(new(Root, StorePath, AnalysisMode.SyntaxOnly));
            RepoRoot = Directory.GetCurrentDirectory(); McpDll = Path.Combine(RepoRoot, "src", "CodeVirtualize.Mcp", "bin", "Release", "net9.0", "CodeVirtualize.Mcp.dll");
            CliDll = Path.Combine(RepoRoot, "src", "CodeVirtualize.Cli", "bin", "Release", "net9.0", "CodeVirtualize.Cli.dll");
        }
        public string Root { get; } public string Source { get; } public string SourcePath { get; } public string StorePath { get; } public string RepoRoot { get; } public string McpDll { get; } public string CliDll { get; }
        public Client StartMcp(int timeoutMs = 30000, int delayMs = 0) => new(McpDll, Root, timeoutMs, delayMs);
        public void Manage(string action, bool dryRun)
        {
            var args = $"-NoProfile -File \"{Path.Combine(RepoRoot, "integrations", "claude", "manage.ps1")}\" -Action {action} -Workspace \"{Root}\" -McpDll \"{McpDll}\" -CliDll \"{CliDll}\"" + (dryRun ? " -DryRun" : string.Empty);
            Run("powershell.exe", args, null);
        }
        public void Hook(string eventName, object payload)
        {
            var args = $"-NoProfile -File \"{Path.Combine(RepoRoot, "integrations", "claude", "hook.ps1")}\" -Event {eventName} -Workspace \"{Root}\" -CliDll \"{CliDll}\"";
            Run("powershell.exe", args, JsonSerializer.Serialize(payload));
        }
        private static void Run(string file, string args, string? stdin)
        {
            using var process = new Process { StartInfo = new(file, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            process.Start(); if (stdin is not null) process.StandardInput.Write(stdin); process.StandardInput.Close(); var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit(30000);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Integration process failed: {stderr} {stdout}");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class Client : IDisposable
    {
        private readonly Process process;
        public Client(string dll, string workspace, int timeoutMs, int delayMs)
        {
            process = new() { StartInfo = new("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add(dll); process.StartInfo.ArgumentList.Add("--workspace"); process.StartInfo.ArgumentList.Add(workspace); process.StartInfo.ArgumentList.Add("--timeout-ms"); process.StartInfo.ArgumentList.Add(timeoutMs.ToString());
            if (delayMs > 0) process.StartInfo.Environment["CODE_VIRTUALIZE_MCP_TEST_DELAY_MS"] = delayMs.ToString(); process.Start(); _ = process.StandardError.ReadToEndAsync();
        }
        public bool HasExited => process.HasExited;
        public void Initialize() { Request(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "fixture", version = "1.0" } } }); Notify(new { jsonrpc = "2.0", method = "notifications/initialized" }); }
        public JsonElement Request(object request) { Raw(JsonSerializer.Serialize(request)); return Read(); }
        public void Notify(object request) => Raw(JsonSerializer.Serialize(request));
        public void Raw(string line) { process.StandardInput.WriteLine(line); process.StandardInput.Flush(); }
        public JsonElement Read() { var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() ?? throw new InvalidOperationException("MCP stdout closed."); return JsonDocument.Parse(line).RootElement.Clone(); }
        public void Kill() { if (!process.HasExited) process.Kill(true); process.WaitForExit(5000); }
        public void Dispose() { if (!process.HasExited) { process.StandardInput.Close(); if (!process.WaitForExit(5000)) process.Kill(true); } process.Dispose(); }
    }
}
