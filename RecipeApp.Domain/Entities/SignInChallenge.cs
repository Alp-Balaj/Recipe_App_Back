namespace RecipeApp.Domain.Entities;

// Accounts (KAN-21): a sign-in that has passed the password and is waiting on the factor.
//
// CONTEXT.md → Challenge: "the moment an account is asked to produce its second factor."
// This row is that moment, made durable for the few minutes it lasts.
//
// WHY A ROW AND NOT AN OPTIONAL CODE FIELD ON /auth/login. Two reasons, both load-bearing:
//
//   * The password is then never held by the client across the prompt. A single call with an
//     optional code means the SPA keeps the password in memory while the user picks up their
//     phone, or asks for it twice.
//   * A per-account attempt cap needs somewhere to count, and this is the only place it can
//     live. Five wrong codes kill this row and sign-in starts over — a cap that bounds one
//     attacker's BURST, where SignInBackoff bounds their sustained rate (ADR-0008).
//
// It is deliberately NOT a UserSession. Nothing about it authenticates anything: it names an
// account and says a password was right a moment ago, which is worth nothing without the
// second factor it is waiting for. A session row would put it one bug away from being one.
public class SignInChallenge
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// SHA-256 of the token handed to the caller, hex-encoded — same shape as every other
    /// secret in this schema. The plaintext is in the login response and nowhere else.
    /// </summary>
    public string TokenHash { get; set; } = null!;

    /// <summary>
    /// Wrong codes answered against this challenge. At <see cref="MaxFailedAttempts"/> the
    /// row is deleted and the caller starts again from the password.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>
    /// Short — this is the gap between typing a password and reading a phone, not a session.
    /// An abandoned challenge left lying around is a password success somebody could still
    /// finish tomorrow.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Carried from the sign-in request so the session this challenge eventually opens is
    /// labelled with the device that started it, rather than with whatever answered.
    /// </summary>
    public string? UserAgent { get; set; }

    public User User { get; set; } = null!;

    /// <summary>See <see cref="FailedAttempts"/>. Five, per ADR-0008.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>How long a raised challenge stays answerable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
}
