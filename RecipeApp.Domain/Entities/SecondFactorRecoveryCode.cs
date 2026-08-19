namespace RecipeApp.Domain.Entities;

// Accounts (KAN-21): one issued recovery code.
//
// CONTEXT.md → Recovery code: "a single-use secret, issued at enrolment, that answers a
// challenge in the authenticator's place. Spending one uses it up."
//
// Rows rather than a JSON column on UserSecondFactor, because "spend exactly one" is the
// whole behaviour and a row is what can be spent. The digest is the lookup key, exactly as
// on AccountToken and UserSession: read access to this table must not be sign-in access.
//
// A SPENT ROW IS KEPT. It is the only record that a recovery code was used, and the
// remaining count the settings screen shows is "how many are unspent", which needs both
// halves. Reissuing deletes the whole set and writes a fresh one, so the table does not grow.
public class SecondFactorRecoveryCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the normalised code, hex-encoded. The plaintext is shown once and never stored.</summary>
    public string CodeHash { get; set; } = null!;

    /// <summary>Null until the code is spent; a spent code is never accepted again.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
