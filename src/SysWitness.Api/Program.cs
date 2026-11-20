using System.Net;
using System.Security.Cryptography;
using System.Text;
using SysWitness.Api;
using SysWitness.Core;
using SysWitness.Storage;

var token = Environment.GetEnvironmentVariable("SYSWITNESS_API_TOKEN");
if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
    throw new InvalidOperationException(
        "Set SYSWITNESS_API_TOKEN (at least 32 characters) before starting. No API is served without it.");

var portText = Environment.GetEnvironmentVariable("SYSWITNESS_PORT");
var port = 8769;
if (!string.IsNullOrWhiteSpace(portText) &&
    (!int.TryParse(portText, out port) || port is < 1 or > 65535))
    throw new InvalidOperationException("SYSWITNESS_PORT must be a valid TCP port (1-65535).");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var dataDirectory = Environment.GetEnvironmentVariable("SYSWITNESS_DATA_DIRECTORY");
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysWitness", "data");
builder.Services.AddSingleton(new SqliteEventStore(Path.Combine(dataDirectory, "events.sqlite")));
builder.Services.AddSingleton(new WindowsEventLogReader("System"));
builder.Services.AddSingleton<CollectionState>();
builder.Services.AddHostedService<SystemEventPollingService>();

var app = builder.Build();
await app.Services.GetRequiredService<SqliteEventStore>().InitializeAsync();

var expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/api/v1/health")
    {
        await next(context);
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-SysWitness-Token", out var presented) ||
        presented.Count != 1 ||
        !CryptographicOperations.FixedTimeEquals(expectedTokenHash,
            SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString()))))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next(context);
});

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok", apiVersion = "v1" }));
app.MapGet("/api/v1/monitoring/coverage",
    (CollectionState state) => Results.Ok(state.Snapshot()));
app.MapGet("/api/v1/events", async (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
{
    var value = context.Request.Query["limit"].ToString();
    var limit = 100;
    if (!string.IsNullOrWhiteSpace(value) && (!int.TryParse(value, out limit) || limit is < 1 or > 500))
        return Results.BadRequest(new { error = "invalid_limit", message = "limit must be 1..500" });
    return Results.Ok(await store.GetRecentAsync(limit, ct));
});
app.MapGet("/api/v1/events/{eventId}", async (string eventId, SqliteEventStore store, CancellationToken ct) =>
{
    var result = await store.GetByIdAsync(eventId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

// Intentionally no OS control, firewall, process action, or configuration endpoints.
await app.RunAsync();
