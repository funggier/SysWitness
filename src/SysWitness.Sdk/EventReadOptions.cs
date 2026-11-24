using System.Globalization;

namespace SysWitness.Sdk;

/// <summary>Named, bounded filters for retained evidence. This is not an expression or SQL interface.</summary>
public sealed record EventReadOptions(
    int Limit = 100,
    string? Cursor = null,
    string? Channel = null,
    string? Category = null,
    string? Provider = null,
    int? SourceEventId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

internal static class ReadValidation
{
    public static void Text(string? value, string parameter, int maximum, bool required = false)
    {
        if (value is null)
        {
            if (required) throw new ArgumentException($"A value is required for {parameter}.", parameter);
            return;
        }
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException($"Invalid {parameter}.", parameter);
    }

    public static void GuidValue(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A nonzero process GUID is required.", nameof(value));
    }

    public static void Range(int value, int minimum, int maximum, string parameter)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameter, $"Expected {minimum}..{maximum}.");
    }

    public static List<KeyValuePair<string, string>> Page(EventReadOptions? options, bool allowCategory)
    {
        var q = options ?? new();
        Range(q.Limit, 1, 500, nameof(q.Limit));
        Text(q.Cursor, nameof(q.Cursor), 4096);
        Text(q.Channel, nameof(q.Channel), 256);
        Text(q.Provider, nameof(q.Provider), 256);
        Text(q.Category, nameof(q.Category), 128);
        if (!allowCategory && q.Category is not null)
            throw new ArgumentException("Category is supported only for general event summaries.", nameof(q.Category));
        if (q.SourceEventId is { } eventId) Range(eventId, 0, 65535, nameof(q.SourceEventId));
        if (q.FromUtc is { } from && q.ToUtc is { } to && from >= to)
            throw new ArgumentException("FromUtc must be earlier than ToUtc.");

        var values = new List<KeyValuePair<string, string>>
            { new("limit", q.Limit.ToString(CultureInfo.InvariantCulture)) };
        void Add(string name, string? value) { if (value is not null) values.Add(new(name, value)); }
        Add("cursor", q.Cursor);
        Add("channel", q.Channel);
        Add("category", q.Category);
        Add("provider", q.Provider);
        Add("sourceEventId", q.SourceEventId?.ToString(CultureInfo.InvariantCulture));
        Add("fromUtc", q.FromUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add("toUtc", q.ToUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return values;
    }
}
