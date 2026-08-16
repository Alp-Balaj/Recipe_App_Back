using FluentValidation;

namespace RecipeApp.Application.Auth.Validators;

// The password rule set, expressed ONCE (Accounts, KAN-19).
//
// It used to live inline in RegisterRequestValidator, which was fine while registration was
// the only place a password was chosen. Password reset is a second such place, and a
// requirement that applies at registration but not at reset is not a requirement — it is a
// back door with a slightly longer path to it. So the rules move here and both validators
// call this.
public static class PasswordRules
{
    public const int MinimumLength = 8;

    /// <summary>
    /// publish cp1 auth hardening: length alone lets "aaaaaaaa" through a public signup.
    /// Letter + digit is the floor, not full complexity theater.
    /// </summary>
    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(MinimumLength)
            .Matches("[A-Za-z]").WithMessage("Password must contain at least one letter.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.");
}
