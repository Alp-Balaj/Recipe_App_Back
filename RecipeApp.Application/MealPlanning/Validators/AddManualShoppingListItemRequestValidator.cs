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

        // The rework's Global Constraint: UTC-midnight MONDAY. A client-local midnight, an
        // offset date, or a midnight that lands on a Wednesday would all stamp the item into a
        // week no plan can ever equal — the row would then be invisible in every week view and
        // show up only as a phantom week under scope=All.
        RuleFor(x => x.WeekStartDate)
            .Must(WeekStart.IsUtcMidnightMonday)
            .WithMessage($"WeekStartDate {WeekStart.ValidationMessage}");
    }
}
