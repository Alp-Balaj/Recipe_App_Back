using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.API.Endpoints;

// Admin Rework stream BE-A: read-only analytics beside the moderation endpoints.
// A second MapGroup("/admin") is deliberate — same policy set, disjoint file.
public static class AdminAnalyticsEndpoints
{
    public static void MapAdminAnalyticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(AuthorizationPolicies.AdminOnly)
            .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapGet("/overview", async (IAdminAnalyticsService analytics, CancellationToken cancellationToken) =>
            Results.Ok(await analytics.GetOverviewAsync(cancellationToken)));
    }
}
