using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeApp.Application.Common;

// Opaque keyset-pagination cursor for the social-feed endpoints (social-feed plan, cp01–03).
// Same base64url-JSON convention as RecipeListCursor/ChatCursor — {"t":"<timestamp ISO-8601
// round-trip (O)>","i":"<Guid>"} — shared by every social list because they all keyset on a
// (DateTime, Guid) pair: comments on (CreatedAt, Id), saved recipes on (SavedAt, RecipeId),
// follow lists on (FollowedAt, other user's Id), the feed on (CreatedAt, Id). The two
// existing cursors stay untouched (their wire contracts are frozen); new lanes should use
// this one rather than minting a fourth.
public sealed record KeysetCursor(DateTime Timestamp, Guid Id)
{
    public string Encode()
    {
        var payload = new Payload(Timestamp.ToString("O"), Id.ToString());
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    public static bool TryDecode(string value, out KeysetCursor? cursor)
    {
        cursor = null;

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return false;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(bytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload?.T is null || payload.I is null)
        {
            return false;
        }

        // Only server-generated "O"-format UTC timestamps ("Z" suffix) decode to Kind=Utc;
        // anything else is garbage (and Npgsql rejects non-UTC DateTimes against timestamptz).
        if (!DateTime.TryParse(payload.T, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            || timestamp.Kind != DateTimeKind.Utc)
        {
            return false;
        }

        if (!Guid.TryParse(payload.I, out var id))
        {
            return false;
        }

        cursor = new KeysetCursor(timestamp, id);
        return true;
    }

    private sealed record Payload(
        [property: JsonPropertyName("t")] string? T,
        [property: JsonPropertyName("i")] string? I);
}
