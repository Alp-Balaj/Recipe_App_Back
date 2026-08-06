using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Recipes.Import;

// Stream L. Registers both import tiers: the guarded fetcher, the vision provider seam, the
// extraction assistant, and the orchestrator.
//
// The registration idiom is ChatServiceCollectionExtensions', deliberately — factory lambdas
// with LAZY key resolution, so an absent Gemini key breaks the first call that needs one and
// not the host. Two things depend on that: the integration-test host builds the real Program.cs
// and must not need a key, and the JSON-LD import path must keep working on a deployment where
// no key is configured at all. The second is not hypothetical — it is the whole point of Tier
// 1's deterministic half, and an eager check here would make the free path require the thing
// it exists to avoid.
public static class RecipeImportServiceCollectionExtensions
{
    /// <summary>The named client for outbound page and image fetches.</summary>
    public const string FetchClientName = "recipe-import";

    /// <summary>The named client for the vision provider call.</summary>
    private const string VisionClientName = "recipe-import-vision";

    public static IServiceCollection AddRecipeImport(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new RecipeImportOptions();
        configuration.GetSection(RecipeImportOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // A NAMED client with its own primary handler, because this one is unlike every other
        // HttpClient in the app: it connects to addresses a user chose. GuardedHttpHandler's
        // ConnectCallback is what enforces the address policy at connect time, and it must not
        // leak onto the default client that the Gemini callers use — nor they onto it, since
        // AllowAutoRedirect=false would change how the provider call behaves.
        services.AddHttpClient(FetchClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
                GuardedHttpHandler.Create(sp.GetRequiredService<RecipeImportOptions>()))
            // The fetcher applies its own linked-token deadline per request; this is the outer
            // backstop. Infinite here so a redirect chain's total time is governed by the one
            // timeout the fetcher controls rather than by two that can disagree.
            .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan);

        // A separate named client for the provider: a vision request carries base64 image data
        // and takes appreciably longer than a text turn, so it gets a longer ceiling than the
        // chat client's 30 s rather than making every chat turn wait as long as a photo import.
        services.AddHttpClient(VisionClientName)
            .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(90));

        services.AddScoped<IRecipePageFetcher>(sp => new SafeRecipePageFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(FetchClientName),
            sp.GetRequiredService<RecipeImportOptions>(),
            sp.GetRequiredService<ILogger<SafeRecipePageFetcher>>()));

        // Lazy key resolution, exactly as AddChatAssistant does it — see the class comment.
        services.AddScoped<IVisionMessageCaller>(sp =>
        {
            var apiKey = configuration["Gemini:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "Gemini:ApiKey not configured — set via user-secrets or Gemini__ApiKey env var. " +
                    "The key is server-side only and must never be committed or sent to the browser.");
            }

            var geminiOptions = new GeminiOptions { ApiKey = apiKey };
            configuration.GetSection(GeminiOptions.SectionName).Bind(geminiOptions);

            return new GeminiVisionMessageCaller(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(VisionClientName),
                geminiOptions);
        });

        // Depends on BOTH provider seams — the text caller for the page fallback, the vision
        // caller for photos — and neither resolves a key until it is actually called.
        services.AddScoped<IRecipeExtractionAssistant, RecipeExtractionAssistant>();

        services.AddScoped<IRecipeImportService, RecipeImportService>();

        return services;
    }
}
