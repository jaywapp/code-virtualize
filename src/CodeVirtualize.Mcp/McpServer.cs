using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeVirtualize.Core.Contracts;
using CodeVirtualize.Core.Impact;
using CodeVirtualize.Core.Resolution;
using CodeVirtualize.Core.Search;
using CodeVirtualize.CSharp.References;

namespace CodeVirtualize.Mcp;

public static class McpServer
{
    public const string ProtocolVersion = "2025-06-18";
    public const string ServerVersion = "0.1.0";
    private const int MaxMessageCharacters = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        McpOptions options;
        try { options = McpOptions.Parse(args); }
        catch (ArgumentException exception) { await error.WriteLineAsync(exception.Message); return 2; }

        var writeLock = new SemaphoreSlim(1, 1);
        var calls = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var pending = new List<Task>();
        var initialized = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length > MaxMessageCharacters)
            {
                await WriteAsync(output, writeLock, Error(null, -32600, "Request exceeds the maximum message size."), cancellationToken);
                continue;
            }

            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(output, writeLock, Error(null, -32700, "Parse error."), cancellationToken);
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
                    !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                {
                    await WriteAsync(output, writeLock, Error(Id(root), -32600, "Invalid Request."), cancellationToken);
                    continue;
                }

                var method = methodElement.GetString()!;
                if (method == "notifications/initialized") { initialized = true; continue; }
                if (method == "notifications/cancelled") { Cancel(root, calls); continue; }
                var id = Id(root);
                if (id is null) continue;

                if (method == "initialize")
                {
                    var response = Initialize(root, id.Value);
                    await WriteAsync(output, writeLock, response, cancellationToken);
                    continue;
                }

                if (!initialized)
                {
                    await WriteAsync(output, writeLock, Error(id, -32002, "Server is not initialized."), cancellationToken);
                    continue;
                }

                if (method == "ping")
                {
                    await WriteAsync(output, writeLock, Result(id.Value, new { }), cancellationToken);
                    continue;
                }

                if (method == "tools/list")
                {
                    await WriteAsync(output, writeLock, Result(id.Value, new { tools = ToolDefinitions.All }), cancellationToken);
                    continue;
                }

                if (method != "tools/call")
                {
                    await WriteAsync(output, writeLock, Error(id, -32601, "Method not found."), cancellationToken);
                    continue;
                }

                var request = root.Clone();
                var key = IdKey(id.Value);
                var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                calls[key] = callCts;
                pending.Add(HandleToolCallAsync(request, id.Value, options, output, writeLock, callCts, calls, key));
            }
        }

        try { await Task.WhenAll(pending); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        foreach (var call in calls.Values) call.Dispose();
        return 0;
    }

    private static JsonElement Initialize(JsonElement request, JsonElement id)
    {
        if (!request.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("protocolVersion", out var requested) || requested.ValueKind != JsonValueKind.String)
            return Error(id, -32602, "initialize requires protocolVersion.");
        return Result(id, new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "code-virtualize", version = ServerVersion },
            instructions = "Use cv_find before cv_get. Treat partial, stale, and error statuses explicitly; use existing repository search/read tools when a call fails."
        });
    }

    private static async Task HandleToolCallAsync(JsonElement request, JsonElement id, McpOptions options, TextWriter output,
        SemaphoreSlim writeLock, CancellationTokenSource callCts, ConcurrentDictionary<string, CancellationTokenSource> calls, string key)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callCts.Token);
            var operation = Task.Run(() =>
            {
                var delayText = Environment.GetEnvironmentVariable("CODE_VIRTUALIZE_MCP_TEST_DELAY_MS");
                if (int.TryParse(delayText, out var delayMs) && delayMs > 0)
                    Task.Delay(delayMs, timeout.Token).GetAwaiter().GetResult();
                return Invoke(request, options, timeout.Token);
            }, CancellationToken.None);
            var completed = await Task.WhenAny(operation, Task.Delay(options.Timeout, callCts.Token));
            JsonElement response;
            if (callCts.IsCancellationRequested)
            {
                throw new OperationCanceledException(callCts.Token);
            }
            if (completed != operation)
            {
                timeout.Cancel();
                response = Result(id, ToolError("TIMEOUT", "The engine call exceeded its bounded timeout."));
            }
            else
            {
                response = Result(id, await operation);
            }
            await WriteAsync(output, writeLock, response, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(output, writeLock, Error(id, -32800, "Request cancelled."), CancellationToken.None);
        }
        catch (ArgumentException exception)
        {
            await WriteAsync(output, writeLock, Error(id, -32602, exception.Message), CancellationToken.None);
        }
        catch (Exception exception)
        {
            await WriteAsync(output, writeLock, Result(id, ToolError("ENGINE_FAILURE", "The engine call failed. Use existing repository search/read tools.")), CancellationToken.None);
            _ = exception;
        }
        finally
        {
            calls.TryRemove(key, out _);
            callCts.Dispose();
        }
    }

    private static object Invoke(JsonElement request, McpOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("tools/call requires a tool name.");
        var name = nameElement.GetString()!;
        var arguments = parameters.TryGetProperty("arguments", out var value) && value.ValueKind == JsonValueKind.Object ? value : EmptyObject();
        object result = name switch
        {
            "cv_find" => Find(options, arguments),
            "cv_get" => Get(options, arguments),
            "cv_impact" => Impact(options, arguments),
            _ => throw new ArgumentException($"Unknown tool: {name}")
        };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static object Find(McpOptions options, JsonElement args)
    {
        var query = new SymbolSearchQuery(String(args, "query"), String(args, "exact"), String(args, "qualifiedName"),
            String(args, "name"), String(args, "kind"), String(args, "accessibility"), String(args, "projectId"),
            String(args, "path"), Integer(args, "limit", 50, 1, 1000), String(args, "cursor"));
        var response = new SymbolSearchService().Search(options.StorePath, query);
        return ToolResult(ContractJson.Serialize(response), response, response.Status == ResponseStatus.Error);
    }

    private static object Get(McpOptions options, JsonElement args)
    {
        var symbolId = RequiredString(args, "symbolId");
        if (!Enum.TryParse<SourcePart>(String(args, "part") ?? "header", true, out var part)) throw new ArgumentException("part must be header, declaration, body, or context.");
        var response = new SourceResolver().Resolve(new(options.WorkspacePath, options.StorePath, symbolId, part,
            new(Integer(args, "maxBytes", 65536, 1, int.MaxValue), Integer(args, "maxLines", 400, 1, int.MaxValue)),
            String(args, "generationId"), String(args, "path"), NullableInteger(args, "declaration", 0, int.MaxValue),
            Integer(args, "contextLines", 2, 0, 100), null, String(args, "ifNoneMatch")));
        return ToolResult(ContractJson.Serialize(response), response, response.Status == ResponseStatus.Error);
    }

    private static object Impact(McpOptions options, JsonElement args)
    {
        if (!args.TryGetProperty("symbolIds", out var ids) || ids.ValueKind != JsonValueKind.Array) throw new ArgumentException("symbolIds is required.");
        var symbolIds = ids.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        var budget = new ImpactBudget(Integer(args, "maxDepth", 1, 0, 20), Integer(args, "maxResults", 200, 1, 10000),
            Integer(args, "maxSourceFiles", 500, 1, 100000), Integer(args, "pageSize", 100, 1, 10000));
        var response = new CSharpImpactService().Query(new(options.WorkspacePath, options.StorePath, symbolIds, budget,
            Boolean(args, "includeLexicalCandidates", true), String(args, "generationId"), String(args, "cursor")));
        var json = JsonSerializer.Serialize(response, JsonOptions);
        return ToolResult(json, response, response.Status == ResponseStatus.Error);
    }

    private static object ToolResult(string text, object structured, bool isError) => new
    {
        content = new[] { new { type = "text", text } },
        structuredContent = structured,
        isError
    };

    private static object ToolError(string code, string message) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { code, message, fallback = new { attempted = false, reason = "Use existing repository search/read tools." } }) } },
        structuredContent = new { code, message, fallback = new { attempted = false, reason = "Use existing repository search/read tools." } },
        isError = true
    };

    private static void Cancel(JsonElement request, ConcurrentDictionary<string, CancellationTokenSource> calls)
    {
        if (!request.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("requestId", out var id)) return;
        if (calls.TryGetValue(IdKey(id), out var cts)) cts.Cancel();
    }

    private static async Task WriteAsync(TextWriter output, SemaphoreSlim gate, JsonElement value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await output.WriteLineAsync(value.GetRawText()); await output.FlushAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    private static JsonElement Result(JsonElement id, object result) => JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, result }, JsonOptions);
    private static JsonElement Error(JsonElement? id, int code, string message) => JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOptions);
    private static JsonElement? Id(JsonElement root) => root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number ? id.Clone() : null;
    private static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? $"s:{id.GetString()}" : $"n:{id.GetRawText()}";
    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });
    private static string? String(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement args, string name) => String(args, name) ?? throw new ArgumentException($"{name} is required.");
    private static int Integer(JsonElement args, string name, int fallback, int min, int max) { if (!args.TryGetProperty(name, out var value)) return fallback; if (!value.TryGetInt32(out var result) || result < min || result > max) throw new ArgumentException($"{name} must be between {min} and {max}."); return result; }
    private static int? NullableInteger(JsonElement args, string name, int min, int max) => args.TryGetProperty(name, out _) ? Integer(args, name, 0, min, max) : null;
    private static bool Boolean(JsonElement args, string name, bool fallback) { if (!args.TryGetProperty(name, out var value)) return fallback; if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{name} must be boolean."); return value.GetBoolean(); }
    private static JsonSerializerOptions CreateJsonOptions() { var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never }; options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false)); return options; }
}
