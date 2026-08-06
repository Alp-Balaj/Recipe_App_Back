using FluentValidation;
using RecipeApp.Application.Social.Dtos;

namespace RecipeApp.Application.Social.Validators;

// Validates PUT /users/me. Username mirrors register (3–50); Bio caps at the 160 the
// Edit-profile UI counts down from; the image URL is bounded; visibility must be a known
// enum member. Auto-registered via AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker>.
public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3).MaximumLength(50);
        RuleFor(x => x.Bio).MaximumLength(160).When(x => x.Bio is not null);
        RuleFor(x => x.ProfileImageUrl).MaximumLength(2048).When(x => x.ProfileImageUrl is not null);
        RuleFor(x => x.DefaultRecipeVisibility).IsInEnum();
        // Stream G: each restriction must be a known member. Undefined values would sail
        // through model binding (the JsonStringEnumConverter allows integers) and land in
        // jsonb, then reach an AI system prompt as a meaningless constraint.
        RuleForEach(x => x.DietaryRestrictions).IsInEnum().When(x => x.DietaryRestrictions is not null);
        // Stream K: same guard, same reason — an undefined Cuisine would bind, land in jsonb
        // and then be weighted against a candidate set it can never match.
        RuleForEach(x => x.CuisinePreferences).IsInEnum().When(x => x.CuisinePreferences is not null);
    }
}

// Validates POST /users/me/onboarding. Both lists are optional — omitting both IS the skip
// case — so the only rule is that whatever IS sent names real members, exactly as the
// profile validator requires above.
public class CompleteOnboardingRequestValidator : AbstractValidator<CompleteOnboardingRequest>
{
    public CompleteOnboardingRequestValidator()
    {
        RuleForEach(x => x.CuisinePreferences).IsInEnum().When(x => x.CuisinePreferences is not null);
        RuleForEach(x => x.DietaryRestrictions).IsInEnum().When(x => x.DietaryRestrictions is not null);
    }
}
