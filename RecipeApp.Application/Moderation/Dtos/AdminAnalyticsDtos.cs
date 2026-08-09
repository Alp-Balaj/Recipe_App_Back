using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Moderation.Dtos;

// Admin Rework, spec §5.1–5.3. AdminOverviewResponse reuses the name Task 4 freed.
public record AdminOverviewResponse(AdminUserCounts Users, AdminRecipeCounts Recipes, AdminCommentCounts Comments, AdminReportCounts Reports, AdminAiToday AiToday);
public record AdminUserCounts(int Total, int Banned, int Suspended, int Admins);
public record AdminRecipeCounts(int Total, int Hidden);
public record AdminCommentCounts(int Total);
public record AdminReportCounts(int Open, int Resolved, int Dismissed);
public record AdminAiToday(int Calls, long Tokens, List<AdminAiLaneUsage> ByLane, List<AdminAiTopUser> TopUsers);
public record AdminAiLaneUsage(string Lane, int Calls, long Tokens);
public record AdminAiTopUser(Guid UserId, string Username, long Tokens);

public enum AdminUserStatusFilter { All, Banned, Suspended, Admins }
public enum AdminUserSort { Newest, Tokens }

public record AdminUserListItem(Guid Id, string Username, string Email, UserRole Role, bool IsBanned, DateTime? SuspendedUntilUtc, DateTime CreatedAt, long AllTimeTokens);
public record AdminUserListResponse(List<AdminUserListItem> Items, int Page, int TotalPages, int TotalCount);
public record AdminUserBudget(int CallsRemaining, long TokensRemaining, DateTime ResetsAtUtc);
public record AdminUserUsageResponse(int TodayCalls, long TodayTokens, long AllTimeTokens, AdminUserBudget Budget);
