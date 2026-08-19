using FluentValidation;
using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth.Validators;

// Accounts (KAN-21). The request validators for the second-factor endpoints, in one file for
// the same reason AccountRecoveryValidators.cs is: a small family belonging to one feature,
// which would otherwise be five files of four lines each.
//
// WHAT IS DELIBERATELY NOT CHECKED HERE. No validator asks whether a code is six digits or
// ten characters, whether a token names a real challenge, or whether a recovery code has been
// spent. All of those are answered by the service against the database, and answering any of
// them in a validator would turn a 400 body into an oracle: a caller could learn the SHAPE of
// a valid answer, or that a guessed token named a real sign-in, from a message that never
// touched the account. Presence is the only thing a validator can check without leaking.

public class AnswerChallengeRequestValidator : AbstractValidator<AnswerChallengeRequest>
{
    public AnswerChallengeRequestValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty();
        // Bounded, not shaped. A length cap keeps a megabyte of "code" from reaching the
        // hasher; anything narrower would start describing what a valid code looks like.
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64);
    }
}

public class ConfirmSecondFactorRequestValidator : AbstractValidator<ConfirmSecondFactorRequest>
{
    public ConfirmSecondFactorRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64);
    }
}

public class SecondFactorCodeRequestValidator : AbstractValidator<SecondFactorCodeRequest>
{
    public SecondFactorCodeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64);
    }
}

public class RequestSecondFactorResetRequestValidator : AbstractValidator<RequestSecondFactorResetRequest>
{
    public RequestSecondFactorResetRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public class ConfirmSecondFactorResetRequestValidator : AbstractValidator<ConfirmSecondFactorResetRequest>
{
    public ConfirmSecondFactorResetRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}
