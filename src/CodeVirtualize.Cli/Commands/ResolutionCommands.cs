using System.Text.Json;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Cli.Commands;

internal static class ResolutionCommands
{
    public static int Resolve(IReadOnlyList<string> args, TextWriter output, TextWriter error) => Execute(() =>
    {
        var workspace = Required(args, "--workspace");
        var symbol = CliOptions.Read(args, "--symbol") ?? Positional(args) ?? throw new ArgumentException("Missing required option: --symbol.");
        if (!Enum.TryParse<SourcePart>(CliOptions.Read(args, "--part") ?? "header", true, out var part)) throw new ArgumentException("--part must be header, declaration, body, or context.");
        var response = new SourceResolver().Resolve(new(workspace, CliOptions.StorePath(args, workspace), symbol, part,
            new(Positive(args, "--max-bytes", 65536), Positive(args, "--max-lines", 400)), CliOptions.Read(args, "--generation"),
            CliOptions.Read(args, "--path"), OptionalNonNegative(args, "--declaration"), NonNegative(args, "--context-lines", 2),
            null, CliOptions.Read(args, "--if-none-match")));
        Write(response, CliOptions.Format(args), output, error, "resolve"); return Exit(response);
    }, error);

    public static int Validate(IReadOnlyList<string> args, TextWriter output, TextWriter error) => Execute(() =>
    {
        var workspace = Required(args, "--workspace");
        var response = new GenerationDiagnostics().Validate(workspace, CliOptions.StorePath(args, workspace), CliOptions.Read(args, "--generation"));
        Write(response, CliOptions.Format(args), output, error, "validate"); return Exit(response);
    }, error);

    public static int Inspect(IReadOnlyList<string> args, TextWriter output, TextWriter error) => Execute(() =>
    {
        var workspace = CliOptions.Read(args, "--workspace");
        var response = new GenerationDiagnostics().Inspect(CliOptions.StorePath(args, workspace), CliOptions.Read(args, "--generation"));
        Write(response, CliOptions.Format(args), output, error, "inspect"); return Exit(response);
    }, error);

    private static void Write(ResponseContract response, string format, TextWriter output, TextWriter error, string command)
    {
        if (format == "json") output.WriteLine(ContractJson.Serialize(response));
        else
        {
            output.WriteLine($"Command       {command}");
            output.WriteLine($"Generation    {response.GenerationId ?? "none"}");
            output.WriteLine($"Status        {Snake(response.Status)}");
            output.WriteLine($"Freshness     returned-files: {Snake(response.Freshness.ReturnedFiles)} / workspace: {Snake(response.Freshness.Workspace)}");
            output.WriteLine($"Coverage      {Snake(response.Coverage.Level)} / {response.Coverage.Scope}");
            if (command == "resolve" && response.Results.Count == 1)
            {
                var item = response.Results[0];
                output.WriteLine($"Symbol        {item.GetProperty("symbolId").GetString()}");
                output.WriteLine($"Path          {item.GetProperty("path").GetString()}");
                output.WriteLine($"Part          {item.GetProperty("part").GetString()}");
                if (response.Source is not null)
                {
                    output.WriteLine($"Range         {response.Source.Span.StartLine}-{response.Source.Span.EndLine} utf16:{response.Source.Span.Start}+{response.Source.Span.Length}");
                    output.WriteLine($"Budget        bytes={response.Source.Usage.Bytes}/{response.Source.Budget.MaxBytes} lines={response.Source.Usage.Lines}/{response.Source.Budget.MaxLines} truncated={response.Source.Truncated.ToString().ToLowerInvariant()}");
                    output.WriteLine($"ContentHash   {response.Source.ContentHash}");
                    output.WriteLine($"NotModified   {response.Source.NotModified.ToString().ToLowerInvariant()}");
                    if (!response.Source.NotModified)
                    {
                        output.WriteLine("Source"); output.Write(response.Source.Content); if (!response.Source.Content.EndsWith('\n')) output.WriteLine();
                    }
                }
            }
            else if (command == "validate" && response.Results.Count == 1)
            {
                var item = response.Results[0];
                output.WriteLine($"Files         total={item.GetProperty("totalFiles")} verified={item.GetProperty("verifiedFiles")} stale={item.GetProperty("staleFiles")} missing={item.GetProperty("missingFiles")} invalid={item.GetProperty("invalidFiles")}");
            }
            else if (command == "inspect" && response.Results.Count == 1)
            {
                var item = response.Results[0]; output.WriteLine($"State         {item.GetProperty("state").GetString()}");
                output.WriteLine($"Created       {item.GetProperty("createdAt").GetDateTimeOffset():O}");
                output.WriteLine($"WorkspaceKey  {item.GetProperty("workspaceKey").GetString()}");
                output.WriteLine($"Files         {item.GetProperty("fileCount")}  Projects {item.GetProperty("projectCount")}  Shards {item.GetProperty("shardCount")}");
                foreach (var project in item.GetProperty("projects").EnumerateArray())
                    output.WriteLine($"Project       {project.GetProperty("projectId").GetString()} load={project.GetProperty("loadStatus").GetString()} analysis={project.GetProperty("analysisLevel").GetString()}");
                foreach (var shard in item.GetProperty("shards").EnumerateArray())
                    output.WriteLine($"Shard         {shard.GetProperty("kind").GetString()} path={shard.GetProperty("path").GetString()} records={shard.GetProperty("recordCount")}");
            }
            foreach (var limitation in response.Coverage.Limitations) output.WriteLine($"Limitation    {limitation}");
        }
        foreach (var issue in response.Errors) error.WriteLine($"{issue.Code}: {issue.Message}");
    }

    private static int Exit(ResponseContract r) => r.Status == ResponseStatus.NotFound ? 0 :
        r.Status is ResponseStatus.Partial or ResponseStatus.Error || r.Coverage.Level != CoverageLevel.CompleteWithinScope ? 3 : 0;
    private static int Execute(Func<int> action, TextWriter error)
    {
        try { return action(); }
        catch (ArgumentException e) { error.WriteLine(e.Message); return 2; }
        catch (ContractValidationException e) { error.WriteLine($"{e.ErrorCode}: {e.Message}"); return 2; }
        catch (StorageException e) { error.WriteLine($"{e.ErrorCode}: {e.Message}"); return 4; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error.WriteLine(e.Message); return 4; }
    }
    private static string Required(IReadOnlyList<string> a, string n) => CliOptions.Read(a, n) ?? throw new ArgumentException($"Missing required option: {n}.");
    private static int Positive(IReadOnlyList<string> a, string n, int d) { var s = CliOptions.Read(a, n); if (s is null) return d; if (!int.TryParse(s, out var v) || v <= 0) throw new ArgumentException($"{n} must be a positive integer."); return v; }
    private static int NonNegative(IReadOnlyList<string> a, string n, int d) { var s = CliOptions.Read(a, n); if (s is null) return d; if (!int.TryParse(s, out var v) || v < 0) throw new ArgumentException($"{n} must be a non-negative integer."); return v; }
    private static int? OptionalNonNegative(IReadOnlyList<string> a, string n) { var s = CliOptions.Read(a, n); if (s is null) return null; if (!int.TryParse(s, out var v) || v < 0) throw new ArgumentException($"{n} must be a non-negative integer."); return v; }
    private static string? Positional(IReadOnlyList<string> a) { for (var i = 1; i < a.Count; i++) { if (a[i].StartsWith("--")) { i++; continue; } return a[i]; } return null; }
    private static string Snake<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
