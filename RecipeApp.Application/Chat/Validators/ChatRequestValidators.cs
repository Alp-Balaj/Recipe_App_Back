using FluentValidation;
using RecipeApp.Application.Chat.Dtos;

namespace RecipeApp.Application.Chat.Validators;

// Auto-registered by AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker> in
// Program.cs and applied at the endpoint via ValidationFilter<T>.

public class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    // Shared by POST /chat/conversations and POST /chat/conversations/{id}/messages.
    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Content).NotEmpty().MaximumLength(4000);
    }
}

public class RenameConversationRequestValidator : AbstractValidator<RenameConversationRequest>
{
    public RenameConversationRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
    }
}
