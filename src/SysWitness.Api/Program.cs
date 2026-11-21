using System.Net;
using SysWitness.Api;
using SysWitness.Core;
using SysWitness.Security;
using SysWitness.Storage;

var clients = ReadOnlyClientRegistry.Load(
    Environment.GetEnvironmentVariable("SYSWITNESS_API_TOKEN"),
    Environment.GetEnvironmentVariable("SYSWITNESS_CLIENT_KEYS_FILE"));

var portText = Environment.GetEnvironmentVariable("SYSWITNESS_PORT");
var port = 8769;
if (!string.IsNullOrWhiteSpace(portText) &&
    (!int.TryParse(portText, out port) || port is < 1 or > 65535))
    throw new InvalidOperationException("SYSWITNESS_PORT must be a valid TCP port (1-65535).");

var configuredChannels = WindowsEventLogChannels.ParseEnabled(
    Environment.GetEnvironmentVariable("SYSWITNESS_EVENT_CHANNELS"));

var processMode = ProcessCollectorCoverage.ParseMode(
    Environment.GetEnvironmentVariable("SYSWITNESS_PROCESS_MODE"));
var storagePolicy = StoragePolicy.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var dataDirectory = Environment.GetEnvironmentVariable("SYSWITNESS_DATA_DIRECTORY");
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton(new SqliteEventStore(
    Path.Combine(dataDirectory, "events.sqlite"), storagePolicy));
builder.Services.AddSingleton(new CollectionState(configuredChannels, processMode));
if (processMode != ProcessCollectionMode.Off)
{
    builder.Services.AddHostedService(sp => new WindowsProcessCollectionService(processMode,
        sp.GetRequiredService<SqliteEventStore>(), sp.GetRequiredService<CollectionState>(),
        sp.GetRequiredService<ILogger<WindowsProcessCollectionService>>()));
}
builder.Services.AddHostedService<StorageMaintenanceService>();
foreach (var channel in configuredChannels)
{
    var sourceChannel = channel;
    builder.Services.AddSingleton<IHostedService>(sp => new WindowsChannelPollingService(
        new WindowsEventLogReader(sourceChannel),
        sp.GetRequiredService<SqliteEventStore>(),
        sp.GetRequiredService<CollectionState>(),
        sp.GetRequiredService<ILogger<WindowsChannelPollingService>>()));
}

var app = builder.Build();
await app.Services.GetRequiredService<SqliteEventStore>().InitializeAsync();

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";

    // An HTTP request on loopback can still arrive with an attacker-controlled Host header.
    // Reject non-loopback host names to reduce DNS-rebinding exposure.
    if (!string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (context.Request.Path == "/api/v1/health" &&
        HttpMethods.IsGet(context.Request.Method))
    {
        await next(context);
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-SysWitness-Token", out var presented) ||
        presented.Count != 1 ||
        !clients.TryAuthenticate(presented.ToString(), out var client))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var scope = ReadOnlyClientRegistry.RequiredScope(context.Request.Path.Value ?? "");
    // No non-read method or unknown API path inherits permissions.
    if (scope is null || !HttpMethods.IsGet(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!client!.CanRead(scope))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok", apiVersion = "v1" }));
app.MapGet("/api/v1/storage/status",
    async (SqliteEventStore store, CancellationToken ct) =>
        Results.Ok(await store.GetStorageStatusAsync(ct)));
app.MapGet("/api/v1/storage/retention",
    async (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
    {
        if (context.Request.Query.Count > 1 ||
            context.Request.Query.Keys.Any(key => key != "limit") ||
            context.Request.Query.Any(field => field.Value.Count != 1))
            return Results.BadRequest(new { error = "invalid_query" });

        var limit = 50;
        if (context.Request.Query.TryGetValue("limit", out var value) &&
            (!int.TryParse(value.ToString(), out limit) || limit is < 1 or > 100))
            return Results.BadRequest(new { error = "invalid_limit" });
        return Results.Ok(await store.GetRetentionHistoryAsync(limit, ct));
    });
app.MapGet("/api/v1/monitoring/coverage", (HttpContext context, CollectionState state) =>
{
    if (context.Request.Query.Count == 0)
        return Results.Ok(state.Snapshot());

    if (context.Request.Query.Count != 1 ||
        !context.Request.Query.TryGetValue("channel", out var names) || names.Count != 1 ||
        string.IsNullOrWhiteSpace(names[0]))
        return Results.BadRequest(new { error = "invalid_query" });

    var source = state.ForChannel(names[0]!);
    return source is null
        ? Results.NotFound(new { error = "channel_not_configured" })
        : Results.Ok(source);
});
app.MapGet("/api/v1/monitoring/gaps",
    async (HttpContext context, CollectionState state, SqliteEventStore store, CancellationToken ct) =>
{
    if (context.Request.Query.Keys.Any(key => key is not ("channel" or "limit")) ||
        context.Request.Query.Any(entry => entry.Value.Count != 1))
        return Results.BadRequest(new { error = "invalid_query" });

    var requestedChannel = context.Request.Query.TryGetValue("channel", out var value)
        ? value.ToString() : state.IsConfigured("System") ? "System" : state.Channels[0];
    var channel = state.ForChannel(requestedChannel)?.Channel;
    if (channel is null)
        return Results.NotFound(new { error = "channel_not_configured" });

    var limit = 50;
    if (context.Request.Query.TryGetValue("limit", out var limitValue) &&
        (!int.TryParse(limitValue.ToString(), out limit) || limit is < 1 or > 100))
        return Results.BadRequest(new { error = "invalid_limit" });

    return Results.Ok(await store.GetGapsAsync(channel, limit, ct));
});
app.MapGet("/api/v1/events", async (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
{
    if (!EventQueryParser.TryParse(context.Request, out var query, out var error))
        return Results.BadRequest(new { error = "invalid_query", message = error });
    try
    {
        return Results.Ok(await store.GetPageAsync(query!, ct));
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_query", message = "Invalid query filters or cursor." });
    }
});
app.MapGet("/api/v1/events/{eventId}", async (string eventId, SqliteEventStore store, CancellationToken ct) =>
{
    var result = await store.GetByIdAsync(eventId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

// Intentionally no OS control, firewall, process action, or configuration endpoints.
app.MapProcessEventEndpoints();
await app.RunAsync();
