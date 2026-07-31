using FluentValidation;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Validators;

public class GenerateRecipeRequestValidator : AbstractValidator<GenerateRecipeRequest>
{
    // The prompt is injected verbatim into a paid provider call, so it is bounded for the
    // same reason CreateRecipeRequestValidator bounds Description: an unbounded value is an
    // unbounded token bill. 1000 characters is far more than any real "make me something
    // with…" request needs.
    public const int MaxPromptLength = 1000;

    public GenerateRecipeRequestValidator()
    {
        RuleFor(x => x.Prompt).NotEmpty().MaximumLength(MaxPromptLength);
        // Only checked when supplied — omitting it means "use my default visibility".
        RuleFor(x => x.Visibility).IsInEnum().When(x => x.Visibility is not null);
    }
}
