using FluentValidation;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Validators;

// Mirrors CreateRecipeRequestValidator rule for rule: PUT is a full replace, so an
// update body must satisfy exactly what a create body would.
public class UpdateRecipeRequestValidator : AbstractValidator<UpdateRecipeRequest>
{
    public UpdateRecipeRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        // Cap the free-text description: it is injected verbatim into the chat grounding prompt,
        // so an unbounded value would inflate token cost. 2000 chars is ample for a real recipe.
        RuleFor(x => x.Description).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.PrepTimeMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CookTimeMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Servings).GreaterThan(0);
        RuleFor(x => x.Difficulty).IsInEnum();
        RuleFor(x => x.Visibility).IsInEnum();
        RuleFor(x => x.CaloriesPerServing).GreaterThanOrEqualTo(0).When(x => x.CaloriesPerServing is not null);

        // Stream G's typed vocabularies — see CreateRecipeRequestValidator for why IsInEnum
        // is not redundant with the CLR type.
        RuleFor(x => x.CuisineType).IsInEnum().When(x => x.CuisineType is not null);
        RuleForEach(x => x.Tags).IsInEnum();

        RuleFor(x => x.Ingredients).NotEmpty();
        RuleForEach(x => x.Ingredients).ChildRules(ingredient =>
        {
            ingredient.RuleFor(i => i.Name).NotEmpty();
            ingredient.RuleFor(i => i.Quantity).GreaterThan(0);
            ingredient.RuleFor(i => i.Unit).IsInEnum();
        });

        RuleFor(x => x.Steps).NotEmpty();
        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.StepNumber).GreaterThan(0);
            step.RuleFor(s => s.Description).NotEmpty();

            // Stream J's typed step — see CreateRecipeRequestValidator for the reasoning.
            step.RuleFor(s => s.DurationSeconds)
                .InclusiveBetween(1, RecipeStepRules.MaxDurationSeconds)
                .When(s => s.DurationSeconds is not null);

            step.RuleFor(s => s.Temperature)
                .Must(RecipeStepRules.TemperatureIsValid)
                .WithMessage(RecipeStepRules.TemperatureMessage);
        });

        // Decision D16. This one matters MORE on the update path than the create path: an
        // edit is where an ingredient line gets deleted out from under a step's reference,
        // and PUT being a full replace is exactly what lets the pair be checked together.
        RuleForEach(x => x.Steps)
            .Must((request, step) =>
                RecipeStepRules.IngredientIndexesAreValid(step, request.Ingredients?.Count ?? 0))
            .WithMessage(RecipeStepRules.IngredientIndexesMessage);
    }
}
