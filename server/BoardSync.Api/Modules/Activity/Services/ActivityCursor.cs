using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace BoardSync.Api.Modules.Activity.Services;

/// <summary>
/// A position in the activity feed: the sort key of the last row a client has seen.
/// </summary>
/// <remarks>
/// Both components are required. The feed sorts by <see cref="OccurredAt"/> and breaks ties on
/// <see cref="Id"/> because entries written in one transaction share a timestamp to the microsecond;
/// a cursor carrying only the timestamp would skip or repeat those rows at exactly the boundary
/// where they are most likely to sit.
/// </remarks>
public readonly record struct ActivityCursor(DateTime OccurredAt, Guid Id)
{
    /// <summary>
    /// Encodes to base64url. Ticks rather than a formatted date because the value has to survive the
    /// round trip exactly — a cursor that loses sub-millisecond precision silently re-reads rows.
    /// </summary>
    public string Encode()
    {
        var raw = $"{OccurredAt.Ticks.ToString(CultureInfo.InvariantCulture)}:{Id:N}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Parses a cursor supplied by a caller. Returns false for anything malformed rather than
    /// throwing: the value arrives from the query string, so a bad one is a client mistake to be
    /// answered with the first page, not a 500.
    /// </summary>
    public static bool TryDecode(string? value, out ActivityCursor cursor)
    {
        cursor = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = raw.IndexOf(':');
        if (separator <= 0)
            return false;

        if (!long.TryParse(raw.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            return false;

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return false;

        if (!Guid.TryParseExact(raw.AsSpan(separator + 1), "N", out var id))
            return false;

        // The column is timestamptz and EF hands back UTC values; the kind has to be restored
        // explicitly or the comparison below comes out shifted by the server's offset.
        cursor = new ActivityCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}
