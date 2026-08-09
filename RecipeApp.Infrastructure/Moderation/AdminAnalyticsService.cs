using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Moderation;

// Admin Rework, stream BE-A: read-only aggregation beside AdminService (decision D5's
// separate-service split carries forward). Nothing here mutates or audits — every method is
// a query over content AdminService already owns the write side of.
public class AdminAnalyticsService : IAdminAnalyticsService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AdminAnalyticsService> _logger;

    public AdminAnalyticsService(ApplicationDbContext db, ILogger<AdminAnalyticsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AdminOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var dayStartUtc = now.Date;

        var users = await _db.Users
            .GroupBy(_ => 1)
            .Select(g => new AdminUserCounts(
                g.Count(),
                g.Count(u => u.IsBanned),
                g.Count(u => u.SuspendedUntilUtc != null && u.SuspendedUntilUtc > now),
                g.Count(u => u.Role == UserRole.Admin)))
            .SingleOrDefaultAsync(cancellationToken) ?? new AdminUserCounts(0, 0, 0, 0);

        // Live count via the global filter; hidden needs the filter off.
        var liveRecipes = await _db.Recipes.CountAsync(cancellationToken);
        var hiddenRecipes = await _db.Recipes.IgnoreQueryFilters().CountAsync(r => r.IsDeleted, cancellationToken);
        var comments = await _db.Comments.CountAsync(cancellationToken);

        var reportRows = await _db.Reports
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var reports = new AdminReportCounts(
            reportRows.SingleOrDefault(r => r.Status == ReportStatus.Open)?.Count ?? 0,
            reportRows.SingleOrDefault(r => r.Status == ReportStatus.Resolved)?.Count ?? 0,
            reportRows.SingleOrDefault(r => r.Status == ReportStatus.Dismissed)?.Count ?? 0);

        var laneRows = await _db.AiUsageRecords
            .Where(r => r.CreatedAt >= dayStartUtc)
            .GroupBy(r => r.Lane)
            .Select(g => new AdminAiLaneUsage(g.Key, g.Count(), g.Sum(r => (long)r.TotalTokens)))
            .ToListAsync(cancellationToken);
        // Zero-fill so the dashboard's lane table is stable (spec: all 7 lanes, zeros included).
        var allLanes = new[] { AiUsageLanes.Chat, AiUsageLanes.RecipeGeneration, AiUsageLanes.MealPlanProposal,
            AiUsageLanes.CookAssistant, AiUsageLanes.ContentModeration, AiUsageLanes.Import, AiUsageLanes.FoodScan };
        var byLane = allLanes
            .Select(lane => laneRows.SingleOrDefault(l => l.Lane == lane) ?? new AdminAiLaneUsage(lane, 0, 0))
            .ToList();

        var topUsers = await _db.AiUsageRecords
            .Where(r => r.CreatedAt >= dayStartUtc && r.UserId != SystemUsers.ModerationId)
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, Tokens = g.Sum(r => (long)r.TotalTokens) })
            .OrderByDescending(x => x.Tokens)
            .Take(5)
            .Join(_db.Users, x => x.UserId, u => u.Id, (x, u) => new AdminAiTopUser(u.Id, u.Username, x.Tokens))
            .ToListAsync(cancellationToken);

        return new AdminOverviewResponse(users, new AdminRecipeCounts(liveRecipes, hiddenRecipes),
            new AdminCommentCounts(comments), reports,
            new AdminAiToday(byLane.Sum(l => l.Calls), byLane.Sum(l => l.Tokens), byLane, topUsers));
    }
}
