using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>.
public class AddMealPlanEntryRequestValidator : AbstractValidator<AddMealPlanEntryRequest>
{
    public AddMealPlanEntryRequestValidator()
    {
        // Stream G turned OFF allowIntegerValues on the global JsonStringEnumConverter, so a
        // numeric body value no longer binds at all — "dayOfWeek": 9 is a 400 at
        // deserialization now, alongside malformed strings like "Fourthday".
        //
        // IsInEnum() stays, and is not redundant: it guards the value that arrives by a CAST
        // rather than off the wire, which is how the unit tests reach these rules and how any
        // future in-process caller would.
        RuleFor(x => x.DayOfWeek).IsInEnum();
        RuleFor(x => x.MealType).IsInEnum();
        RuleFor(x => x.RecipeId).NotEmpty();
    }
}
