using System.Security.Cryptography;
using System.Text;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-21): the second factor itself — RFC 6238 time-based one-time passwords,
// HMAC-SHA1 over a 30-second step, six digits.
//
// WHY HAND-WRITTEN. This is eighty lines of arithmetic pinned by the RFC's own published
// test vectors (see TotpTests), and every authenticator app implements the same eighty
// lines. A package would be a supply-chain dependency in the authentication path for code
// whose correctness is decided by a table of expected values we would still have to assert.
//
// WHY SHA1, WHICH IS OTHERWISE FINISHED. HMAC-SHA1 is not SHA1-as-a-digest: the collision
// attacks that retired SHA1 say nothing about HMAC, and it is what Google Authenticator,
// Aegis, 1Password and every other scanner assumes when an otpauth:// URI omits `algorithm`.
// Choosing SHA256 here would produce a QR that silently yields wrong codes in most apps —
// a worse failure than the theoretical one it avoids, because it fails at enrolment for the
// user rather than in a lab.
public static class Totp
{
    /// <summary>Six digits, because that is what every authenticator app shows.</summary>
    public const int Digits = 6;

    /// <summary>The RFC's default, and what an otpauth:// URI means when it omits `period`.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many steps either side of "now" are accepted.
    ///
    /// One, which is a 90-second window in total. Zero would reject a correct code typed by
    /// a person whose phone clock is two seconds out, or who started typing at second 29 —
    /// the single most common way a correct setup reads as broken. More than one widens the
    /// guessing surface for nothing: nobody takes ninety seconds to copy six digits.
    /// </summary>
    public const int DriftSteps = 1;

    /// <summary>
    /// 20 bytes — the HMAC-SHA1 block-derived size the RFC's own vectors use, and what
    /// authenticator apps expect. Base32 because that is the only encoding an otpauth://
    /// URI's `secret` parameter is defined for.
    /// </summary>
    public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>The step number a moment falls in. Public because a spent code is remembered by step.</summary>
    public static long StepAt(DateTime utc) =>
        (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / Step.TotalSeconds);

    /// <summary>The six digits for one step of one secret.</summary>
    public static string Compute(byte[] secret, long step)
    {
        // Big-endian eight-byte counter, per the RFC.
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var mac = HMACSHA1.HashData(secret, counter);

        // Dynamic truncation: the low nibble of the last byte picks where to read four bytes
        // from, and the top bit is masked off so the result is a positive 31-bit integer.
        var offset = mac[^1] & 0x0F;
        var binary =
            ((mac[offset] & 0x7F) << 24)
            | ((mac[offset + 1] & 0xFF) << 16)
            | ((mac[offset + 2] & 0xFF) << 8)
            | (mac[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits)).ToString().PadLeft(Digits, '0');
    }

    /// <summary>
    /// Whether <paramref name="code"/> is valid for <paramref name="base32Secret"/> at
    /// <paramref name="nowUtc"/>, and if so WHICH step it belongs to.
    ///
    /// The step comes back because accepting a code is not the end of the story: an observed
    /// code stays valid for the rest of its window unless the account remembers that it was
    /// spent, so the caller stores this and refuses anything at or below it. Without that,
    /// someone reading a code over a shoulder has up to ninety seconds to use it too.
    /// </summary>
    public static bool TryMatch(string base32Secret, string code, DateTime nowUtc, out long matchedStep)
    {
        matchedStep = 0;

        var normalised = Normalise(code);
        if (normalised.Length != Digits || !normalised.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!Base32.TryDecode(base32Secret, out var secret))
        {
            return false;
        }

        var current = StepAt(nowUtc);
        for (var offset = -DriftSteps; offset <= DriftSteps; offset++)
        {
            var step = current + offset;
            // Fixed-time comparison. The window is tiny and the codes are short, but a
            // byte-by-byte early exit here is a free oracle and the fix costs one call.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Compute(secret, step)),
                    Encoding.ASCII.GetBytes(normalised)))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a submitted value even LOOKS like a TOTP code. Callers need this before they
    /// look anything up, because the backoff gate applies to codes and deliberately does not
    /// apply to recovery codes (ADR-0008), and the two are told apart by shape.
    /// </summary>
    public static bool LooksLikeCode(string value)
    {
        var normalised = Normalise(value);
        return normalised.Length == Digits && normalised.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The otpauth:// URI an authenticator scans. `algorithm`, `digits` and `period` are
    /// spelled out even though all three are this function's defaults — a reader comparing
    /// the QR against this file should not have to know the defaults to check it.
    /// </summary>
    public static string BuildUri(string issuer, string accountName, string base32Secret)
    {
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}";
        return $"otpauth://totp/{label}"
            + $"?secret={base32Secret}"
            + $"&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={(int)Step.TotalSeconds}";
    }

    // People read codes off a phone and type them with a space in the middle, because that
    // is how the phone shows them.
    private static string Normalise(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace(" ", string.Empty).Trim();
}

// RFC 4648 base32, upper-case, no padding on the way out and padding tolerated on the way in.
// Its own type rather than two private methods on Totp because the recovery codes use the
// same alphabet reasoning and a reader looking for "how is the secret encoded" should find
// one answer.
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] bytes)
    {
        var builder = new StringBuilder();
        int buffer = 0, bitsLeft = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                builder.Append(Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return builder.ToString();
    }

    public static bool TryDecode(string encoded, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var output = new List<byte>();
        int buffer = 0, bitsLeft = 0;

        foreach (var c in encoded.Trim().TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        bytes = [.. output];
        return bytes.Length > 0;
    }
}
