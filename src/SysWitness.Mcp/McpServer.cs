using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SysWitness.Sdk;

namespace SysWitness.Mcp;

/// <summary>Only the MCP 2025-11-25 initialize lifecycle and named synchronous-result read tools.</summary>
internal sealed class McpServer(SysWitnessClient client)
{
    private const string ProtocolVersion = "2025-11-25";
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });
    private readonly SemaphoreSlim output = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveCall> active = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private SessionState state;
    private double readTokens = 20;
    private long lastRefill = Stopwatch.GetTimestamp();
    private enum SessionState { New, AwaitingInitialized, Ready }
    private sealed class ActiveCall : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation;
        private bool disposed;
        public CancellationToken Token { get; }
        public ActiveCall(CancellationToken token)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = cancellation.Token;
        }
        public Task Completion { get; set; } = Task.CompletedTask;
        public void Cancel() { lock (gate) { if (!disposed) cancellation.Cancel(); } }
        public void Dispose() { lock (gate) { if (!disposed) { disposed = true; cancellation.Dispose(); } } }
    }

    public async Task RunAsync(Stream input)
    {
        try
        {
            var reader = new BoundedLineReader(input, 65_536);
            while (await reader.ReadAsync(lifetime.Token) is { } frame)
            {
                if (frame.InvalidFrame)
                {
                    await Error(null, -32600, "Input frame exceeds the limit or is incomplete.");
                    continue;
                }
                if (!await Handle(frame.Data)) break;
            }
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(active.Values.Select(x => x.Completion));
            lifetime.Dispose();
            output.Dispose();
        }
    }

    private async Task<bool> Handle(byte[] data)
    {
        JsonElement? id = null;
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions
                { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root))
            {
                await Error(null, -32600, "Expected one JSON-RPC object without duplicate properties.");
                return true;
            }
            if (root.TryGetProperty("id", out var candidate))
            {
                if (!ValidId(candidate))
                {
                    await Error(null, -32600, "Request ID must be a bounded string or integer.");
                    return true;
                }
                id = candidate.Clone();
            }
            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString() != "2.0" || !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String || methodElement.GetString() is not { Length: > 0 and <= 128 } method)
            {
                await Error(id, -32600, "Invalid JSON-RPC request.");
                return true;
            }
            var parameters = root.TryGetProperty("params", out var value) ? value : EmptyArguments;
            if (id is null)
            {
                // Notifications are fire-and-forget, including unsupported or malformed notifications.
                if (parameters.ValueKind == JsonValueKind.Object)
                {
                    if (method == "notifications/initialized" && state == SessionState.AwaitingInitialized)
                        state = SessionState.Ready;
                    else if (method == "notifications/cancelled" &&
                        parameters.TryGetProperty("requestId", out var cancelled) && ValidId(cancelled) &&
                        active.TryGetValue(IdKey(cancelled), out var call))
                        call.Cancel();
                }
                return true;
            }
            var key = IdKey(id.Value);
            if (!seenIds.Add(key))
            {
                await Error(id, -32600, "Request ID was already used in this session.");
                return true;
            }
            if (seenIds.Count > 100_000)
            {
                await Error(id, -32603, "Session request budget exhausted; restart the server.");
                return false;
            }
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                await Error(id, -32602, "Request params must be an object.");
                return true;
            }
            if (method == "initialize")
            {
                if (state != SessionState.New || !ValidInitialize(parameters))
                {
                    await Error(id, -32602, "Invalid initialize request or session is already initialized.");
                    return true;
                }
                await Result(id.Value, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "SysWitness.Mcp", version = "0.1.0" },
                    instructions = "Read-only retained evidence. Check coverage and preserve provenance, quality and limitations. Empty evidence does not prove no activity."
                });
                state = SessionState.AwaitingInitialized;
                return true;
            }
            if (method == "ping")
            {
                if (!ValidParams(parameters, "_meta")) await Error(id, -32602, "Invalid ping params.");
                else await Result(id.Value, new { });
                return true;
            }
            if (method is not ("tools/list" or "tools/call"))
            {
                await Error(id, -32601, "Method not found. This server supports MCP 2025-11-25 with initialize.");
                return true;
            }
            if (state != SessionState.Ready)
            {
                await Error(id, -32600, "Complete initialize and notifications/initialized first.");
                return true;
            }
            if (method == "tools/list")
            {
                if (!ValidParams(parameters, "_meta"))
                    await Error(id, -32602, "The fixed catalog does not accept pagination cursors or filters.");
                else await Result(id.Value, new { tools = ToolCatalog.Tools.Values.Select(x => x.Describe()).ToArray() });
                return true;
            }
            if (!ValidParams(parameters, "name", "arguments", "_meta") ||
                !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                nameElement.GetString() is not { Length: > 0 and <= 128 } name ||
                !ToolCatalog.Tools.TryGetValue(name, out var tool))
            {
                await Error(id, -32602, "Invalid tools/call request or unknown tool.");
                return true;
            }
            var arguments = parameters.TryGetProperty("arguments", out var args) ? args : EmptyArguments;
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                await Error(id, -32602, "Tool arguments must be an object.");
                return true;
            }
            if (!ToolArguments.Validate(tool, arguments, out var validationMessage))
            {
                await ToolError(id.Value, "invalid_arguments", validationMessage);
                return true;
            }
            if (!TakeReadToken())
            {
                await ToolError(id.Value, "rate_limited", "Read rate exceeded; retry later.");
                return true;
            }
            if (active.Count >= 4)
            {
                await ToolError(id.Value, "busy", "At most four backend reads may be in progress.");
                return true;
            }
            var pending = new ActiveCall(lifetime.Token);
            if (!active.TryAdd(key, pending)) { pending.Dispose(); return true; }
            pending.Completion = Execute(id.Value, key, pending, name, arguments.Clone());
            return true;
        }
        catch (JsonException)
        {
            await Error(id, -32700, "Invalid JSON or maximum JSON depth exceeded.");
            return true;
        }
    }

    private async Task Execute(JsonElement id, string key, ActiveCall pending, string name, JsonElement arguments)
    {
        try
        {
            var data = await ToolArguments.Invoke(client, name, arguments, pending.Token);
            await Result(id, new { content = new[] { new { type = "text", text = data.GetRawText() } }, isError = false },
                pending.Token);
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested) { }
        catch (SysWitnessApiException ex)
        {
            await ToolError(id, ex.ErrorCode, "Backend read failed; inspect HTTP status and local coverage.", ex.HttpStatus,
                pending.Token);
        }
        catch (ArgumentException)
        {
            await ToolError(id, "invalid_arguments", "Invalid named read arguments.", cancellationToken: pending.Token);
        }
        catch (Exception)
        {
            await ToolError(id, "internal_error", "Read could not be completed.", cancellationToken: pending.Token);
        }
        finally
        {
            active.TryRemove(key, out _);
            pending.Dispose();
        }
    }

    private bool TakeReadToken()
    {
        var now = Stopwatch.GetTimestamp();
        readTokens = Math.Min(20, readTokens + Stopwatch.GetElapsedTime(lastRefill, now).TotalSeconds * 10);
        lastRefill = now;
        if (readTokens < 1) return false;
        readTokens--;
        return true;
    }

    private Task ToolError(JsonElement id, string errorCode, string message, int? httpStatus = null,
        CancellationToken cancellationToken = default) =>
        Result(id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { errorCode, message, httpStatus }) } },
            isError = true
        }, cancellationToken);

    private Task Error(JsonElement? id, int code, string message) =>
        Write(new { jsonrpc = "2.0", id, error = new { code, message } });

    private Task Result(JsonElement id, object result, CancellationToken cancellationToken = default) =>
        Write(new { jsonrpc = "2.0", id, result }, cancellationToken);

    private async Task Write(object message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message);
        await output.WaitAsync();
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Console.Out.WriteLineAsync(json);
                await Console.Out.FlushAsync();
            }
        }
        finally { output.Release(); }
    }

    private static bool ValidInitialize(JsonElement p) =>
        ValidParams(p, "protocolVersion", "capabilities", "clientInfo", "_meta") &&
        p.TryGetProperty("protocolVersion", out var v) && Text(v, 64) &&
        p.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty("clientInfo", out var info) && info.ValueKind == JsonValueKind.Object &&
        info.TryGetProperty("name", out var name) && Text(name, 128) &&
        info.TryGetProperty("version", out var version) && Text(version, 128);

    private static bool Text(JsonElement value, int maximum) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text &&
        text.Length <= maximum && !text.Any(char.IsControl);

    private static bool ValidParams(JsonElement parameters, params string[] allowed) =>
        parameters.EnumerateObject().All(x => allowed.Contains(x.Name, StringComparer.Ordinal)) &&
        (!parameters.TryGetProperty("_meta", out var meta) || meta.ValueKind == JsonValueKind.Object);

    private static bool ValidId(JsonElement value) =>
        (value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 128 &&
            !value.GetString()!.Any(char.IsControl)) ||
        (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _));

    private static string IdKey(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetInt64();

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item)) return true;
        return false;
    }
}
