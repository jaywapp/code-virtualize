using System.Text.Json;
using CodeVirtualize.Cli.Commands;
using CodeVirtualize.CSharp;
using CodeVirtualize.Core.Analysis;

return CliApplication.Run(args, Console.Out, Console.Error);

public static class CliApplication
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteHelp(output);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "cv-build" or "build" => BuildCommand.Run(args, output, error),
                "cv-find" or "find" => FindCommand.Run(args, output, error),
                "cv-update" or "update" => UpdateCommand.Run(args, output, error),
                "cv-resolve" or "resolve" => ResolutionCommands.Resolve(args, output, error),
                "cv-validate" or "validate" => ResolutionCommands.Validate(args, output, error),
                "cv-inspect" or "inspect" => ResolutionCommands.Inspect(args, output, error),
                "analyze" => Analyze(args, output, error),
                _ => Unknown(error)
            };
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static int Analyze(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var workspace = ReadOption(args, "--workspace");
        if (workspace is null)
        {
            error.WriteLine("Missing required option: --workspace.");
            return 2;
        }

        var trustedWorkspace = ReadOption(args, "--trust-workspace");
        var request = new WorkspaceAnalysisRequest(workspace, trustedWorkspace is null ? AnalysisMode.SyntaxOnly : AnalysisMode.TrustedSemantic);
        IWorkspaceTrustPolicy policy = trustedWorkspace is null
            ? DenyAllWorkspaceTrustPolicy.Instance
            : new ExplicitWorkspaceTrustPolicy([trustedWorkspace]);
        var result = new CSharpAdapter(policy).Analyze(request);
        output.WriteLine(JsonSerializer.Serialize(result));
        return result.SemanticLoadAttempted && result.Limitations.Contains("semantic-loader-not-installed", StringComparer.Ordinal) ? 3 : 0;
    }

    private static string? ReadOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Missing value for option: {option}.");
                }

                return args[index + 1];
            }
        }

        return null;
    }

    private static int Unknown(TextWriter error)
    {
        error.WriteLine("Unknown command. Run --help for usage.");
        return 2;
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Code-Virtualize CLI");
        output.WriteLine("Usage:");
        output.WriteLine("  code-virtualize cv-build --workspace <path> [--store <path>] [--trust-workspace <exact-path>] [--format text|json]");
        output.WriteLine("  code-virtualize cv-update --workspace <path> [--session <id>] [--changed <relative-path>]... [--writer-wait-ms <n>] [--format text|json]");
        output.WriteLine("  code-virtualize cv-update --workspace <path> --session <id> --start-session [--format text|json]");
        output.WriteLine("  code-virtualize cv-update --workspace <path> --session <id> --end-session [--format text|json]");
        output.WriteLine("  code-virtualize cv-find [query] (--workspace <path> | --store <path>) [--exact <name>] [--qualified <name>] [--name <name>]");
        output.WriteLine("      [--kind <kind>] [--accessibility <value>] [--project <id>] [--path <relative-path>] [--limit <n>] [--cursor <value>] [--format text|json]");
        output.WriteLine("  code-virtualize cv-resolve <symbol-id> --workspace <path> [--part header|body|context] [--path <relative-path>] [--declaration <index>]");
        output.WriteLine("      [--generation <id>] [--max-bytes <n>] [--max-lines <n>] [--context-lines <n>] [--format text|json]");
        output.WriteLine("  code-virtualize cv-validate --workspace <path> [--store <path>] [--generation <id>] [--format text|json]");
        output.WriteLine("  code-virtualize cv-inspect (--workspace <path> | --store <path>) [--generation <id>] [--format text|json]");
        output.WriteLine("Default build mode is syntax-only and never evaluates MSBuild, restores/builds, or runs analyzers/source generators.");
        output.WriteLine("Semantic analysis requires --trust-workspace to exactly name the workspace.");
    }
}
