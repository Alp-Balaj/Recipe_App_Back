using FluentValidation;
using RecipeApp.Application.Social.Dtos;

namespace RecipeApp.Application.Social.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Used by
// PUT /recipes/{recipeId}/rating.
//
// The same 1-5 range is also a check constraint on the CookedRecipes table: this validator
// turns a bad value into a clean 400, the constraint stops any future write path that
// bypasses it from corrupting the average.
public class RatingRequestValidator : AbstractValidator<RatingRequest>
{
    public RatingRequestValidator()
    {
        RuleFor(x => x.Rating).InclusiveBetween(1, 5);
    }
}
