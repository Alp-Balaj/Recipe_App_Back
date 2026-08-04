using FluentValidation;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Validators;

public class CreateRecipeRequestValidator : AbstractValidator<CreateRecipeRequest>
{
    public CreateRecipeRequestValidator()
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

        // Stream G: the typed vocabularies. IsInEnum is doing real work here rather than
        // restating the type — the global JsonStringEnumConverter is registered with integer
        // values allowed, so a client posting "cuisineType": 999 binds to an undefined enum
        // value without complaint and would otherwise reach jsonb. (Same reasoning as
        // CreateReportRequestValidator's note.) A null cuisine is legal: "no particular
        // cuisine" is an answer.
        RuleFor(x => x.CuisineType).IsInEnum().When(x => x.CuisineType is not null);
        RuleForEach(x => x.Tags).IsInEnum();

        RuleFor(x => x.Ingredients).NotEmpty();
        RuleForEach(x => x.Ingredients).ChildRules(ingredient =>
        {
            ingredient.RuleFor(i => i.Name).NotEmpty();
            ingredient.RuleFor(i => i.Quantity).GreaterThan(0);
            // Was NotEmpty() on a free-text string. The unit is now a closed vocabulary, so
            // "what is a valid unit" is a question the type answers and this only has to
            // catch the undefined-value case above.
            ingredient.RuleFor(i => i.Unit).IsInEnum();
        });

        RuleFor(x => x.Steps).NotEmpty();
        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.StepNumber).GreaterThan(0);
            step.RuleFor(s => s.Description).NotEmpty();
        });
    }
}
