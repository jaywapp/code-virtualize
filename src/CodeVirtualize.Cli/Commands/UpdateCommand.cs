using System.Text.Json;
using System.Text.Json.Serialization;
using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Snapshots;
using CodeVirtualize.Core.Storage;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Cli.Commands;

internal static class UpdateCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        try
        {
            var workspace = CliOptions.Read(args, "--workspace")
                ?? throw new ArgumentException("Missing required option: --workspace.");
            var store = CliOptions.StorePath(args, workspace);
            var format = CliOptions.Format(args);
            var session = CliOptions.Read(args, "--session");
            var trustedWorkspace = CliOptions.Read(args, "--trust-workspace");
            var mode = trustedWorkspace is null ? AnalysisMode.SyntaxOnly : AnalysisMode.TrustedSemantic;
            IWorkspaceTrustPolicy policy = trustedWorkspace is null
                ? DenyAllWorkspaceTrustPolicy.Instance
                : new ExplicitWorkspaceTrustPolicy([trustedWorkspace]);
            var wait = TimeSpan.FromMilliseconds(NonNegative(args, "--writer-wait-ms", 500));
            var service = new CSharpLifecycleService(policy);

            if (CliOptions.Has(args, "--end-session"))
            {
                if (session is null)
                {
                    throw new ArgumentException("--end-session requires --session.");
                }

                new SessionSnapshotStore().Close(store, session);
                if (format == "json")
                {
                    output.WriteLine(JsonSerializer.Serialize(new { sessionId = session, state = "closed" }, JsonOptions));
                }
                else
                {
                    output.WriteLine($"Session       {session}");
                    output.WriteLine("State         closed");
                }

                return 0;
            }

            if (CliOptions.Has(args, "--start-session"))
            {
                if (session is null)
                {
                    throw new ArgumentException("--start-session requires --session.");
                }

                var started = service.StartSession(new CSharpSessionStartRequest(
                    workspace,
                    store,
                    session,
                    mode,
                    CliOptions.Read(args, "--configuration") ?? "Debug",
                    wait));
                if (format == "json")
                {
                    output.WriteLine(JsonSerializer.Serialize(started, JsonOptions));
                }
                else
                {
                    output.WriteLine($"Session       {started.Snapshot.SessionId}");
                    output.WriteLine($"Generation    {started.Manifest.GenerationId}");
                    output.WriteLine($"SnapshotFiles {started.Snapshot.Files.Count}");
                    output.WriteLine($"Pinned        true");
                    output.WriteLine($"Coverage      {Snake(started.Manifest.Coverage.Level)} / {started.Manifest.Coverage.Scope}");
                }

                return started.Manifest.Coverage.Level == CoverageLevel.CompleteWithinScope ? 0 : 3;
            }

            var result = service.Update(new CSharpUpdateRequest(
                workspace,
                store,
                session,
                CliOptions.ReadAll(args, "--changed"),
                mode,
                CliOptions.Read(args, "--configuration") ?? "Debug",
                wait));
            if (format == "json")
            {
                output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                output.WriteLine($"Previous      {result.PreviousGenerationId}");
                output.WriteLine($"Generation    {result.Manifest.GenerationId}");
                output.WriteLine($"Published     {result.Published.ToString().ToLowerInvariant()}");
                output.WriteLine($"Changes       {result.Reconciliation.Changes.Count}");
                output.WriteLine($"Incremental   full={result.UsedFullRebuild.ToString().ToLowerInvariant()} reparsed={result.ReparsedFileCount} reused-symbols={result.ReusedSymbolCount}");
                foreach (var change in result.Reconciliation.Changes)
                {
                    var rename = change.PreviousPath is null ? string.Empty : $" <- {change.PreviousPath}";
                    output.WriteLine($"Change        {Snake(change.Kind)} {change.Path}{rename}");
                }

                output.WriteLine($"Invalidated   {result.Reconciliation.InvalidatedProjects.Count}");
                foreach (var project in result.Reconciliation.InvalidatedProjects)
                {
                    output.WriteLine($"Project       {project.ProjectId} {project.Reason}");
                }

                output.WriteLine($"Fallback      attempted={result.Fallback.Attempted.ToString().ToLowerInvariant()} reason={result.Fallback.Reason ?? "none"}");
                output.WriteLine($"Repair        {Snake(result.Repair.Status)} reason={result.Repair.Reason ?? "none"}");
                output.WriteLine($"Coverage      {Snake(result.Manifest.Coverage.Level)} / {result.Manifest.Coverage.Scope}");
            }

            return result.Manifest.Coverage.Level == CoverageLevel.CompleteWithinScope ? 0 : 3;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (SnapshotException exception)
        {
            error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
            return 3;
        }
        catch (StorageException exception)
        {
            error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
            return exception.ErrorCode == StorageErrorCodes.Busy ? 3 : 4;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static int NonNegative(IReadOnlyList<string> args, string option, int defaultValue)
    {
        var text = CliOptions.Read(args, option);
        if (text is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, out var value) || value < 0)
        {
            throw new ArgumentException($"{option} must be a non-negative integer.");
        }

        return value;
    }

    private static string Snake<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}