using RecipeApp.Application.Moderation.Dtos;

namespace RecipeApp.Application.Moderation.Abstractions;

// Admin Rework, stream BE-A: read-only aggregation beside the moderation-only IAdminService
// (decision D5's separation carries forward — this is a second, disjoint admin service, not
// an extension of that one). Every method here reads only; nothing mutates or audits.
public interface IAdminAnalyticsService
{
    Task<AdminOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<AdminUserListResponse> GetUsersAsync(string? search, AdminUserStatusFilter status, AdminUserSort sort, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<AdminUserUsageResponse?> GetUserUsageAsync(Guid userId, CancellationToken cancellationToken = default); // null => 404
}
