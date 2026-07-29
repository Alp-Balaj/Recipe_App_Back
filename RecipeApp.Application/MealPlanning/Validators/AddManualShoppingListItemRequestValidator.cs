using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Week/shopping rework (2026-07-29 design). Same caps as the superseded
// AddShoppingListItemRequestValidator (Ingredient 200 to match the title-shaped-field
// convention, Quantity 50 as a short display string like "2.5 cups"), plus the week.
public class AddManualShoppingListItemRequestValidator : AbstractValidator<AddManualShoppingListItemRequest>
{
    public AddManualShoppingListItemRequestValidator()
    {
        RuleFor(x => x.Ingredient).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Quantity).NotEmpty().MaximumLength(50);

        // Same pure-UTC-date rule as CreateMealPlanRequestValidator: a client-local midnight
        // or an offset date would stamp the item into a week no projection ever asks for, so
        // the row would silently never appear in any list.
        RuleFor(x => x.WeekStartDate)
            .Must(d => d.Kind == DateTimeKind.Utc && d.TimeOfDay == TimeSpan.Zero)
            .WithMessage("WeekStartDate must be a pure UTC date (00:00:00 UTC, no time component).");
    }
}
