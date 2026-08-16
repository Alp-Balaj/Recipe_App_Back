using FluentValidation;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Validators;

// Accounts (KAN-19). The three request validators for the recovery endpoints.
//
// Named plurally and kept in one file, following ChatRequestValidators.cs: the repo's rule
// is one validator per file, and its one existing exception is a small family of validators
// for a single feature that would otherwise be three files of four lines each. These are
// that same shape.
//
// The password half is the SAME rule set registration validates against (PasswordRules) —
// the requirements a user meets when they sign up are the requirements they meet when they
// choose a replacement.
//
// The token is only checked for presence here. Whether it is real, unspent and unexpired is
// a database question, answered by the service, and answering it in a validator would turn
// a 400 body into an oracle for guessed tokens.
public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword).Password();
    }
}

public class RequestPasswordResetRequestValidator : AbstractValidator<RequestPasswordResetRequest>
{
    public RequestPasswordResetRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public class ConfirmEmailVerificationRequestValidator : AbstractValidator<ConfirmEmailVerificationRequest>
{
    public ConfirmEmailVerificationRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}
