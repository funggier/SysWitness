using SysWitness.Storage;

namespace SysWitness.Api;

internal static class NetworkEventEndpoints
{
    public static void MapNetworkEventEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/network/connections",
            (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
                ReadPageAsync(context, store, dns: false, ct));

        app.MapGet("/api/v1/network/dns",
            (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
                ReadPageAsync(context, store, dns: true, ct));
    }

    private static async Task<IResult> ReadPageAsync(
        HttpContext context, SqliteEventStore store, bool dns, CancellationToken ct)
    {
        if (!EventQueryParser.TryParse(context.Request, out var query, out var error,
            allowCategory: false, allowProcessGuid: true))
            return Results.BadRequest(new { error = "invalid_query", message = error });

        Guid? processGuid = null;
        if (context.Request.Query.TryGetValue("processGuid", out var value))
        {
            if (!Guid.TryParseExact(value.ToString(), "D", out var guid) || guid == Guid.Empty)
                return Results.BadRequest(new { error = "invalid_query", message = "A nonzero processGuid is required." });
            processGuid = guid;
        }

        try
        {
            if (dns)
                return Results.Ok(await store.GetDnsQueriesAsync(query!, processGuid, ct));
            return Results.Ok(await store.GetNetworkConnectionsAsync(query!, processGuid, ct));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid_query", message = "Invalid query filters or cursor." });
        }
    }
}
