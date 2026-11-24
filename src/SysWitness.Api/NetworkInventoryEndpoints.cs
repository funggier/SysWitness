using SysWitness.Storage;

namespace SysWitness.Api;

internal static class NetworkInventoryEndpoints
{
    public static void MapNetworkInventoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/network/inventory",
            (HttpContext context, SqliteEventStore store, CancellationToken ct) => ReadPageAsync(context, store, ct));
    }

    private static async Task<IResult> ReadPageAsync(HttpContext context, SqliteEventStore store, CancellationToken ct)
    {
        if (!EventQueryParser.TryParse(context.Request, out var query, out var error, allowCategory: false))
            return Results.BadRequest(new { error = "invalid_query", message = error });
        try { return Results.Ok(await store.GetNetworkInventoryAsync(query!, ct)); }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid_query", message = "Invalid query filters or cursor." });
        }
    }
}
