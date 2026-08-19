namespace RecipeApp.Domain.Entities;

// Accounts (KAN-21): the third and slowest rung of the recovery ladder — stripping the
// second factor through email, after a deliberate delay.
//
// THE DELAY IS THE FEATURE. Without it, whoever holds the mailbox can reset the password AND
// strip the second factor through the same channel, and the second factor collapses back
// into the first — which is precisely the reason CONTEXT.md says "a password and an emailed
// code are not two factors when that same mailbox can also reset the password". Forty-eight
// hours costs the honest user nothing, because someone who has merely lost their phone still
// has their recovery codes; it only bites when both are gone, which is the case worth being
// slow about.
//
// The two other rungs need no row at all: a recovery code is spent in one call, and an admin
// reset happens in one call with a human behind it. Only this one has to wait, so only this
// one is a record.
public class SecondFactorResetRequest
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the sweeper may strip the factor. Set once at request time and never moved —
    /// in particular a password reset inside the window does NOT shorten it, because a
    /// password reset runs through the same mailbox and shortening it would hand that
    /// mailbox the whole account again.
    /// </summary>
    public DateTime EffectiveAtUtc { get; set; }

    /// <summary>
    /// Null unless someone signed in and stopped it. This is the honest user's answer to a
    /// reset they did not ask for: they still have their factor, so they can still get in,
    /// and getting in is what cancelling requires. Deliberately not cancellable from a link
    /// in the notification mail — that would hand the cancel button to the same mailbox the
    /// request came from, which is nobody's threat model.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Null until the sweeper has acted. A completed row is kept as the record that it happened.</summary>
    public DateTime? CompletedAt { get; set; }

    public User User { get; set; } = null!;

    /// <summary>The cooling-off period. Forty-eight hours, per the KAN-21 design.</summary>
    public static readonly TimeSpan CoolingOff = TimeSpan.FromHours(48);
}
