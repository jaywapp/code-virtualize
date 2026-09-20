namespace CodeVirtualize.Mcp;

public sealed record McpOptions(string WorkspacePath, string StorePath, TimeSpan Timeout)
{
    public static McpOptions Parse(IReadOnlyList<string> args)
    {
        var workspace = Read(args, "--workspace") ?? throw new ArgumentException("Missing required option: --workspace.");
        workspace = Path.GetFullPath(workspace);
        var store = Path.GetFullPath(Read(args, "--store") ?? Path.Combine(workspace, ".code-virtualize"));
        var timeoutText = Read(args, "--timeout-ms") ?? "30000";
        if (!int.TryParse(timeoutText, out var timeoutMs) || timeoutMs is < 1 or > 300000)
            throw new ArgumentException("--timeout-ms must be between 1 and 300000.");
        return new(workspace, store, TimeSpan.FromMilliseconds(timeoutMs));
    }

    private static string? Read(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
            if (args[index] == option && index + 1 < args.Count) return args[index + 1];
        return null;
    }
}
