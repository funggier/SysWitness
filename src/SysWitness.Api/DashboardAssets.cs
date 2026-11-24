namespace SysWitness.Api;

/// <summary>Optional public static client; only fixed asset names can be served.</summary>
internal static class DashboardAssets
{
    private static readonly IReadOnlyDictionary<string, (string File, string Type)> Assets =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = ("index.html", "text/html; charset=utf-8"),
            ["/index.html"] = ("index.html", "text/html; charset=utf-8"),
            ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
            ["/model.js"] = ("model.js", "text/javascript; charset=utf-8"),
            ["/styles.css"] = ("styles.css", "text/css; charset=utf-8")
        };

    public static bool IsPublicPath(string? path) => path is not null && Assets.ContainsKey(path);

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        foreach (var entry in Assets)
        {
            var asset = entry.Value;
            app.MapGet(entry.Key, () =>
            {
                var root = Environment.GetEnvironmentVariable("SYSWITNESS_DASHBOARD_DIRECTORY");
                if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(AppContext.BaseDirectory, "dashboard");
                var path = Path.Combine(Path.GetFullPath(root, AppContext.BaseDirectory), asset.File);
                return File.Exists(path) ? Results.File(path, asset.Type) : Results.NotFound();
            });
        }
    }
}
