namespace RecipeApp.Domain.Enums;

// Accounts (KAN-19, extended by KAN-21): the things a single-use emailed link can be for.
// One token concept with a purpose value rather than a table each — all of them are issued
// to a user, all expire, all are spent once, and the only difference between them is what
// consuming one means. Stored as text like every other enum.
public enum AccountTokenPurpose
{
    EmailVerification,
    PasswordReset,

    // KAN-21, the slowest rung of the recovery ladder. Consuming this one does NOT strip the
    // second factor — it starts a 48-hour countdown that the account holder can cancel from
    // any signed-in session. The delay is the whole point: without it, whoever holds the
    // mailbox could reset the password AND strip the factor through the same channel, and
    // the second factor would collapse back into the first.
    SecondFactorReset,
}
