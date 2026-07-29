using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning;

namespace RecipeApp.Application.MealPlanning.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>.
public class CreateMealPlanRequestValidator : AbstractValidator<CreateMealPlanRequest>
{
    public CreateMealPlanRequestValidator()
    {
        // Week/shopping rework's Global Constraint: WeekStartDate is always a UTC-midnight
        // MONDAY. Previously this only checked pure-UTC-midnight, which let a client create a
        // plan on e.g. a Wednesday — a plan whose shopping list GET /shopping-list and PUT
        // /shopping-list/marks could then never reach, since both already enforce the Monday
        // rule via WeekStart.IsUtcMidnightMonday. No legitimate client sends a non-Monday (the
        // SPA always computes it), so tightening this closes the self-contradiction rather than
        // breaking a real caller.
        RuleFor(x => x.WeekStartDate)
            .Must(WeekStart.IsUtcMidnightMonday)
            .WithMessage($"WeekStartDate {WeekStart.ValidationMessage}");
    }
}
