using SysWitness.Storage;

namespace SysWitness.Api;

internal static class ProcessEventEndpoints
{
    public static void MapProcessEventEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/processes/events",
            (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
                ReadPageAsync(context, store, null, ct));

        app.MapGet("/api/v1/processes/{processGuid:guid}/timeline",
            async (Guid processGuid, HttpContext context, SqliteEventStore store, CancellationToken ct) =>
            {
                if (processGuid == Guid.Empty)
                    return Results.BadRequest(new { error = "invalid_process_guid" });
                return await ReadPageAsync(context, store, processGuid, ct);
            });
    }

    private static async Task<IResult> ReadPageAsync(
        HttpContext context, SqliteEventStore store, Guid? processGuid, CancellationToken ct)
    {
        if (!EventQueryParser.TryParse(context.Request, out var query, out var error, processOnly: true))
            return Results.BadRequest(new { error = "invalid_query", message = error });

        try
        {
            return Results.Ok(await store.GetProcessEventsAsync(query!, processGuid, ct));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid_query", message = "Invalid query filters or cursor." });
        }
    }
}
