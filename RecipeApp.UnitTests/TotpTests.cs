using System.Text;
using RecipeApp.Infrastructure.Auth;

namespace RecipeApp.UnitTests;

// Accounts (KAN-21): the second factor's arithmetic.
//
// The first class of tests is the only reason writing TOTP by hand is defensible: RFC 6238
// publishes expected values for a known secret at known moments, so "does this agree with
// every authenticator app in the world" is a fact this file checks rather than a hope.
public class TotpTests
{
    // The RFC's SHA1 test seed: the ASCII string "12345678901234567890".
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // Unix seconds → the six digits the RFC's appendix B tabulates (its table shows eight
    // digits; six-digit TOTP is the same number truncated to its last six, which is what
    // every authenticator shows).
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Compute_agrees_with_the_RFC_6238_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTime.UnixEpoch.AddSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Compute(RfcSecret, step));
    }

    [Fact]
    public void Base32_round_trips_and_matches_the_known_encoding_of_the_RFC_seed()
    {
        var encoded = Base32.Encode(RfcSecret);

        // Not an arbitrary round-trip: this is the string an authenticator app is given for
        // the RFC's seed, so it pins the ALPHABET and the bit order, not just self-consistency.
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", encoded);

        Assert.True(Base32.TryDecode(encoded, out var decoded));
        Assert.Equal(RfcSecret, decoded);
    }

    [Fact]
    public void Base32_tolerates_padding_and_lower_case_but_refuses_a_foreign_character()
    {
        Assert.True(Base32.TryDecode("gezdgnbvgy3tqojq====", out var lower));
        Assert.True(Base32.TryDecode("GEZDGNBVGY3TQOJQ", out var upper));
        Assert.Equal(upper, lower);

        // '1' and '0' are deliberately not in the alphabet, so a hand-typed secret that
        // confused them for I and O must fail loudly rather than decode to something else.
        Assert.False(Base32.TryDecode("GEZDGNBVGY3TQOJ1", out _));
    }

    [Fact]
    public void A_code_is_accepted_one_step_either_side_of_now_and_nowhere_further()
    {
        var secret = Base32.Encode(RfcSecret);
        var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var step = Totp.StepAt(now);

        foreach (var offset in new[] { -1, 0, 1 })
        {
            Assert.True(
                Totp.TryMatch(secret, Totp.Compute(RfcSecret, step + offset), now, out var matched),
                $"offset {offset} should be inside the drift window");
            Assert.Equal(step + offset, matched);
        }

        foreach (var offset in new[] { -2, 2 })
        {
            Assert.False(
                Totp.TryMatch(secret, Totp.Compute(RfcSecret, step + offset), now, out _),
                $"offset {offset} is outside the drift window and must be refused");
        }
    }

    [Fact]
    public void A_code_typed_the_way_a_phone_shows_it_is_accepted()
    {
        var secret = Base32.Encode(RfcSecret);
        var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var code = Totp.Compute(RfcSecret, Totp.StepAt(now));

        // Authenticators render "123 456", and people type what they see.
        Assert.True(Totp.TryMatch(secret, $"{code[..3]} {code[3..]}", now, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void A_value_that_is_not_six_digits_is_never_a_code(string value)
    {
        Assert.False(Totp.LooksLikeCode(value));
        Assert.False(Totp.TryMatch(Base32.Encode(RfcSecret), value, DateTime.UtcNow, out _));
    }

    [Fact]
    public void A_recovery_code_does_not_look_like_a_TOTP_code()
    {
        // The distinction the backoff gate rests on (ADR-0008): recovery codes must stay
        // usable while backoff is running, and shape is how the two are told apart.
        Assert.False(Totp.LooksLikeCode(RecoveryCodes.Generate(1)[0]));
    }

    [Fact]
    public void Two_generated_secrets_differ_and_decode()
    {
        var a = Totp.GenerateSecret();
        var b = Totp.GenerateSecret();

        Assert.NotEqual(a, b);
        Assert.True(Base32.TryDecode(a, out var bytes));
        Assert.Equal(20, bytes.Length);
    }

    [Fact]
    public void The_otpauth_uri_carries_the_secret_the_issuer_and_the_explicit_parameters()
    {
        var uri = Totp.BuildUri("What are we cooking?", "cook@example.com", "GEZDGNBVGY3TQOJQ");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=GEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("algorithm=SHA1", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
        // The label and the issuer parameter both have to survive a space and a question mark.
        Assert.Contains("cook%40example.com", uri);
        Assert.DoesNotContain(" ", uri);
    }
}
