using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Common;
using RecipeApp.Application.Moderation;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Moderation;

// Governor (stream D, decision D5 2026-07-30): THE separate admin service. Every query in
// this file may see private and soft-deleted content — each IgnoreQueryFilters() below is
// one of the "explicitly reviewed" admin reads that decision traded for leaving the
// user-facing "public OR mine" predicates untouched. Nothing here is reachable except
// through the /admin endpoints, which sit behind the AdminOnly policy.
//
// Every action appends one AuditLogEntry in the SAME SaveChanges as the act, so the log
// and the action commit atomically. The log is append-only by construction: this is the
// only code that writes it and no code updates or deletes it.
public class AdminService : IAdminService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserSecurityStateService _securityState;
    private readonly ILogger<AdminService> _logger;

    public AdminService(ApplicationDbContext db, IUserSecurityStateService securityState, ILogger<AdminService> logger)
    {
        _db = db;
        _securityState = securityState;
        _logger = logger;
    }

    public async Task<AdminReportListResponse> GetReportsAsync(
        ReportStatus? status, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var reports = status is ReportStatus s
            ? _db.Reports.Where(r => r.Status == s)
            : _db.Reports;

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            reports = reports.Where(r =>
                r.CreatedAt < cursorCreatedAt
                || (r.CreatedAt == cursorCreatedAt && r.Id.CompareTo(cursorId) < 0));
        }

        // limit + 1 keyset paging, same convention as every list — unchanged from before
        // Task 14. The reporter/resolver usernames ride the projection; the report row
        // itself carries the target snapshot. TargetAuthorId/Username resolve the ONE FK
        // that is actually set (Recipe.CreatedByUserId, Comment.UserId, or TargetUserId
        // itself) to whoever the report is effectively against.
        var projected = reports
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .Select(r => new
            {
                Report = r,
                ReporterUsername = r.Reporter.Username,
                ReporterImageUrl = r.Reporter.ProfileImageUrl,
                ResolvedByUsername = r.ResolvedByUser != null ? r.ResolvedByUser.Username : null,
                TargetAuthorId = r.RecipeId != null ? r.Recipe!.CreatedByUserId
                    : r.CommentId != null ? r.Comment!.UserId
                    : r.TargetUserId!.Value,
                TargetAuthorUsername = r.RecipeId != null ? r.Recipe!.CreatedByUser.Username
                    : r.CommentId != null ? r.Comment!.User.Username
                    : r.TargetUser!.Username,
            });

        // Second Select so the correlated count can reference TargetAuthorId above rather
        // than repeating the three-way FK resolution inline. All statuses count — the
        // spec-mandated behavior that a triaged report never shrinks the count the queue
        // shows for the same target author.
        var rows = await projected
            .Select(p => new
            {
                p.Report,
                p.ReporterUsername,
                p.ReporterImageUrl,
                p.ResolvedByUsername,
                p.TargetAuthorId,
                p.TargetAuthorUsername,
                TotalReportsAgainst = _db.Reports.Count(x =>
                    x.TargetUserId == p.TargetAuthorId
                    || (x.RecipeId != null && x.Recipe!.CreatedByUserId == p.TargetAuthorId)
                    || (x.CommentId != null && x.Comment!.UserId == p.TargetAuthorId)),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1].Report;
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        return new AdminReportListResponse(
            rows.Select(r => new AdminReportListItem(
                ReportService.ToResponse(r.Report, r.ReporterUsername, r.ReporterImageUrl, r.ResolvedByUsername),
                new UserSummaryResponse(r.Report.ReporterId, r.ReporterUsername, r.ReporterImageUrl),
                new AdminReportTargetAuthor(r.TargetAuthorId, r.TargetAuthorUsername, r.TotalReportsAgainst)))
                .ToList(),
            nextCursor);
    }

    public async Task<ModerationResult<ReportResponse>> ResolveReportAsync(
        Guid reportId, Guid adminUserId, string? note, bool dismiss, CancellationToken cancellationToken = default)
    {
        var report = await _db.Reports.SingleOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return ModerationResult<ReportResponse>.NotFound();
        }

        // Triage happens once. A second resolve is a 409, not a silent overwrite — the
        // queue is shared state and two admins can race on the same row.
        if (report.Status != ReportStatus.Open)
        {
            return ModerationResult<ReportResponse>.Conflict();
        }

        report.Status = dismiss ? ReportStatus.Dismissed : ReportStatus.Resolved;
        report.ResolvedAtUtc = DateTime.UtcNow;
        report.ResolvedByUserId = adminUserId;
        report.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        Append(adminUserId, dismiss ? AuditAction.ReportDismissed : AuditAction.ReportResolved, report.Id, report.ResolutionNote);
        await _db.SaveChangesAsync(cancellationToken);

        var usernames = await _db.Users
            .Where(u => u.Id == report.ReporterId || u.Id == adminUserId)
            .Select(u => new { u.Id, u.Username, u.ProfileImageUrl })
            .ToListAsync(cancellationToken);
        var reporter = usernames.Single(u => u.Id == report.ReporterId);
        var resolver = usernames.SingleOrDefault(u => u.Id == adminUserId);

        return ModerationResult<ReportResponse>.Success(
            ReportService.ToResponse(report, reporter.Username, reporter.ProfileImageUrl, resolver?.Username));
    }

    public async Task<ModerationResult<AdminRecipeResponse>> GetRecipeAsync(Guid recipeId, CancellationToken cancellationToken = default)
    {
        // D5 admin read: IgnoreQueryFilters — soft-deleted (hidden) recipes are exactly
        // what a triaging admin needs to see; visibility is not filtered either.
        var recipe = await _db.Recipes
            .IgnoreQueryFilters()
            .Where(r => r.Id == recipeId)
            .Select(r => new AdminRecipeResponse(
                r.Id, r.Title, r.Description, r.Visibility, r.IsDeleted, r.DeletedAt, r.CreatedAt,
                new UserSummaryResponse(r.CreatedByUserId, r.CreatedByUser.Username, r.CreatedByUser.ProfileImageUrl)))
            .SingleOrDefaultAsync(cancellationToken);

        return recipe is null
            ? ModerationResult<AdminRecipeResponse>.NotFound()
            : ModerationResult<AdminRecipeResponse>.Success(recipe);
    }

    public async Task<ModerationResult<AdminCommentResponse>> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        // D5 admin read: the recipe's visibility (and soft-delete state) is deliberately
        // not consulted — the comment is the subject here.
        var comment = await _db.Comments
            .Where(c => c.Id == commentId)
            .Select(c => new AdminCommentResponse(
                c.Id, c.Content, c.CreatedAt, c.RecipeId,
                new UserSummaryResponse(c.UserId, c.User.Username, c.User.ProfileImageUrl)))
            .SingleOrDefaultAsync(cancellationToken);

        return comment is null
            ? ModerationResult<AdminCommentResponse>.NotFound()
            : ModerationResult<AdminCommentResponse>.Success(comment);
    }

    public async Task<ModerationResult<AdminUserResponse>> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new AdminUserResponse(
                u.Id, u.Username, u.Email, u.Role, u.IsBanned, u.SuspendedUntilUtc, u.CreatedAt,
                // D5 admin read: counts the user's rows across every visibility (the
                // soft-delete filter still applies — hidden content is not "theirs" in
                // the catalogue sense any more).
                _db.Recipes.Count(r => r.CreatedByUserId == u.Id),
                _db.Reports.Count(r => r.Status == ReportStatus.Open
                    && (r.TargetUserId == u.Id
                        || (r.Recipe != null && r.Recipe.CreatedByUserId == u.Id)
                        || (r.Comment != null && r.Comment.UserId == u.Id)))))
            .SingleOrDefaultAsync(cancellationToken);

        return user is null
            ? ModerationResult<AdminUserResponse>.NotFound()
            : ModerationResult<AdminUserResponse>.Success(user);
    }

    public async Task<ModerationResult<bool>> HideRecipeAsync(Guid recipeId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters so hiding an ALREADY hidden recipe reads its true state and
        // answers Conflict instead of a misleading NotFound.
        var recipe = await _db.Recipes.IgnoreQueryFilters().SingleOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        if (recipe.IsDeleted)
        {
            return ModerationResult<bool>.Conflict();
        }

        // The existing soft delete, reused exactly as the owner path uses it: flag +
        // DeletedAt, no SQL DELETE, interaction history survives, the global filter hides
        // the row from every user-facing read.
        recipe.IsDeleted = true;
        recipe.DeletedAt = DateTime.UtcNow;

        Append(adminUserId, AuditAction.RecipeHidden, recipeId, reason);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Admin {AdminId} hid recipe {RecipeId}.", adminUserId, recipeId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> RestoreRecipeAsync(Guid recipeId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default)
    {
        var recipe = await _db.Recipes.IgnoreQueryFilters().SingleOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        if (!recipe.IsDeleted)
        {
            return ModerationResult<bool>.Conflict();
        }

        recipe.IsDeleted = false;
        recipe.DeletedAt = null;

        Append(adminUserId, AuditAction.RecipeRestored, recipeId, reason);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Admin {AdminId} restored recipe {RecipeId}.", adminUserId, recipeId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> RemoveCommentAsync(Guid commentId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default)
    {
        var comment = await _db.Comments.SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        // Comments have no soft delete, so removal is the same hard delete the owner path
        // performs — including the symmetric rank reversal SocialService.DeleteCommentAsync
        // does: the +3 the comment earned its author goes back, unless it was a self-comment
        // (which never awarded). The recipe is read filter-free: a comment under a hidden
        // recipe must still be removable.
        var recipeAuthorId = await _db.Recipes
            .IgnoreQueryFilters()
            .Where(r => r.Id == comment.RecipeId)
            .Select(r => (Guid?)r.CreatedByUserId)
            .SingleOrDefaultAsync(cancellationToken);

        _db.Comments.Remove(comment);

        if (recipeAuthorId is Guid authorOfRecipe && comment.UserId != authorOfRecipe)
        {
            var commentAuthor = await _db.Users.SingleOrDefaultAsync(u => u.Id == comment.UserId, cancellationToken);
            if (commentAuthor is not null)
            {
                commentAuthor.CookingRank = RankingService.RevertRank(commentAuthor.CookingRank, RankEvent.RecipeReceivedComment);
            }
        }

        Append(adminUserId, AuditAction.CommentRemoved, commentId, reason);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Admin {AdminId} removed comment {CommentId}.", adminUserId, commentId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> SuspendUserAsync(Guid userId, Guid adminUserId, int days, string? reason, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        // Admins are not moderatable through this surface (which also blocks self-
        // suspension) — demote first, deliberately, if it ever comes to that.
        if (user.Role == UserRole.Admin)
        {
            return ModerationResult<bool>.Forbidden();
        }

        user.SuspendedUntilUtc = DateTime.UtcNow.AddDays(days);
        // The revocation check: bump the version so every already-issued token ("tver"
        // claim behind by one) fails OnTokenValidated immediately — no waiting for the
        // suspension read, no living out the JWT's expiry.
        user.TokenVersion++;

        Append(adminUserId, AuditAction.UserSuspended, userId, DetailWithDays(days, reason));
        await _db.SaveChangesAsync(cancellationToken);
        _securityState.Invalidate(userId);

        _logger.LogInformation("Admin {AdminId} suspended user {UserId} for {Days} days.", adminUserId, userId, days);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> UnsuspendUserAsync(Guid userId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        if (user.SuspendedUntilUtc is null || user.SuspendedUntilUtc <= DateTime.UtcNow)
        {
            return ModerationResult<bool>.Conflict();
        }

        user.SuspendedUntilUtc = null;

        Append(adminUserId, AuditAction.UserUnsuspended, userId, detail: null);
        await _db.SaveChangesAsync(cancellationToken);
        _securityState.Invalidate(userId);

        _logger.LogInformation("Admin {AdminId} unsuspended user {UserId}.", adminUserId, userId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> BanUserAsync(Guid userId, Guid adminUserId, string? reason, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        if (user.Role == UserRole.Admin)
        {
            return ModerationResult<bool>.Forbidden();
        }

        if (user.IsBanned)
        {
            return ModerationResult<bool>.Conflict();
        }

        user.IsBanned = true;
        // Decision D3 (production-ready): banning must actually ban. The version bump plus
        // the cache invalidation below kill the user's LIVE sessions on their next request;
        // LoginAsync's IsBanned gate keeps them from opening a new one.
        user.TokenVersion++;

        Append(adminUserId, AuditAction.UserBanned, userId, reason);
        await _db.SaveChangesAsync(cancellationToken);
        _securityState.Invalidate(userId);

        _logger.LogWarning("Admin {AdminId} banned user {UserId}.", adminUserId, userId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> UnbanUserAsync(Guid userId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ModerationResult<bool>.NotFound();
        }

        if (!user.IsBanned)
        {
            return ModerationResult<bool>.Conflict();
        }

        user.IsBanned = false;

        Append(adminUserId, AuditAction.UserUnbanned, userId, detail: null);
        await _db.SaveChangesAsync(cancellationToken);
        _securityState.Invalidate(userId);

        _logger.LogInformation("Admin {AdminId} unbanned user {UserId}.", adminUserId, userId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> PromoteUserAsync(Guid userId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        if (userId == adminUserId)
        {
            return ModerationResult<bool>.Forbidden();
        }
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ModerationResult<bool>.NotFound();
        }
        if (user.Role == UserRole.Admin || user.IsBanned)
        {
            return ModerationResult<bool>.Conflict();
        }

        user.Role = UserRole.Admin;
        // The role rides the JWT: bump + invalidate so the promotion takes effect on next login,
        // and any stale token stops answering as the OLD role immediately.
        user.TokenVersion++;

        Append(adminUserId, AuditAction.AdminPromoted, userId, detail: null);
        await _db.SaveChangesAsync(cancellationToken);
        _securityState.Invalidate(userId);

        _logger.LogWarning("Admin {AdminId} promoted user {UserId} to Admin.", adminUserId, userId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<ModerationResult<bool>> DemoteUserAsync(Guid userId, Guid adminUserId, CancellationToken cancellationToken = default)
    {
        if (userId == adminUserId)
        {
            return ModerationResult<bool>.Forbidden();
        }
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ModerationResult<bool>.NotFound();
        }
        if (user.Role != UserRole.Admin)
        {
            return ModerationResult<bool>.Conflict();
        }

        user.Role = UserRole.User;
        user.TokenVersion++;

        Append(adminUserId, AuditAction.AdminDemoted, userId, detail: null);
        await _db.SaveChangesAsync(cancellationToken);
        _securityState.Invalidate(userId);

        _logger.LogWarning("Admin {AdminId} demoted admin {UserId} to User.", adminUserId, userId);
        return ModerationResult<bool>.Success(true);
    }

    public async Task<AuditLogListResponse> GetAuditLogAsync(
        KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var entries = _db.AuditLog.AsQueryable();

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            entries = entries.Where(a =>
                a.CreatedAt < cursorCreatedAt
                || (a.CreatedAt == cursorCreatedAt && a.Id.CompareTo(cursorId) < 0));
        }

        var rows = await entries
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(limit + 1)
            .Select(a => new { Entry = a, ActorUsername = a.ActorUser.Username })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1].Entry;
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        return new AuditLogListResponse(
            rows.Select(r => new AuditLogEntryResponse(
                r.Entry.Id, r.ActorUsername, r.Entry.Action, r.Entry.TargetId, r.Entry.Detail, r.Entry.CreatedAt)).ToList(),
            nextCursor);
    }

    // The one audit write path. Rides the caller's pending SaveChanges so action and log
    // row commit atomically — an action that fails to record itself fails entirely.
    private void Append(Guid actorUserId, AuditAction action, Guid targetId, string? detail)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            Action = action,
            TargetId = targetId,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
        });
    }

    private static string DetailWithDays(int days, string? reason)
    {
        var label = $"{days} day{(days == 1 ? "" : "s")}";
        return string.IsNullOrWhiteSpace(reason) ? label : $"{label} — {reason.Trim()}";
    }
}
