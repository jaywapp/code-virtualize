using CodeVirtualize.Core.Analysis;
using CodeVirtualize.CSharp;
using CodeVirtualize.Integration.Tests.Storage;
using CodeVirtualize.Integration.Tests.Lifecycle;
using CodeVirtualize.Integration.Tests.FailureInjection;
using CodeVirtualize.Integration.Tests.Security;
using CodeVirtualize.Integration.Tests.Diff;
using CodeVirtualize.Integration.Tests.AgentAdapter;

if (args.SequenceEqual(["--fixture-verifier"], StringComparer.Ordinal))
{
    FailureInjectionTests.RunFixtureVerifier();
    Console.WriteLine("Independent fixture JSON/source verifier passed.");
    return;
}

var fixtureRoot = Path.Combine(Directory.GetCurrentDirectory(), "tests", "Integration.Tests", "Fixtures", "UntrustedWorkspace");
var sentinel = Path.Combine(fixtureRoot, "generator-or-build-sentinel.txt");
File.Delete(sentinel);

var result = new CSharpAdapter().Analyze(new WorkspaceAnalysisRequest(fixtureRoot));
if (result.SemanticLoadAttempted || File.Exists(sentinel))
{
    throw new InvalidOperationException("The untrusted fixture ran a build or generator sentinel.");
}

GenerationStoreTests.Run();
LifecycleTests.Run();
FailureInjectionTests.Run();
SecurityTests.Run();
DiffTests.Run();
McpAgentAdapterTests.Run();

Console.WriteLine("Untrusted workspace, storage, lifecycle, security, diff, and MCP agent adapter integration tests passed.");

