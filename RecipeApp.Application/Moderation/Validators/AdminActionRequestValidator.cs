using FluentValidation;
using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.Application.Moderation.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Shared by the admin
// actions whose body is just an optional reason (hide/restore/remove/ban).
public class AdminActionRequestValidator : AbstractValidator<AdminActionRequest>
{
    public AdminActionRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
