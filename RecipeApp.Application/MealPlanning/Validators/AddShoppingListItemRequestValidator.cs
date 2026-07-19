using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Length caps mirror the
// existing title/name-shaped-field convention (CreateRecipeRequestValidator.Title,
// ChatRequestValidators.Title both cap at 200); Quantity is a short display string
// ("2.5 cups") so it gets the shorter cap used for short fields (RegisterRequestValidator.Username).
public class AddShoppingListItemRequestValidator : AbstractValidator<AddShoppingListItemRequest>
{
    public AddShoppingListItemRequestValidator()
    {
        RuleFor(x => x.Ingredient).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Quantity).NotEmpty().MaximumLength(50);
    }
}
