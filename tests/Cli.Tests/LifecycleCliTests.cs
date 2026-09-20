using System.Text;
using System.Text.Json;
using CodeVirtualize.Core.Snapshots;

internal static class LifecycleCliTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cv-cli-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>",
                new UTF8Encoding(false));
            const string initial = "namespace Fixture; public class Item { public string Read() => \"initial\"; }";
            File.WriteAllText(Path.Combine(root, "Item.cs"), initial, new UTF8Encoding(false));

            var startOutput = new StringWriter();
            var startError = new StringWriter();
            var startExit = CliApplication.Run(
                ["cv-update", "--workspace", root, "--session", "cli-session", "--start-session", "--format", "json"],
                startOutput,
                startError);
            Assert(startExit == 3 && startError.ToString().Length == 0,
                "Syntax-only session start must preserve the partial-success exit and clean stderr.");
            using (var document = JsonDocument.Parse(startOutput.ToString()))
            {
                Assert(document.RootElement.GetProperty("snapshot").GetProperty("sessionId").GetString() == "cli-session",
                    "Session start JSON must expose the session identity.");
                Assert(document.RootElement.GetProperty("snapshot").GetProperty("files").GetArrayLength() == 1,
                    "Session start JSON must expose the captured inventory.");
            }

            File.WriteAllText(
                Path.Combine(root, "Item.cs"),
                "namespace Fixture; public class Item { public string Read() => \"updated\"; }",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "Missed.cs"),
                "namespace Fixture; public class Missed { }",
                new UTF8Encoding(false));
            var updateOutput = new StringWriter();
            var updateError = new StringWriter();
            var updateExit = CliApplication.Run(
                ["cv-update", "--workspace", root, "--session", "cli-session", "--changed", "Item.cs", "--format", "json"],
                updateOutput,
                updateError);
            Assert(updateExit == 3 && updateError.ToString().Length == 0,
                "Syntax-only update must preserve the partial-success exit and clean stderr.");
            using (var document = JsonDocument.Parse(updateOutput.ToString()))
            {
                var rootElement = document.RootElement;
                Assert(rootElement.GetProperty("fallback").GetProperty("attempted").GetBoolean(),
                    "Missed hook reconciliation must be explicit in cv-update JSON.");
                Assert(rootElement.GetProperty("repair").GetProperty("status").GetString() == "succeeded",
                    "Successful generation repair must be explicit in cv-update JSON.");
                Assert(rootElement.GetProperty("reparsedFileCount").GetInt32() == 2,
                    "cv-update must report only changed/new files as reparsed.");
                Assert(!rootElement.GetProperty("usedFullRebuild").GetBoolean(),
                    "A source-only syntax update must remain incremental.");
            }

            File.Delete(Path.Combine(root, "Item.cs"));
            var baseBytes = new SessionSnapshotStore().ReadSource(
                Path.Combine(root, ".code-virtualize"),
                "cli-session",
                "Item.cs");
            Assert(Encoding.UTF8.GetString(baseBytes) == initial,
                "CLI session baseline must restore dirty-start bytes after current source deletion.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}