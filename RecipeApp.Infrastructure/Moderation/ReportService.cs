using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Moderation;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.Infrastructure.Moderation;

// Governor (stream D): the user-facing half of moderation. This service runs UNDER the
// normal visibility rules — a reporter can only report what they can see, and an
// invisible target answers NotFound (rule 2: never confirm hidden content exists). The
// admin half lives in AdminService and never shares queries with this one (decision D5).
public class ReportService : IReportService
{
    private const int SummaryMaxLength = 200;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<ReportService> _logger;

    public ReportService(ApplicationDbContext db, ILogger<ReportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ModerationResult<ReportResponse>> CreateReportAsync(
        CreateReportRequest request, Guid reporterId, CancellationToken cancellationToken = default)
    {
        // Resolve the target under the reporter's visibility, snapshot its summary, and
        // note its owner (own content is Invalid — flagging yourself is a mis-click, and
        // the admin inbox should not carry it).
        Guid? recipeId = null, commentId = null, targetUserId = null;
        Guid ownerId;
        string summary;

        switch (request.TargetType)
        {
            case ReportTargetType.Recipe:
            {
                // Soft-deleted rows are excluded by the global filter; the reportable set is
                // the READABLE set (the shared RecipeVisibilityPolicy) — you can report
                // exactly what you can see, so a mutual friend can report a FriendsOnly
                // recipe they can now open, and nobody can probe for one they cannot.
                // This is the reporter's own read, NOT an admin read: decision D5 keeps
                // AdminService's IgnoreQueryFilters queries out of this policy entirely.
                var recipe = await _db.Recipes
                    .Where(RecipeVisibilityPolicy.VisibleTo(reporterId))
                    .Where(r => r.Id == request.TargetId)
                    .Select(r => new { r.Id, r.CreatedByUserId, r.Title })
                    .SingleOrDefaultAsync(cancellationToken);
                if (recipe is null)
                {
                    return ModerationResult<ReportResponse>.NotFound();
                }

                recipeId = recipe.Id;
                ownerId = recipe.CreatedByUserId;
                summary = Excerpt($"Recipe: {recipe.Title}");
                break;
            }

            case ReportTargetType.Comment:
            {
                // A comment is visible when its recipe is (comments ride the recipe's
                // visibility — same rule VisibleCommentAuthorAsync applies in SocialService,
                // reached the same way: readable recipe ids, then Contains).
                var visibleRecipeIds = _db.Recipes
                    .Where(RecipeVisibilityPolicy.VisibleTo(reporterId))
                    .Select(r => r.Id);
                var comment = await _db.Comments
                    .Where(c => c.Id == request.TargetId && visibleRecipeIds.Contains(c.RecipeId))
                    .Select(c => new { c.Id, c.UserId, c.Content, AuthorUsername = c.User.Username })
                    .SingleOrDefaultAsync(cancellationToken);
                if (comment is null)
                {
                    return ModerationResult<ReportResponse>.NotFound();
                }

                commentId = comment.Id;
                ownerId = comment.UserId;
                summary = Excerpt($"Comment by {comment.AuthorUsername}: {comment.Content}");
                break;
            }

            case ReportTargetType.User:
            {
                var target = await _db.Users
                    .Where(u => u.Id == request.TargetId)
                    .Select(u => new { u.Id, u.Username })
                    .SingleOrDefaultAsync(cancellationToken);
                if (target is null)
                {
                    return ModerationResult<ReportResponse>.NotFound();
                }

                targetUserId = target.Id;
                ownerId = target.Id;
                summary = Excerpt($"User: {target.Username}");
                break;
            }

            default:
                return ModerationResult<ReportResponse>.Invalid();
        }

        if (ownerId == reporterId)
        {
            return ModerationResult<ReportResponse>.Invalid();
        }

        // One OPEN report per (reporter, target): a duplicate is a 409, not a second inbox
        // row. Re-reporting after triage is allowed — that is a new signal, not spam.
        var duplicate = await _db.Reports.AnyAsync(r =>
            r.ReporterId == reporterId
            && r.Status == ReportStatus.Open
            && r.TargetType == request.TargetType
            && r.RecipeId == recipeId
            && r.CommentId == commentId
            && r.TargetUserId == targetUserId,
            cancellationToken);
        if (duplicate)
        {
            return ModerationResult<ReportResponse>.Conflict();
        }

        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = reporterId,
            TargetType = request.TargetType,
            RecipeId = recipeId,
            CommentId = commentId,
            TargetUserId = targetUserId,
            TargetSummary = summary,
            Reason = request.Reason,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim(),
        };

        _db.Reports.Add(report);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "User {UserId} reported {TargetType} {TargetId} ({Reason}).",
            reporterId, request.TargetType, request.TargetId, request.Reason);

        var reporterUsername = await _db.Users
            .Where(u => u.Id == reporterId)
            .Select(u => u.Username)
            .SingleAsync(cancellationToken);

        return ModerationResult<ReportResponse>.Success(ToResponse(report, reporterUsername, null, resolvedByUsername: null));
    }

    private static string Excerpt(string text)
    {
        var flattened = text.ReplaceLineEndings(" ");
        return flattened.Length <= SummaryMaxLength ? flattened : flattened[..SummaryMaxLength];
    }

    // Shared shape with the admin queue — kept internal static so AdminService reuses the
    // mapping without the two services sharing any query.
    internal static ReportResponse ToResponse(Report report, string reporterUsername, string? reporterImageUrl, string? resolvedByUsername)
    {
        return new ReportResponse(
            report.Id,
            report.TargetType,
            report.RecipeId ?? report.CommentId ?? report.TargetUserId,
            report.TargetSummary,
            report.Reason,
            report.Details,
            report.Status,
            report.CreatedAt,
            new UserSummaryResponse(report.ReporterId, reporterUsername, reporterImageUrl),
            report.ResolvedAtUtc,
            resolvedByUsername,
            report.ResolutionNote);
    }
}
