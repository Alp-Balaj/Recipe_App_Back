using System.Security.Cryptography;
using System.Text;

namespace RecipeApp.Infrastructure.Auth;

// Accounts: the two primitives behind every store-the-digest-not-the-secret table here —
// emailed links (KAN-19, AccountToken) and refresh tokens (KAN-20, UserSession).
//
// Lifted out of AccountRecoveryService when the second caller arrived, rather than copied.
// Two independently maintained implementations of "how we generate and digest a secret" is
// exactly the duplication that ends with one of them quietly weakened.
internal static class SecretTokens
{
    /// <summary>
    /// 32 bytes from the CSPRNG, URL-safe base64 — long enough that guessing is not a
    /// strategy, and safe in a mail link and a cookie value alike without escaping.
    /// </summary>
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256, hex-encoded. What gets stored; the plaintext never is.</summary>
    public static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
