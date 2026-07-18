using System.Buffers.Text;
using System.Text;
using RecipeApp.Application.Common;

namespace RecipeApp.UnitTests;

// Mirrors RecipeListCursorTests: KeysetCursor (social-feed cp1) shares the base64url-JSON
// convention ({"t","i"} payload, same shape as ChatCursor) and must reject the same garbage.
public class KeysetCursorTests
{
    [Fact]
    public void EncodeThenTryDecode_RoundTripsTimestampAndId()
    {
        var original = new KeysetCursor(
            new DateTime(2026, 7, 18, 9, 30, 12, 456, DateTimeKind.Utc).AddTicks(7890),
            Guid.NewGuid());

        var decoded = KeysetCursor.TryDecode(original.Encode(), out var cursor);

        Assert.True(decoded);
        Assert.Equal(original, cursor);
        Assert.Equal(DateTimeKind.Utc, cursor!.Timestamp.Kind);
    }

    [Fact]
    public void Encode_ProducesUrlSafeToken()
    {
        var encoded = new KeysetCursor(DateTime.UtcNow, Guid.NewGuid()).Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData("!!!not-base64url!!!")]                          // invalid base64url characters
    [InlineData("aGVsbG8")]                                      // base64url("hello") — not JSON
    [InlineData("e30")]                                          // base64url("{}") — missing t and i
    [InlineData("")]                                             // empty token
    public void TryDecode_GarbageToken_ReturnsFalse(string token)
    {
        Assert.False(KeysetCursor.TryDecode(token, out var cursor));
        Assert.Null(cursor);
    }

    [Theory]
    [InlineData("""{"t":"not-a-date","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    [InlineData("""{"t":"2026-07-18T10:00:00.0000000Z","i":"not-a-guid"}""")]
    [InlineData("""{"t":null,"i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    [InlineData("""{"t":"2026-07-18T10:00:00.0000000Z","i":null}""")]
    // Non-UTC timestamps are rejected: only server-generated "O"-format UTC ("Z") values
    // are legitimate, and Npgsql would refuse a non-UTC DateTime against timestamptz.
    [InlineData("""{"t":"2026-07-18T10:00:00.0000000","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    [InlineData("""{"t":"2026-07-18T10:00:00.0000000+02:00","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    // A RecipeListCursor-shaped payload ({"c","i"}) is NOT valid for this cursor.
    [InlineData("""{"c":"2026-07-18T10:00:00.0000000Z","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    public void TryDecode_ValidBase64UrlButInvalidPayload_ReturnsFalse(string payloadJson)
    {
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));

        Assert.False(KeysetCursor.TryDecode(token, out var cursor));
        Assert.Null(cursor);
    }
}
