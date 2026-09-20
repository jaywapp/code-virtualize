using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Storage;

namespace CodeVirtualize.Core.Tests.Storage;

internal static class SemanticConfigFingerprintTests
{
    public static void Run()
    {
        WorkspaceFingerprintIsDeterministic();
        AnalysisFingerprintIsDeterministicAndSemantic();
    }

    private static void WorkspaceFingerprintIsDeterministic()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "code-virtualize-fingerprint"));
        var first = SemanticConfigFingerprint.CreateWorkspaceKey(new WorkspaceFingerprintInput(root, "worktree-01"));
        var second = SemanticConfigFingerprint.CreateWorkspaceKey(new WorkspaceFingerprintInput(root + Path.DirectorySeparatorChar, " worktree-01 "));
        Assert(first == second, "Equivalent workspace identities must produce the same key.");
        Assert(first != SemanticConfigFingerprint.CreateWorkspaceKey(new WorkspaceFingerprintInput(root, "worktree-02")), "Worktree identity must affect the workspace key.");
        AssertSha256(first);
    }

    private static void AnalysisFingerprintIsDeterministicAndSemantic()
    {
        var input = new AnalysisConfigFingerprintInput(
            ContractVersions.Current,
            "adapter-1",
            "compiler-1",
            ["project-b", "project-a"],
            ["reference-b", "reference-a"],
            ["net9.0", "net8.0"],
            ["TRACE", "DEBUG"],
            AnalysisMode.SyntaxOnly);
        var equivalent = input with
        {
            ProjectGraph = ["project-a", "project-b", "project-a"],
            References = ["reference-a", "reference-b"],
            TargetFrameworks = ["net8.0", "net9.0"],
            Defines = ["DEBUG", "TRACE"]
        };

        var baseline = SemanticConfigFingerprint.CreateAnalysisKey(input);
        Assert(baseline == SemanticConfigFingerprint.CreateAnalysisKey(equivalent), "Set-like semantic configuration must be order independent.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { SchemaVersion = 2 }), "Schema version must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { AdapterVersion = "adapter-2" }), "Adapter version must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { CompilerVersion = "compiler-2" }), "Compiler version must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { ProjectGraph = ["project-a", "project-c"] }), "Project graph must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { References = ["reference-c"] }), "References must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { TargetFrameworks = ["net9.0"] }), "Target framework must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { Defines = ["RELEASE"] }), "Defines must affect the analysis key.");
        Assert(baseline != SemanticConfigFingerprint.CreateAnalysisKey(input with { TrustMode = AnalysisMode.TrustedSemantic }), "Trust mode must affect the analysis key.");
        AssertSha256(baseline);
    }

    private static void AssertSha256(string value)
    {
        Assert(value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal), "Fingerprints must use the SHA-256 contract format.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}