using System.Globalization;
using System.Text.RegularExpressions;
using SysWitness.Contracts;

namespace SysWitness.Api;

/// <summary>Strictly accepts known, bounded GET filters. Never accepts SQL or executable expressions.</summary>
internal static class EventQueryParser
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "limit", "cursor", "channel", "category", "provider", "sourceEventId", "fromUtc", "toUtc"
    };

    public static bool TryParse(HttpRequest request, out EventQuery? query, out string error,
        bool allowCategory = true, bool allowProcessGuid = false)
    {
        query = null;
        error = "";
        foreach (var field in request.Query)
        {
            var supported = AllowedKeys.Contains(field.Key) || (allowProcessGuid &&
                field.Key.Equals("processGuid", StringComparison.OrdinalIgnoreCase));
            if (!supported || field.Value.Count != 1 ||
                (!allowCategory && field.Key.Equals("category", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Unsupported or repeated query parameter.";
                return false;
            }
        }

        static string? Read(HttpRequest req, string key)
        {
            var value = req.Query[key].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        var limit = 100;
        var limitText = Read(request, "limit");
        if (limitText is not null &&
            (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
             || limit is < 1 or > 500))
        {
            error = "limit must be 1..500.";
            return false;
        }

        int? sourceEventId = null;
        if (Read(request, "sourceEventId") is { } eventIdText)
        {
            if (!int.TryParse(eventIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id is < 0 or > 65535)
            {
                error = "sourceEventId must be 0..65535.";
                return false;
            }
            sourceEventId = id;
        }

        static bool ParseUtc(string? input, out DateTimeOffset? utc)
        {
            utc = null;
            if (input is null) return true;
            if (!DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var value))
                return false;
            // Reject date-only and timezone-less values. A '-' in the date is not a UTC offset.
            if (!input.Contains('T', StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(input, @"(?:Z|[+-]\d{2}:\d{2})\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;
            utc = value.ToUniversalTime();
            return true;
        }

        if (!ParseUtc(Read(request, "fromUtc"), out var from) ||
            !ParseUtc(Read(request, "toUtc"), out var to) ||
            (from is not null && to is not null && from >= to))
        {
            error = "fromUtc/toUtc must be timezone-aware timestamps with fromUtc < toUtc.";
            return false;
        }

        query = new EventQuery(limit, Read(request, "cursor"), Read(request, "channel"),
            Read(request, "category"), Read(request, "provider"), sourceEventId, from, to);
        return true;
    }
}
