using SysWitness.Storage;

namespace SysWitness.Api;

internal static class ProcessActivityEndpoints
{
    public static void MapProcessActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/processes/{processGuid:guid}/activity",
            async (Guid processGuid, HttpContext context, SqliteEventStore store, CancellationToken ct) =>
            {
                if (!EventQueryParser.TryParse(context.Request, out var query, out var error,
                    allowCategory: false, allowComputer: true))
                    return Results.BadRequest(new { error = "invalid_query", message = error });

                var computer = context.Request.Query["computer"].ToString();
                try
                {
                    return Results.Ok(await store.GetProcessActivityAsync(query!, processGuid, computer, ct));
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = "invalid_query",
                        message = "A nonzero process GUID, recorded computer name and valid filters are required." });
                }
            });
    }
}
