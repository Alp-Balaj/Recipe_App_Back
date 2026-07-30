using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Moderation.Dtos;

// Wire contract for the report + admin endpoints (stream D governor). Shapes shared with
// the frontend — src/api/reports.ts and src/api/admin.ts mirror these 1:1.

// POST /reports — any authenticated user flagging a recipe, a comment, or a user.
// TargetId is the id of whatever TargetType names; the service resolves it to the right
// FK and snapshots a summary of the content at report time.
public record CreateReportRequest(ReportTargetType TargetType, Guid TargetId, ReportReason Reason, string? Details);

// One report row, as both the reporter's confirmation and the admin queue's item.
// TargetId is nullable because a removed comment's FK nulls out (SetNull) — the
// TargetSummary snapshot keeps the row readable after that.
public record ReportResponse(
    Guid Id,
    ReportTargetType TargetType,
    Guid? TargetId,
    string TargetSummary,
    ReportReason Reason,
    string? Details,
    ReportStatus Status,
    DateTime CreatedAt,
    UserSummaryResponse Reporter,
    DateTime? ResolvedAtUtc,
    string? ResolvedByUsername,
    string? ResolutionNote);

public record ReportListResponse(IReadOnlyList<ReportResponse> Items, string? NextCursor);

// GET /admin/overview — the three honest counts (band 03 part 4: counts, not a chart
// dashboard). TotalRecipes counts non-deleted rows of every visibility; HiddenRecipes
// would be a fourth count and was deliberately left out of the minimal surface.
public record AdminOverviewResponse(int TotalUsers, int TotalRecipes, int OpenReports);

// Bodies of the admin actions. Reason/Note is optional free text that lands in the
// audit log (and, for report triage, on the report row).
public record ResolveReportRequest(string? Note);
public record AdminActionRequest(string? Reason);
public record SuspendUserRequest(int Days, string? Reason);

// Admin reads of content the visibility rules hide (decision D5: these live on the
// SEPARATE admin service, never on the user-facing predicates). The moderation state is
// explicit — an admin needs to see that a recipe is already hidden.
public record AdminRecipeResponse(
    Guid Id,
    string Title,
    string Description,
    RecipeVisibility Visibility,
    bool IsDeleted,
    DateTime? DeletedAt,
    DateTime CreatedAt,
    UserSummaryResponse Author);

public record AdminCommentResponse(
    Guid Id,
    string Content,
    DateTime CreatedAt,
    Guid RecipeId,
    UserSummaryResponse Author);

// GET /admin/users/{id} — the moderation view of an account.
public record AdminUserResponse(
    Guid Id,
    string Username,
    string Email,
    UserRole Role,
    bool IsBanned,
    DateTime? SuspendedUntilUtc,
    DateTime CreatedAt,
    int RecipeCount,
    int OpenReportsAgainst);

public record AuditLogEntryResponse(
    Guid Id,
    string ActorUsername,
    AuditAction Action,
    Guid TargetId,
    string? Detail,
    DateTime CreatedAt);

public record AuditLogListResponse(IReadOnlyList<AuditLogEntryResponse> Items, string? NextCursor);
