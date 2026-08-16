using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.Entities;

// Accounts (KAN-19): the single-use secret behind an emailed link — one row per issued
// link, for either purpose.
//
// The plaintext token NEVER reaches this table. What is stored is a SHA-256 digest of it,
// and the plaintext exists only in the message that was sent, so read access to this table
// is not read access to anybody's account. Lookup is by digest, which is why the column is
// indexed and unique.
//
// ConsumedAt is null until the link is spent — the same "null means not yet" shape as
// User.OnboardingCompletedAt and User.EmailVerifiedAt. A spent row is deliberately KEPT
// until it expires, so a second click on the same link can be answered honestly ("already
// verified") rather than as an error.
public class AccountToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AccountTokenPurpose Purpose { get; set; }

    /// <summary>SHA-256 of the plaintext token, hex-encoded. The plaintext is never stored.</summary>
    public string TokenHash { get; set; } = null!;

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Null until the link is spent; a spent token is never accepted again.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
