namespace CodeVirtualize.Mcp;

internal static class ToolDefinitions
{
    public static IReadOnlyList<object> All { get; } =
    [
        new
        {
            name = "cv_find",
            description = "Find indexed C# symbols. Inspect status, coverage, truncation, and freshness before selecting a symbol.",
            inputSchema = Schema(new Dictionary<string, object>
            {
                ["query"] = String(), ["exact"] = String(), ["qualifiedName"] = String(), ["name"] = String(),
                ["kind"] = String(), ["accessibility"] = String(), ["projectId"] = String(), ["path"] = String(),
                ["limit"] = Integer(1, 1000), ["cursor"] = String()
            })
        },
        new
        {
            name = "cv_get",
            description = "Return a digest-verified source slice for one indexed symbol. Stale or missing source is an explicit error and never returns the old span.",
            inputSchema = Schema(new Dictionary<string, object>
            {
                ["symbolId"] = String("^sym_[0-9a-f]{64}$"), ["part"] = Enum("header", "declaration", "body", "context"),
                ["generationId"] = String(), ["path"] = String(), ["declaration"] = Integer(0, int.MaxValue),
                ["maxBytes"] = Integer(1, int.MaxValue), ["maxLines"] = Integer(1, int.MaxValue), ["contextLines"] = Integer(0, 100),
                ["ifNoneMatch"] = String("^sha256:[0-9a-f]{64}$")
            }, ["symbolId"])
        },
        new
        {
            name = "cv_impact",
            description = "Return bounded static references and separately marked lexical candidates for indexed symbols.",
            inputSchema = Schema(new Dictionary<string, object>
            {
                ["symbolIds"] = new { type = "array", items = String("^sym_[0-9a-f]{64}$"), minItems = 1 },
                ["maxDepth"] = Integer(0, 20), ["maxResults"] = Integer(1, 10000),
                ["maxSourceFiles"] = Integer(1, 100000), ["pageSize"] = Integer(1, 10000),
                ["includeLexicalCandidates"] = new { type = "boolean" }, ["generationId"] = String(), ["cursor"] = String()
            }, ["symbolIds"])
        }
    ];

    private static object Schema(IReadOnlyDictionary<string, object> properties, string[]? required = null) => new
    {
        type = "object",
        properties,
        required = required ?? [],
        additionalProperties = false
    };
    private static object String(string? pattern = null) => pattern is null ? new { type = "string" } : (object)new { type = "string", pattern };
    private static object Integer(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    private static object Enum(params string[] values) => new { type = "string", @enum = values };
}
