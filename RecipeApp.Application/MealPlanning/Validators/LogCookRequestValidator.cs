using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Cook log (plan-page redesign / roadmap spec 2). Shape only — whether the recipe exists and
// whether the entry is the caller's are ownership questions, and those are 404s from the
// service, not 400s from here.
public class LogCookRequestValidator : AbstractValidator<LogCookRequest>
{
    public LogCookRequestValidator()
    {
        RuleFor(x => x.RecipeId).NotEmpty();

        // Guid.Empty is a client bug, not "no entry" — null is how you say ad-hoc. Letting
        // it through would spend a database round trip to arrive at the same 404.
        RuleFor(x => x.MealPlanEntryId)
            .NotEqual(Guid.Empty)
            .When(x => x.MealPlanEntryId is not null)
            .WithMessage("MealPlanEntryId must be omitted or a real entry id, not an empty Guid.");
    }
}
