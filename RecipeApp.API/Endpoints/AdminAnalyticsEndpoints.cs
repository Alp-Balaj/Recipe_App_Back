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

        group.MapGet("/users", async (string? search, string? status, string? sort, int? page, int? pageSize,
            IAdminAnalyticsService analytics, CancellationToken cancellationToken) =>
        {
            var statusFilter = AdminUserStatusFilter.All;
            if (!string.IsNullOrEmpty(status) && (!Enum.TryParse(status, true, out statusFilter) || !Enum.IsDefined(statusFilter)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"'{status}' is not a valid status filter."] });
            }
            var sortOrder = AdminUserSort.Newest;
            if (!string.IsNullOrEmpty(sort) && (!Enum.TryParse(sort, true, out sortOrder) || !Enum.IsDefined(sortOrder)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sort"] = [$"'{sort}' is not a valid sort."] });
            }
            var effectivePage = page ?? 1;
            if (effectivePage < 1)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["page must be a positive integer."] });
            }
            var effectiveSize = Math.Min(Math.Max(pageSize ?? 20, 1), 50);
            return Results.Ok(await analytics.GetUsersAsync(search, statusFilter, sortOrder, effectivePage, effectiveSize, cancellationToken));
        });
    }
}
