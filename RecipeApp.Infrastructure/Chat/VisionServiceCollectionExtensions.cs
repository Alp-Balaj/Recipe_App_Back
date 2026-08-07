using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RecipeApp.Infrastructure.Chat;

// Stream N. The IVisionMessageCaller registration, HOISTED out of AddRecipeImport.
//
// Stream L built the vision seam provider-neutral and deliberately not named for import
// (see IVisionMessageCaller's header), but its WIRING still lived inside an import-named
// extension behind a client called "recipe-import-vision" — an acknowledged seam-level wart.
// The moment a second caller exists (the food scanner), leaving it there would make the
// scanner depend on AddRecipeImport having run, silently: nothing in a registration lambda
// fails until first resolution, so removing import would take the scanner down with it and
// the connection would be visible nowhere.
//
// So both features now call AddVisionCaller and neither owns the seam. The method is
// IDEMPOTENT — an explicit already-registered guard up front — so registration order
// between AddRecipeImport and AddFoodScanner is immaterial, exactly as DI order is
// everywhere else in Program.cs.
//
// Everything else is L's registration moved verbatim: lazy key resolution (an absent Gemini
// key breaks the first call that needs one, not the host — the property the integration
// tests and the JSON-LD free path both depend on), and a named client with its own 90 s
// ceiling because a vision request carries base64 image data and must not make every chat
// turn wait as long as a photo read.
public static class VisionServiceCollectionExtensions
{
    /// <summary>The named client for vision provider calls (import photos and food scans).</summary>
    public const string VisionClientName = "vision";

    public static IServiceCollection AddVisionCaller(this IServiceCollection services, IConfiguration configuration)
    {
        // Guarded on the service rather than relying on TryAddScoped alone so the named
        // client's configuration actions are registered once, not once per caller feature.
        if (services.Any(d => d.ServiceType == typeof(IVisionMessageCaller)))
        {
            return services;
        }

        services.AddHttpClient(VisionClientName)
            .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(90));

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

        return services;
    }
}
