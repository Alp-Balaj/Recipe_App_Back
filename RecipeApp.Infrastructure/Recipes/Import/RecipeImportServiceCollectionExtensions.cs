using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Recipes.Import;

// Stream L. Registers both import tiers: the guarded fetcher, the extraction assistant, and
// the orchestrator. (The vision provider seam moved to AddVisionCaller when stream N became
// its second caller — see VisionServiceCollectionExtensions.)
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

        // The vision provider seam used to be registered HERE, behind a private
        // "recipe-import-vision" client — the one seam-level wart this file carried: the
        // interface was clean but its wiring was import's. Stream N hoisted it into
        // AddVisionCaller the moment a second caller existed, so neither feature depends on
        // the other having run. Idempotent, so AddFoodScanner calling it too is fine.
        services.AddVisionCaller(configuration);

        services.AddScoped<IRecipePageFetcher>(sp => new SafeRecipePageFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(FetchClientName),
            sp.GetRequiredService<RecipeImportOptions>(),
            sp.GetRequiredService<ILogger<SafeRecipePageFetcher>>()));

        // Depends on BOTH provider seams — the text caller for the page fallback, the vision
        // caller for photos — and neither resolves a key until it is actually called.
        services.AddScoped<IRecipeExtractionAssistant, RecipeExtractionAssistant>();

        services.AddScoped<IRecipeImportService, RecipeImportService>();

        return services;
    }
}
