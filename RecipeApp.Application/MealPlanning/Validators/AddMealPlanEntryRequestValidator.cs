using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>.
public class AddMealPlanEntryRequestValidator : AbstractValidator<AddMealPlanEntryRequest>
{
    public AddMealPlanEntryRequestValidator()
    {
        // The global JsonStringEnumConverter is registered with AllowIntegerValues (the
        // default), so a numeric body value outside the enum's range binds silently instead
        // of throwing at deserialization — IsInEnum() is what turns that into a 400 instead
        // of a garbage DayOfWeek/MealType reaching the service. Malformed *string* values
        // (e.g. "Fourthday") never reach here — they fail at JSON body binding first (400).
        RuleFor(x => x.DayOfWeek).IsInEnum();
        RuleFor(x => x.MealType).IsInEnum();
        RuleFor(x => x.RecipeId).NotEmpty();
    }
}
