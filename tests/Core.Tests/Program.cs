using CodeVirtualize.Core.Analysis;
using CodeVirtualize.Core.Tests.Contracts;
using CodeVirtualize.Core.Tests.Storage;
using CodeVirtualize.Core.Tests.Resolution;

var root = Path.Combine(Path.GetTempPath(), "code-virtualize-core-trust");
var policy = new ExplicitWorkspaceTrustPolicy([root]);

Assert(policy.IsTrusted(root), "An explicitly trusted workspace must be trusted.");
Assert(!policy.IsTrusted(Path.Combine(root, "nested")), "Trust must not flow to nested directories.");
Assert(!DenyAllWorkspaceTrustPolicy.Instance.IsTrusted(root), "The default policy must deny trust.");

ContractTests.Run();
SemanticConfigFingerprintTests.Run();
SourceResolverTests.Run();

Console.WriteLine("Core trust policy, contract, and storage fingerprint tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
