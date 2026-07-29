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

        RuleFor(x => x.WeekStartDate)
            .Must(d => d.Kind == DateTimeKind.Utc && d.TimeOfDay == TimeSpan.Zero)
            .WithMessage("WeekStartDate must be a pure UTC date (00:00:00 UTC, no time component).");
    }
}
