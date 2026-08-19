namespace RecipeApp.Domain.Entities;

// Accounts (KAN-21): an account's second factor — one row per user, or none.
//
// CONTEXT.md → Enrolled: "said of an account that has a second factor registered".
// That predicate is `EnrolledAt != null` on this row, and it is the single question
// KAN-22's gate will ask.
//
// THE ROW EXISTS BEFORE THE FACTOR DOES. Enrolment is two steps — scan a QR, then prove a
// code from it — and the secret has to survive between them, so a row with a null
// EnrolledAt means "started, not proved". Same "null means not yet" shape as
// User.EmailVerifiedAt and AccountToken.ConsumedAt, and the reason for the two steps is
// that a mis-scanned QR must fail at enrolment rather than at the next sign-in, when it
// would be a lockout.
public class UserSecondFactor
{
    public Guid Id { get; set; }

    /// <summary>Unique: an account has one second factor. A second kind of factor would be a second table.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The shared TOTP secret, base32.
    ///
    /// This is the ONE secret in this schema that is not a digest, and it cannot be one:
    /// verifying a code means recomputing it, which needs the secret itself. Encrypting it
    /// under the JWT signing key was considered and rejected — that key is a deployment
    /// secret with no rotation story, so rotating it would silently un-enrol every account
    /// and the failure would surface as "my authenticator stopped working" rather than as
    /// anything an operator could read. The honest statement of the exposure is: database
    /// read access yields the ability to generate codes, and the password is still in the
    /// way.
    /// </summary>
    public string Secret { get; set; } = null!;

    /// <summary>
    /// Null until a code from this secret has been proved. Non-null is what "enrolled" means
    /// everywhere in the app.
    /// </summary>
    public DateTime? EnrolledAt { get; set; }

    /// <summary>
    /// The most recent TOTP step this account has spent, or null before the first one.
    ///
    /// Without it a code stays usable for the rest of its ninety-second window, so anyone
    /// who read it over a shoulder — or off a phishing page a moment ago — can use it again.
    /// A code is accepted only if its step is strictly greater than this.
    /// </summary>
    public long? LastAcceptedStep { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
