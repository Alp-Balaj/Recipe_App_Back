using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeApp.Application.Recipes;

// Opaque keyset-pagination cursor for GET /recipes (recipe-management plan, Decisions §1):
// base64url of {"c":"<CreatedAt ISO-8601 round-trip (O) format>","i":"<Guid>"}. This
// encoding is also the social-features feed convention — evolve both together or not at all.
public sealed record RecipeListCursor(DateTime CreatedAt, Guid Id)
{
    public string Encode()
    {
        var payload = new Payload(CreatedAt.ToString("O"), Id.ToString());
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    public static bool TryDecode(string value, out RecipeListCursor? cursor)
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

        if (payload?.C is null || payload.I is null)
        {
            return false;
        }

        // Only server-generated "O"-format UTC timestamps ("Z" suffix) decode to
        // Kind=Utc; anything else is garbage (and Npgsql rejects non-UTC DateTimes
        // against timestamptz anyway).
        if (!DateTime.TryParse(payload.C, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt)
            || createdAt.Kind != DateTimeKind.Utc)
        {
            return false;
        }

        if (!Guid.TryParse(payload.I, out var id))
        {
            return false;
        }

        cursor = new RecipeListCursor(createdAt, id);
        return true;
    }

    private sealed record Payload(
        [property: JsonPropertyName("c")] string? C,
        [property: JsonPropertyName("i")] string? I);
}
