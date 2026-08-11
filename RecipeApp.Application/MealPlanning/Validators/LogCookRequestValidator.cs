using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Cook log (plan-page redesign / roadmap spec 2). Shape only — whether the recipe exists and
// whether the entry is the caller's are ownership questions, and those are 404s from the
// service, not 400s from here.
public class LogCookRequestValidator : AbstractValidator<LogCookRequest>
{
    public LogCookRequestValidator()
    {
        RuleFor(x => x.RecipeId).NotEmpty();

        // Guid.Empty is a client bug, not "no entry" — null is how you say ad-hoc. Letting
        // it through would spend a database round trip to arrive at the same 404.
        RuleFor(x => x.MealPlanEntryId)
            .NotEqual(Guid.Empty)
            .When(x => x.MealPlanEntryId is not null)
            .WithMessage("MealPlanEntryId must be omitted or a real entry id, not an empty Guid.");

        // KAN-6. The only rule this file can hold about a backdated cook, and it is the same one
        // MarkNotificationsReadRequestValidator and WeekStart hold: the timestamp must be UTC.
        // Npgsql rejects a non-UTC DateTime against timestamptz, so an unguarded Local- or
        // Unspecified-kind value surfaces as a 500 instead of a 400.
        //
        // The two BOUNDS on the date — no future, nothing before the caller's account — are
        // deliberately NOT here. "Before your account" is a database question and this validator
        // sees the body and nothing else; splitting the pair so that one edge answered from here
        // and the other from the service would leave two places to look when a date is refused.
        // Both live in CookLogService.LogAsync.
        RuleFor(x => x.CookedAt!.Value)
            .Must(value => value.Kind == DateTimeKind.Utc)
            .When(x => x.CookedAt is not null)
            .WithMessage("CookedAt must be a UTC timestamp.");

        // Same 500 as PATCH /cook-log/{id}: CookLog.Note is HasMaxLength(500), and a note is a
        // note whichever call wrote it.
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
