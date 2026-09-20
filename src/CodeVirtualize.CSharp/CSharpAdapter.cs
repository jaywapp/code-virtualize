using CodeVirtualize.Core.Analysis;

namespace CodeVirtualize.CSharp;

public interface ITrustedSemanticLoader
{
    AnalysisResult Load(WorkspaceAnalysisRequest request);
}

public sealed class CSharpAdapter
{
    private readonly IWorkspaceTrustPolicy trustPolicy;
    private readonly ITrustedSemanticLoader semanticLoader;

    public CSharpAdapter(IWorkspaceTrustPolicy? trustPolicy = null, ITrustedSemanticLoader? semanticLoader = null)
    {
        this.trustPolicy = trustPolicy ?? DenyAllWorkspaceTrustPolicy.Instance;
        this.semanticLoader = semanticLoader ?? new UnsupportedSemanticLoader();
    }

    public AnalysisResult Analyze(WorkspaceAnalysisRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);

        if (request.Mode == AnalysisMode.TrustedSemantic && trustPolicy.IsTrusted(request.WorkspacePath))
        {
            return semanticLoader.Load(request);
        }

        var reason = request.Mode == AnalysisMode.TrustedSemantic
            ? "semantic-load-requires-explicit-workspace-trust"
            : "syntax-only-default-does-not-evaluate-projects-or-run-generators";

        return new AnalysisResult(
            request.Mode,
            "syntactic",
            false,
            "partial",
            [reason]);
    }

    private sealed class UnsupportedSemanticLoader : ITrustedSemanticLoader
    {
        public AnalysisResult Load(WorkspaceAnalysisRequest request) => new(
            request.Mode,
            "semantic",
            true,
            "partial",
            ["semantic-loader-not-installed", "no-restore-build-or-generator-execution-is-performed-by-this-baseline"]);
    }
}
