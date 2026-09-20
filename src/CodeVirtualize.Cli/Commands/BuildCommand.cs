using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.CSharp;

namespace CodeVirtualize.Cli.Commands;

internal static class BuildCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        try
        {
            var workspace = CliOptions.Read(args, "--workspace") ?? throw new ArgumentException("Missing required option: --workspace.");
            var format = CliOptions.Format(args);
            var store = CliOptions.StorePath(args, workspace);
            var trustedWorkspace = CliOptions.Read(args, "--trust-workspace");
            var mode = trustedWorkspace is null ? AnalysisMode.SyntaxOnly : AnalysisMode.TrustedSemantic;
            IWorkspaceTrustPolicy policy = trustedWorkspace is null
                ? DenyAllWorkspaceTrustPolicy.Instance
                : new ExplicitWorkspaceTrustPolicy([trustedWorkspace]);
            var result = new CSharpIndexBuilder(policy).Build(new CSharpBuildRequest(workspace, store, mode));
            if (format == "json")
            {
                output.WriteLine(ContractJson.Serialize(result.Manifest));
            }
            else
            {
                output.WriteLine($"Generation    {result.Manifest.GenerationId}");
                output.WriteLine($"State         {result.Manifest.State.ToString().ToLowerInvariant()}");
                output.WriteLine($"Coverage      {Text(result.Manifest.Coverage.Level)} / {result.Manifest.Coverage.Scope}");
                output.WriteLine($"Files         analyzed={result.Manifest.Coverage.AnalyzedFiles} excluded={result.Manifest.Coverage.ExcludedFiles} failed={result.Manifest.Coverage.FailedFiles} unknown={result.Manifest.Coverage.UnknownFiles}");
                output.WriteLine($"Projects      total={result.Manifest.Projects.Count} failed={result.Manifest.Coverage.FailedProjects.Count}");
                output.WriteLine($"Symbols       {result.Symbols.Count}");
                output.WriteLine($"Published     {result.Published.ToString().ToLowerInvariant()}");
                foreach (var limitation in result.Manifest.Coverage.Limitations)
                {
                    output.WriteLine($"Limitation    {limitation}");
                }
            }

            return result.Manifest.Coverage.Level == CoverageLevel.CompleteWithinScope ? 0 : 3;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (DirectoryNotFoundException exception)
        {
            error.WriteLine(exception.Message);
            return 4;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static string Text(CoverageLevel value) => value switch
    {
        CoverageLevel.CompleteWithinScope => "complete_within_scope",
        CoverageLevel.Partial => "partial",
        _ => "unknown"
    };
}
