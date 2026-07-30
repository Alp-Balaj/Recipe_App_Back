using FluentValidation;
using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.Application.Moderation.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Used by
// POST /admin/users/{id}/suspend.
//
// The ceiling is deliberate: past a year, the honest action is a ban — which is
// reversible anyway (unban) and reads truthfully in the audit log.
public class SuspendUserRequestValidator : AbstractValidator<SuspendUserRequest>
{
    public SuspendUserRequestValidator()
    {
        RuleFor(x => x.Days).InclusiveBetween(1, 365);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
