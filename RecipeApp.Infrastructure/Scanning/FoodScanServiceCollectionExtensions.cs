using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Scanning.Abstractions;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Scanning;

// Stream N. Registers the food scanner: the vision seam (shared with import via the
// hoisted, idempotent AddVisionCaller — see VisionServiceCollectionExtensions for why
// neither feature owns it), the detection assistant, and the orchestrator. Nothing here
// resolves a Gemini key until a scan actually runs, per the registration idiom every AI
// feature in this app follows.
public static class FoodScanServiceCollectionExtensions
{
    public static IServiceCollection AddFoodScanner(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddVisionCaller(configuration);

        services.AddScoped<IFoodScanAssistant, FoodScanAssistant>();
        services.AddScoped<IFoodScanService, FoodScanService>();

        return services;
    }
}
