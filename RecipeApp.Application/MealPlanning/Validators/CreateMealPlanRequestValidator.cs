using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>.
public class CreateMealPlanRequestValidator : AbstractValidator<CreateMealPlanRequest>
{
    public CreateMealPlanRequestValidator()
    {
        // meal-planning-v1-semantics: WeekStartDate must be a pure UTC date (Kind Utc after
        // binding, time component zero) so the (UserId, WeekStartDate) uniqueness is
        // meaningful — a client-local midnight or an offset date would silently fragment
        // "the same week" into multiple rows.
        RuleFor(x => x.WeekStartDate)
            .Must(d => d.Kind == DateTimeKind.Utc && d.TimeOfDay == TimeSpan.Zero)
            .WithMessage("WeekStartDate must be a pure UTC date (00:00:00 UTC, no time component).");
    }
}
