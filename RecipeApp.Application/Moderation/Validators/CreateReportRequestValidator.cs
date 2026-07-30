using FluentValidation;
using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.Application.Moderation.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Used by POST /reports.
//
// IsInEnum matters here: TargetType and Reason arrive as strings (JsonStringEnumConverter),
// but a numeric body value outside the enum would otherwise bind silently and corrupt the
// discriminator that decides which FK the service fills.
public class CreateReportRequestValidator : AbstractValidator<CreateReportRequest>
{
    public CreateReportRequestValidator()
    {
        RuleFor(x => x.TargetType).IsInEnum();
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Details).MaximumLength(1000);
    }
}
