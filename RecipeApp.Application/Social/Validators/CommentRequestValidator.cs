using FluentValidation;
using RecipeApp.Application.Social.Dtos;

namespace RecipeApp.Application.Social.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>. Shared by
// POST /recipes/{id}/comments and PUT /comments/{id}.
public class CommentRequestValidator : AbstractValidator<CommentRequest>
{
    public CommentRequestValidator()
    {
        RuleFor(x => x.Content).NotEmpty().MaximumLength(2000);
    }
}
