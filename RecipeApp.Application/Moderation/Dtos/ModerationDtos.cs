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
    string? ResolutionNote,
    // Stream X (D20). Appended, and both are additive: Reporter above stays non-nullable and
    // keeps naming a real account for an auto-flag too (SystemUsers.ModerationId), so the
    // frontend's existing src/api/reports.ts contract does not change shape. These two only
    // let the queue TELL the two kinds of row apart and show how sure the classifier was.
    ReportSource Source,
    double? Confidence);

// GET /admin/reports — triage context riding beside each report row (stream BE-C, Task
// 14): who is being reported against, and how many reports (any status) already name
// them, so an admin can spot a repeat target without opening their user record.
public record AdminReportTargetAuthor(Guid Id, string Username, int TotalReportsAgainst);

public record AdminReportListItem(ReportResponse Report, UserSummaryResponse Reporter, AdminReportTargetAuthor TargetAuthor);

public record AdminReportListResponse(IReadOnlyList<AdminReportListItem> Items, string? NextCursor);

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
