using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning;

namespace RecipeApp.Application.MealPlanning.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Same Global Constraint as
// CreateMealPlanRequestValidator: a proposal for a week that isn't a UTC-midnight Monday
// could never match a stored plan's slots, so 400 beats a proposal for a phantom week.
public class ProposeWeekRequestValidator : AbstractValidator<ProposeWeekRequest>
{
    public ProposeWeekRequestValidator()
    {
        RuleFor(x => x.WeekStartDate)
            .Must(WeekStart.IsUtcMidnightMonday)
            .WithMessage($"WeekStartDate {WeekStart.ValidationMessage}");
    }
}
