using RecipeApp.Application.Common;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Moderation.Abstractions;

// Governor (stream D, decision D5 2026-07-30: SEPARATE ADMIN SERVICE). Every read here is
// an explicitly-reviewed admin query that may see private and soft-deleted content — that
// is its purpose. The user-facing services and their "public OR mine" predicates are never
// touched by, and never call into, anything on this interface. Every mutating method is
// called with the acting admin's id and appends one audit log row atomically with the act.
public interface IAdminService
{
    Task<ReportListResponse> GetReportsAsync(
        ReportStatus? status, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default);

    Task<ModerationResult<ReportResponse>> ResolveReportAsync(
        Guid reportId, Guid adminUserId, string? note, bool dismiss, CancellationToken cancellationToken = default);

    /// <summary>Admin read of any recipe — private and soft-deleted included (D5).</summary>
    Task<ModerationResult<AdminRecipeResponse>> GetRecipeAsync(Guid recipeId, CancellationToken cancellationToken = default);

    /// <summary>Admin read of any comment, regardless of the recipe's visibility (D5).</summary>
    Task<ModerationResult<AdminCommentResponse>> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default);

    Task<ModerationResult<AdminUserResponse>> GetUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Hide via the existing soft delete. Conflict when already hidden.</summary>
    Task<ModerationResult<bool>> HideRecipeAsync(Guid recipeId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Undo an admin hide. Conflict when the recipe is not hidden.</summary>
    Task<ModerationResult<bool>> RestoreRecipeAsync(Guid recipeId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Comments have no soft delete — removal is the same hard delete the owner path uses, rank reversal included.</summary>
    Task<ModerationResult<bool>> RemoveCommentAsync(Guid commentId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default);

    Task<ModerationResult<bool>> SuspendUserAsync(Guid userId, Guid adminUserId, int days, string? reason, CancellationToken cancellationToken = default);

    Task<ModerationResult<bool>> UnsuspendUserAsync(Guid userId, Guid adminUserId, CancellationToken cancellationToken = default);

    Task<ModerationResult<bool>> BanUserAsync(Guid userId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default);

    Task<ModerationResult<bool>> UnbanUserAsync(Guid userId, Guid adminUserId, CancellationToken cancellationToken = default);

    Task<AuditLogListResponse> GetAuditLogAsync(
        KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default);
}
