using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// Wires IChatAssistantService -> ClaudeChatAssistantService and its Anthropic SDK seam.
// Lives in Infrastructure so the Anthropic package/types stay out of the API project
// (kickoff: add the SDK to Infrastructure ONLY).
public static class ChatServiceCollectionExtensions
{
    public static IServiceCollection AddChatAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        // Factory (not constructor injection) so the missing-key check runs only when the
        // caller is actually resolved — which nothing does until chat-ai cp03. This keeps the
        // key optional at startup, so the existing integration-test host (which builds the
        // real Program.cs and never resolves chat) doesn't need an Anthropic key in CI.
        services.AddScoped<IAnthropicMessageCaller>(_ =>
        {
            var apiKey = configuration["Anthropic:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "Anthropic:ApiKey not configured — set via user-secrets or Anthropic__ApiKey env var. " +
                    "The key is server-side only and must never be committed or sent to the browser.");
            }

            return new AnthropicMessageCaller(new AnthropicClient { ApiKey = apiKey });
        });

        services.AddScoped<IChatAssistantService, ClaudeChatAssistantService>();

        // chat-ai cp03: the endpoint-facing orchestrator. Depends on IChatAssistantService
        // above (which is what actually resolves the Anthropic key, lazily via the factory).
        services.AddScoped<IChatService, ChatService>();

        return services;
    }
}
