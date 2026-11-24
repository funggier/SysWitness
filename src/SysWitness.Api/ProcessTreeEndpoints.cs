using System.Globalization;
using SysWitness.Storage;

namespace SysWitness.Api;

internal static class ProcessTreeEndpoints
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
        { "computer", "depth", "limit" };

    public static void MapProcessTreeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/processes/{processGuid:guid}/tree",
            async (Guid processGuid, HttpContext context, SqliteEventStore store, CancellationToken ct) =>
            {
                var fields = context.Request.Query;
                if (fields.Any(field => !Keys.Contains(field.Key) || field.Value.Count != 1))
                    return Results.BadRequest(new { error = "invalid_query" });
                var depth = 3;
                var limit = 100;
                if ((fields.TryGetValue("depth", out var d) &&
                        !int.TryParse(d.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out depth)) ||
                    (fields.TryGetValue("limit", out var n) &&
                        !int.TryParse(n.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit)))
                    return Results.BadRequest(new { error = "invalid_query" });
                try
                {
                    return Results.Ok(await store.GetProcessTreeAsync(processGuid,
                        fields["computer"].ToString(), depth, limit, ct));
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = "invalid_query",
                        message = "A nonzero GUID, recorded computer, depth 0..8 and node limit 1..256 are required." });
                }
            });
    }
}
