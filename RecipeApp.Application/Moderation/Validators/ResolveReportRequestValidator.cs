using FluentValidation;
using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.Application.Moderation.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Shared by
// POST /admin/reports/{id}/resolve and /dismiss.
public class ResolveReportRequestValidator : AbstractValidator<ResolveReportRequest>
{
    public ResolveReportRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
