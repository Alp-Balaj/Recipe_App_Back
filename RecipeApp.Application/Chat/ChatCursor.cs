using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeApp.Application.Chat;

// Opaque keyset-pagination cursor for the chat list endpoints (chat-ai plan, checkpoint 03).
// Same base64url-JSON convention as RecipeListCursor — {"t":"<timestamp ISO-8601 round-trip
// (O)>","i":"<Guid>"} — reused for both chat lists since both keyset on a (DateTime, Guid)
// pair: the conversation list on (UpdatedAt, Id), the message list on (CreatedAt, Id). The
// timestamp field is named neutrally ("Timestamp") because it plays both roles.
public sealed record ChatCursor(DateTime Timestamp, Guid Id)
{
    public string Encode()
    {
        var payload = new Payload(Timestamp.ToString("O"), Id.ToString());
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    public static bool TryDecode(string value, out ChatCursor? cursor)
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

        cursor = new ChatCursor(timestamp, id);
        return true;
    }

    private sealed record Payload(
        [property: JsonPropertyName("t")] string? T,
        [property: JsonPropertyName("i")] string? I);
}
