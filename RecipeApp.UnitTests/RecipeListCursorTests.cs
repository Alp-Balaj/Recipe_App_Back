using System.Buffers.Text;
using System.Text;
using RecipeApp.Application.Recipes;

namespace RecipeApp.UnitTests;

public class RecipeListCursorTests
{
    [Fact]
    public void EncodeThenTryDecode_RoundTripsCreatedAtAndId()
    {
        var original = new RecipeListCursor(
            new DateTime(2026, 7, 11, 18, 52, 25, 123, DateTimeKind.Utc).AddTicks(4567),
            Guid.NewGuid());

        var encoded = original.Encode();
        var decoded = RecipeListCursor.TryDecode(encoded, out var cursor);

        Assert.True(decoded);
        Assert.Equal(original, cursor);
        Assert.Equal(DateTimeKind.Utc, cursor!.CreatedAt.Kind);
    }

    [Fact]
    public void Encode_ProducesUrlSafeToken()
    {
        var encoded = new RecipeListCursor(DateTime.UtcNow, Guid.NewGuid()).Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData("!!!not-base64url!!!")]                          // invalid base64url characters
    [InlineData("aGVsbG8")]                                      // base64url("hello") — not JSON
    [InlineData("e30")]                                          // base64url("{}") — missing c and i
    [InlineData("")]                                             // empty token
    public void TryDecode_GarbageToken_ReturnsFalse(string token)
    {
        Assert.False(RecipeListCursor.TryDecode(token, out var cursor));
        Assert.Null(cursor);
    }

    [Theory]
    [InlineData("""{"c":"not-a-date","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    [InlineData("""{"c":"2026-07-11T10:00:00.0000000Z","i":"not-a-guid"}""")]
    [InlineData("""{"c":null,"i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    [InlineData("""{"c":"2026-07-11T10:00:00.0000000Z","i":null}""")]
    // Non-UTC timestamps are rejected: only server-generated "O"-format UTC ("Z") values
    // are legitimate, and Npgsql would refuse a non-UTC DateTime against timestamptz.
    [InlineData("""{"c":"2026-07-11T10:00:00.0000000","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    [InlineData("""{"c":"2026-07-11T10:00:00.0000000+02:00","i":"0b5f5c05-13b7-4a0b-9c1a-111111111111"}""")]
    public void TryDecode_ValidBase64UrlButInvalidPayload_ReturnsFalse(string payloadJson)
    {
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));

        Assert.False(RecipeListCursor.TryDecode(token, out var cursor));
        Assert.Null(cursor);
    }
}
