using System.Security.Cryptography;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-21): the recovery path that does not run through email.
//
// CONTEXT.md → Recovery code: "a single-use secret, issued at enrolment, that answers a
// challenge in the authenticator's place". Ten of them, shown once, hashed at rest.
//
// WHY A PLAIN SHA-256 AND NOT THE PASSWORD HASHER. Passwords are hashed slowly because
// people choose them out of a space small enough to enumerate. These are not chosen by
// anyone — each is 50 bits from the CSPRNG, so there is nothing to enumerate and slowing
// the comparison would buy nothing while making a sign-in noticeably worse. The digest is
// the same SecretTokens.Hash the emailed links and refresh tokens use, for the same reason:
// read access to this table must not be sign-in access to anybody's account.
public static class RecoveryCodes
{
    /// <summary>
    /// How many are issued at once. Ten is enough that a person who loses their phone can
    /// keep signing in while they re-enrol, and few enough to be printed on one line each.
    /// </summary>
    public const int Count = 10;

    /// <summary>
    /// Crockford's alphabet minus its remaining ambiguities: no I, L, O or U, so nothing on
    /// the page can be misread as a 1 or a 0. These are transcribed by hand off a printout
    /// at the worst moment of someone's week, which is exactly when confusable characters
    /// cost real access.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int GroupLength = 5;
    private const int Groups = 2;

    /// <summary>
    /// <paramref name="count"/> fresh codes in the form <c>ABCDE-FGHJK</c> — 10 characters
    /// from a 32-symbol alphabet, so 50 bits each. The dash is display only; it is stripped
    /// before hashing, so a user who omits it is not punished for it.
    /// </summary>
    public static IReadOnlyList<string> Generate(int count = Count)
    {
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var groups = new string[Groups];
            for (var g = 0; g < Groups; g++)
            {
                var chars = new char[GroupLength];
                for (var c = 0; c < GroupLength; c++)
                {
                    // RandomNumberGenerator.GetInt32 is rejection-sampled, so no character
                    // is likelier than another — a modulo over 32 would have been fine here
                    // but is the habit that is wrong the one time the alphabet is not a
                    // power of two.
                    chars[c] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                }
                groups[g] = new string(chars);
            }
            codes.Add(string.Join('-', groups));
        }

        return codes;
    }

    /// <summary>
    /// The form a code is hashed and compared in: upper-cased, with dashes and spaces gone.
    /// Both the issue path and the redeem path go through this, so a code typed
    /// <c>abcde fghjk</c> finds the row written for <c>ABCDE-FGHJK</c>.
    /// </summary>
    public static string Normalise(string code) =>
        string.IsNullOrEmpty(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);

    /// <summary>The digest stored for a code. Never the code itself.</summary>
    public static string Hash(string code) => SecretTokens.Hash(Normalise(code));

    /// <summary>
    /// Whether a submitted value could be a recovery code at all — ten characters, all from
    /// the alphabet. Used to tell a recovery code from a TOTP code before either is looked
    /// up, which is what lets the backoff gate apply to one and not the other (ADR-0008).
    /// </summary>
    public static bool LooksLikeCode(string value)
    {
        var normalised = Normalise(value);
        return normalised.Length == GroupLength * Groups && normalised.All(Alphabet.Contains);
    }
}
