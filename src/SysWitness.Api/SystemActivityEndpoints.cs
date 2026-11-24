using SysWitness.Core;
using SysWitness.Storage;

namespace SysWitness.Api;

internal static class SystemActivityEndpoints
{
    public static void MapSystemActivityEndpoints(this WebApplication app)
    {
        foreach (var kind in WindowsSystemActivityDecoder.Kinds)
        {
            // Fixed routes also allow the central scope registry to fail closed for unknown kinds.
            app.MapGet("/api/v1/activity/" + kind,
                async (HttpContext context, SqliteEventStore store, CancellationToken ct) =>
                {
                    if (!EventQueryParser.TryParse(context.Request, out var query, out var error,
                        allowCategory: false))
                        return Results.BadRequest(new { error = "invalid_query", message = error });
                    try
                    {
                        return Results.Ok(await store.GetSystemActivityAsync(query!, kind, ct));
                    }
                    catch (ArgumentException)
                    {
                        return Results.BadRequest(new { error = "invalid_query",
                            message = "Use bounded source, channel, provider and timezone-aware time filters." });
                    }
                });
        }
    }
}
