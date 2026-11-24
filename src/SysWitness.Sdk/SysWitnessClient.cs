using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SysWitness.Sdk;

/// <summary>
/// A fixed read-only client for the local SysWitness API. There is deliberately no generic HTTP,
/// URL, path, shell, SQL, file, or host-control method. Does not own or configure collection.
/// </summary>
public sealed class SysWitnessClient : IDisposable
{
    private readonly HttpClient client;
    private readonly string token;
    private readonly int maximumResponseBytes;
    private readonly TimeSpan requestTimeout;

    public SysWitnessClient(Uri origin, string token, int maximumResponseBytes = 1_048_576,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != "http" || origin.Host != "127.0.0.1" ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("The API origin must be HTTP on 127.0.0.1 with no path, credentials, query or fragment.", nameof(origin));
        if (token is null || token.Length is < 32 or > 1024 || token.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("A 32..1024 character printable ASCII credential is required.", nameof(token));
        ReadValidation.Range(maximumResponseBytes, 256, 4_194_304, nameof(maximumResponseBytes));
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Timeout must be 1..120 seconds.");
        this.token = token;
        this.maximumResponseBytes = maximumResponseBytes;
        this.requestTimeout = timeout;
        client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            BaseAddress = origin,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync("health", [], cancellationToken);

    public Task<JsonElement> GetEventsAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetAsync("events", ReadValidation.Page(options, allowCategory: true), cancellationToken);

    public Task<JsonElement> GetEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        if (eventId is null || eventId.Length != 64 || !eventId.All(Uri.IsHexDigit))
            throw new ArgumentException("A 64 character hexadecimal evidence ID is required.", nameof(eventId));
        return GetAsync("events/" + eventId, [], cancellationToken);
    }

    public Task<JsonElement> GetCoverageAsync(string? channel = null, CancellationToken cancellationToken = default)
    {
        ReadValidation.Text(channel, nameof(channel), 256);
        return GetAsync("monitoring/coverage", channel is null ? [] : [new("channel", channel)], cancellationToken);
    }

    public Task<JsonElement> GetGapsAsync(string? channel = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        ReadValidation.Text(channel, nameof(channel), 256);
        ReadValidation.Range(limit, 1, 100, nameof(limit));
        List<KeyValuePair<string, string>> values = [];
        if (channel is not null) values.Add(new("channel", channel));
        values.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        return GetAsync("monitoring/gaps", values, cancellationToken);
    }

    public Task<JsonElement> GetStorageStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync("storage/status", [], cancellationToken);

    public Task<JsonElement> GetRetentionAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        ReadValidation.Range(limit, 1, 100, nameof(limit));
        return GetAsync("storage/retention", [new("limit", limit.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public Task<JsonElement> GetProcessEventsAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetAsync("processes/events", ReadValidation.Page(options, allowCategory: false), cancellationToken);

    public Task<JsonElement> GetProcessTimelineAsync(Guid processGuid, EventReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReadValidation.GuidValue(processGuid);
        return GetAsync($"processes/{processGuid:D}/timeline", ReadValidation.Page(options, allowCategory: false), cancellationToken);
    }

    public Task<JsonElement> GetProcessActivityAsync(Guid processGuid, string computer,
        EventReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ReadValidation.GuidValue(processGuid);
        ReadValidation.Text(computer, nameof(computer), 256, required: true);
        var values = ReadValidation.Page(options, allowCategory: false);
        values.Add(new("computer", computer));
        return GetAsync($"processes/{processGuid:D}/activity", values, cancellationToken);
    }

    public Task<JsonElement> GetProcessTreeAsync(Guid processGuid, string computer, int depth = 3,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ReadValidation.GuidValue(processGuid);
        ReadValidation.Text(computer, nameof(computer), 256, required: true);
        ReadValidation.Range(depth, 0, 8, nameof(depth));
        ReadValidation.Range(limit, 1, 256, nameof(limit));
        return GetAsync($"processes/{processGuid:D}/tree",
            [new("computer", computer), new("depth", depth.ToString(CultureInfo.InvariantCulture)),
                new("limit", limit.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public Task<JsonElement> GetConnectionsAsync(EventReadOptions? options = null, Guid? processGuid = null,
        CancellationToken cancellationToken = default) => GetNetworkAsync("connections", options, processGuid, cancellationToken);

    public Task<JsonElement> GetDnsAsync(EventReadOptions? options = null, Guid? processGuid = null,
        CancellationToken cancellationToken = default) => GetNetworkAsync("dns", options, processGuid, cancellationToken);

    public Task<JsonElement> GetNetworkInventoryAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetAsync("network/inventory", ReadValidation.Page(options, allowCategory: false), cancellationToken);

    public Task<JsonElement> GetStorageProjectionsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("storage/projections", [], cancellationToken);

    public Task<JsonElement> GetFileActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("files", options, cancellationToken);
    public Task<JsonElement> GetRegistryActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("registry", options, cancellationToken);
    public Task<JsonElement> GetAuthenticationActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("authentication", options, cancellationToken);
    public Task<JsonElement> GetServiceActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("services", options, cancellationToken);
    public Task<JsonElement> GetDriverActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("drivers", options, cancellationToken);
    public Task<JsonElement> GetTaskActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("tasks", options, cancellationToken);
    public Task<JsonElement> GetDeviceActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("devices", options, cancellationToken);
    public Task<JsonElement> GetModuleActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("modules", options, cancellationToken);
    public Task<JsonElement> GetProcessAccessActivityAsync(EventReadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetActivityAsync("process-access", options, cancellationToken);

    private Task<JsonElement> GetActivityAsync(string family, EventReadOptions? options, CancellationToken cancellationToken) =>
        GetAsync("activity/" + family, ReadValidation.Page(options, allowCategory: false), cancellationToken);

    private Task<JsonElement> GetNetworkAsync(string route, EventReadOptions? options, Guid? processGuid,
        CancellationToken cancellationToken)
    {
        var values = ReadValidation.Page(options, allowCategory: false);
        if (processGuid is { } guid)
        {
            ReadValidation.GuidValue(guid);
            values.Add(new("processGuid", guid.ToString("D")));
        }
        return GetAsync("network/" + route, values, cancellationToken);
    }

    private async Task<JsonElement> GetAsync(string route, IReadOnlyList<KeyValuePair<string, string>> values,
        CancellationToken cancellationToken)
    {
        var path = "/api/v1/" + route;
        if (values.Count != 0)
            path += "?" + string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-SysWitness-Token", token);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new SysWitnessApiException(status switch
                {
                    401 => "unauthorized", 403 => "permission_denied", 404 => "not_found",
                    429 => "rate_limited", _ => "backend_http_error"
                }, status);
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                throw new SysWitnessApiException("invalid_response_type", status);
            if (response.Content.Headers.ContentLength > maximumResponseBytes)
                throw new SysWitnessApiException("response_too_large", status);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await body.ReadAsync(chunk, timeout.Token);
                if (count == 0) break;
                if (buffer.Length + count > maximumResponseBytes)
                    throw new SysWitnessApiException("response_too_large", status);
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
                new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            ValidateObjectNames(document.RootElement);
            // Never allow an echoed transport credential to escape through evidence content.
            using var sanitized = new MemoryStream();
            using (var writer = new Utf8JsonWriter(sanitized)) WriteSanitized(document.RootElement, writer);
            using var safeDocument = JsonDocument.Parse(sanitized.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return safeDocument.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SysWitnessApiException("timeout");
        }
        catch (HttpRequestException) { throw new SysWitnessApiException("transport_error"); }
        catch (IOException) { throw new SysWitnessApiException("transport_error"); }
        catch (JsonException) { throw new SysWitnessApiException("invalid_json_response"); }
    }

    private static void ValidateObjectNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate response property.");
                ValidateObjectNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateObjectNames(item);
    }

    private void WriteSanitized(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name.Replace(token, "[REDACTED]", StringComparison.Ordinal));
                    WriteSanitized(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteSanitized(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString()!.Replace(token, "[REDACTED]", StringComparison.Ordinal));
                break;
            default: value.WriteTo(writer); break;
        }
    }

    public void Dispose() => client.Dispose();
}
