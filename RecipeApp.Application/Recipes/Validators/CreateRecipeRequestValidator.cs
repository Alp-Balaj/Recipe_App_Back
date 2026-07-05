using FluentValidation;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Validators;

public class CreateRecipeRequestValidator : AbstractValidator<CreateRecipeRequest>
{
    public CreateRecipeRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty();
        RuleFor(x => x.PrepTimeMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CookTimeMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Servings).GreaterThan(0);
        RuleFor(x => x.Difficulty).IsInEnum();
        RuleFor(x => x.Visibility).IsInEnum();
        RuleFor(x => x.CaloriesPerServing).GreaterThanOrEqualTo(0).When(x => x.CaloriesPerServing is not null);

        RuleFor(x => x.Ingredients).NotEmpty();
        RuleForEach(x => x.Ingredients).ChildRules(ingredient =>
        {
            ingredient.RuleFor(i => i.Name).NotEmpty();
            ingredient.RuleFor(i => i.Quantity).GreaterThan(0);
            ingredient.RuleFor(i => i.Unit).NotEmpty();
        });

        RuleFor(x => x.Steps).NotEmpty();
        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.StepNumber).GreaterThan(0);
            step.RuleFor(s => s.Description).NotEmpty();
        });
    }
}
