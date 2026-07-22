using FluentValidation;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3).MaximumLength(50);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        // publish cp1 auth hardening: length alone lets "aaaaaaaa" through a public
        // signup. Letter + digit is the floor, not full complexity theater.
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8)
            .Matches("[A-Za-z]").WithMessage("Password must contain at least one letter.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.");
    }
}
