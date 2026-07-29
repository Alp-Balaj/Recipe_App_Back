using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Week/shopping rework (2026-07-29 design). The mark is stored as-is under the key the
// projection handed the client back, so the only shape rules are "there is a key" and "it
// fits the column" — ShoppingListMark.Key is HasMaxLength(200).
//
// Deliberately NOT re-normalised through IngredientKey.For here: the key may be the synthetic
// "manual:{id}" form, which normalisation would mangle, and a derived key is already normal.
public class SetShoppingListMarkRequestValidator : AbstractValidator<SetShoppingListMarkRequest>
{
    public SetShoppingListMarkRequestValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(200);

        // The rework's Global Constraint: UTC-midnight MONDAY. A mark on any other instant can
        // never be joined to a projected week, so it would be written and then never read.
        RuleFor(x => x.WeekStartDate)
            .Must(WeekStart.IsUtcMidnightMonday)
            .WithMessage($"WeekStartDate {WeekStart.ValidationMessage}");

        // Suppression is the lightweight pantry for DERIVED groups ("I already have olive
        // oil"): it hides a group the plan will regenerate next week anyway. A manual row has
        // no such regeneration — it supports a real DELETE /shopping-list/{id} — so the
        // projection deliberately ignores IsSuppressed on a manual key. Rejecting is the honest
        // contract; accepting a flag and silently dropping it is not.
        //
        // The .When guard is load-bearing, not decoration. FluentValidation's default cascade
        // mode runs every rule in the class even after a sibling fails, so a request carrying
        // {"key": null, "isSuppressed": true} would reach this rule with Key already known-bad
        // and dereference it — an unhandled NullReferenceException, which GlobalExceptionHandler
        // turns into a 500 for what is plainly a 400. (This repo has been bitten by a 400 masked
        // as a 500 before; do not remove the guard.) Same `.When(x => …)` shape as
        // UpdateProfileRequestValidator's nullable-field rules.
        RuleFor(x => x.IsSuppressed)
            .Must((request, isSuppressed) => !(isSuppressed && ShoppingListKeys.IsManual(request.Key)))
            .WithMessage("A manual item cannot be suppressed — delete it with DELETE /shopping-list/{id} instead.")
            .When(x => !string.IsNullOrEmpty(x.Key));
    }
}
