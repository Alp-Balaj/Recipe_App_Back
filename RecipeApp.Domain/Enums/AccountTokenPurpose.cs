namespace RecipeApp.Domain.Enums;

// Accounts (KAN-19): the two things a single-use emailed link can be for. One token
// concept with a purpose value rather than two tables — both are issued to a user, both
// expire, both are spent once, and the only difference between them is what consuming one
// means. Stored as text like every other enum.
public enum AccountTokenPurpose
{
    EmailVerification,
    PasswordReset,
}
