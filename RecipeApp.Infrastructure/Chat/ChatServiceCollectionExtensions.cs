using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// Wires IChatAssistantService -> ChatAssistantService and its Gemini network seam. Lives in
// Infrastructure so the HTTP integration stays out of the API project.
public static class ChatServiceCollectionExtensions
{
    // A conservative ceiling for a single chat turn's HTTP round-trip.
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddChatAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        // IHttpClientFactory: socket-reuse correctness for the repo's first HTTP integration.
        services.AddHttpClient();

        // Factory (not constructor injection) so the missing-key check runs only when the caller
        // is actually resolved — which nothing does until a chat turn runs. This keeps the key
        // optional at startup, so the integration-test host (which builds the real Program.cs and
        // never hits chat) needs no Gemini key in CI.
        services.AddScoped<IChatMessageCaller>(sp =>
        {
            var apiKey = configuration["Gemini:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "Gemini:ApiKey not configured — set via user-secrets or Gemini__ApiKey env var. " +
                    "The key is server-side only and must never be committed or sent to the browser.");
            }

            var options = new GeminiOptions { ApiKey = apiKey };
            // Picks up Model/BaseUrl/MaxOutputTokens overrides (and re-binds ApiKey to the same value).
            configuration.GetSection(GeminiOptions.SectionName).Bind(options);

            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            http.Timeout = HttpTimeout;
            return new GeminiMessageCaller(http, options);
        });

        services.AddScoped<IChatAssistantService, ChatAssistantService>();

        // The endpoint-facing orchestrator. Depends on IChatAssistantService above (which is what
        // actually resolves the Gemini key, lazily via the factory).
        services.AddScoped<IChatService, ChatService>();

        return services;
    }
}
