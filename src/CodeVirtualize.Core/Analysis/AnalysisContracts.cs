namespace CodeVirtualize.Core.Analysis;

public enum AnalysisMode
{
    SyntaxOnly,
    TrustedSemantic
}

public sealed record WorkspaceAnalysisRequest(string WorkspacePath, AnalysisMode Mode = AnalysisMode.SyntaxOnly);

public sealed record AnalysisResult(
    AnalysisMode RequestedMode,
    string AnalysisLevel,
    bool SemanticLoadAttempted,
    string CoverageLevel,
    IReadOnlyList<string> Limitations);

public interface IWorkspaceTrustPolicy
{
    bool IsTrusted(string workspacePath);
}

public sealed class ExplicitWorkspaceTrustPolicy : IWorkspaceTrustPolicy
{
    private readonly HashSet<string> trustedRoots;

    public ExplicitWorkspaceTrustPolicy(IEnumerable<string> trustedWorkspacePaths)
    {
        trustedRoots = new HashSet<string>(
            trustedWorkspacePaths.Select(Canonicalize),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsTrusted(string workspacePath) => trustedRoots.Contains(Canonicalize(workspacePath));

    public static string Canonicalize(string workspacePath) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
}

public sealed class DenyAllWorkspaceTrustPolicy : IWorkspaceTrustPolicy
{
    public static DenyAllWorkspaceTrustPolicy Instance { get; } = new();

    private DenyAllWorkspaceTrustPolicy()
    {
    }

    public bool IsTrusted(string workspacePath) => false;
}
