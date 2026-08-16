using FluentValidation;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3).MaximumLength(50);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        // Accounts (KAN-19): the rules themselves moved to PasswordRules so password
        // RESET validates against the same set. Unchanged in substance — same length, same
        // letter + digit floor, same messages.
        RuleFor(x => x.Password).Password();
    }
}
