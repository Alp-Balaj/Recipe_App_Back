using FluentValidation;
using RecipeApp.Application.Notifications.Dtos;

namespace RecipeApp.Application.Notifications.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>.
//
// The only rule that matters: the bound must be UTC. Npgsql rejects a non-UTC DateTime
// against timestamptz, so an unguarded local-kind value would surface as a 500 rather
// than a 400 — the same trap WeekStart guards on the meal-plan side.
public class MarkNotificationsReadRequestValidator : AbstractValidator<MarkNotificationsReadRequest>
{
    public MarkNotificationsReadRequestValidator()
    {
        RuleFor(x => x.UpTo)
            .Must(value => value.Kind == DateTimeKind.Utc)
            .WithMessage("UpTo must be a UTC timestamp.");
    }
}
