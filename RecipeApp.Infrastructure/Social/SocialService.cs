using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RecipeApp.Application.Common;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social;
using RecipeApp.Application.Social.Abstractions;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Social;

public class SocialService : ISocialService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<SocialService> _logger;

    public SocialService(ApplicationDbContext db, ILogger<SocialService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // --- cp01: interactions -------------------------------------------------------------

    public async Task<SocialResult<bool>> LikeRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await RecipeVisibleAsync(recipeId, currentUserId, cancellationToken))
        {
            return SocialResult<bool>.NotFound();
        }

        var alreadyLiked = await _db.Likes.AnyAsync(l => l.UserId == currentUserId && l.RecipeId == recipeId, cancellationToken);
        if (!alreadyLiked)
        {
            _db.Likes.Add(new Like { UserId = currentUserId, RecipeId = recipeId });
            await SaveIgnoringDuplicateAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<bool>> UnlikeRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await RecipeVisibleAsync(recipeId, currentUserId, cancellationToken))
        {
            return SocialResult<bool>.NotFound();
        }

        // Interaction rows are hard rows (unlike recipes there is nothing to soft-delete);
        // ExecuteDelete is naturally idempotent — deleting a like that isn't there is a no-op.
        await _db.Likes
            .Where(l => l.UserId == currentUserId && l.RecipeId == recipeId)
            .ExecuteDeleteAsync(cancellationToken);

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<bool>> SaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await RecipeVisibleAsync(recipeId, currentUserId, cancellationToken))
        {
            return SocialResult<bool>.NotFound();
        }

        var alreadySaved = await _db.SavedRecipes.AnyAsync(s => s.UserId == currentUserId && s.RecipeId == recipeId, cancellationToken);
        if (!alreadySaved)
        {
            _db.SavedRecipes.Add(new SavedRecipe { UserId = currentUserId, RecipeId = recipeId });
            await SaveIgnoringDuplicateAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<bool>> UnsaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await RecipeVisibleAsync(recipeId, currentUserId, cancellationToken))
        {
            return SocialResult<bool>.NotFound();
        }

        await _db.SavedRecipes
            .Where(s => s.UserId == currentUserId && s.RecipeId == recipeId)
            .ExecuteDeleteAsync(cancellationToken);

        return SocialResult<bool>.Success(true);
    }

    public async Task<RecipeListResponse> GetSavedRecipesAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // The Recipe navigation carries the soft-delete query filter, and the visibility
        // predicate composes on top — a saved recipe that was deleted or went non-visible
        // is silently omitted rather than erroring (chat-suggestion convention).
        var saved = _db.SavedRecipes
            .Where(s => s.UserId == currentUserId)
            .Where(s => s.Recipe.Visibility == RecipeVisibility.Public || s.Recipe.CreatedByUserId == currentUserId);

        if (cursor is not null)
        {
            var cursorSavedAt = cursor.Timestamp;
            var cursorRecipeId = cursor.Id;
            saved = saved.Where(s =>
                s.SavedAt < cursorSavedAt
                || (s.SavedAt == cursorSavedAt && s.RecipeId.CompareTo(cursorRecipeId) < 0));
        }

        var rows = await saved
            .OrderByDescending(s => s.SavedAt)
            .ThenByDescending(s => s.RecipeId)
            .Take(limit + 1)
            .Select(s => new { s.SavedAt, s.RecipeId, s.Recipe })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.SavedAt, last.RecipeId).Encode();
        }

        return new RecipeListResponse(rows.Select(r => RecipeMapper.ToResponse(r.Recipe)).ToList(), nextCursor);
    }

    public async Task<SocialResult<CommentResponse>> AddCommentAsync(Guid recipeId, string content, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await RecipeVisibleAsync(recipeId, currentUserId, cancellationToken))
        {
            return SocialResult<CommentResponse>.NotFound();
        }

        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            Content = content,
            UserId = currentUserId,
            RecipeId = recipeId,
        };
        _db.Comments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} commented on recipe {RecipeId}.", currentUserId, recipeId);

        var username = await _db.Users
            .Where(u => u.Id == currentUserId)
            .Select(u => u.Username)
            .SingleAsync(cancellationToken);

        return SocialResult<CommentResponse>.Success(ToCommentResponse(comment, username));
    }

    public async Task<SocialResult<CommentListResponse>> GetCommentsAsync(Guid recipeId, KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await RecipeVisibleAsync(recipeId, currentUserId, cancellationToken))
        {
            return SocialResult<CommentListResponse>.NotFound();
        }

        var comments = _db.Comments.Where(c => c.RecipeId == recipeId);

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            comments = comments.Where(c =>
                c.CreatedAt < cursorCreatedAt
                || (c.CreatedAt == cursorCreatedAt && c.Id.CompareTo(cursorId) < 0));
        }

        var rows = await comments
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit + 1)
            .Select(c => new CommentResponse(c.Id, c.Content, c.CreatedAt, c.UpdatedAt, c.UserId, c.User.Username, c.RecipeId))
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        return SocialResult<CommentListResponse>.Success(new CommentListResponse(rows, nextCursor));
    }

    public async Task<SocialResult<CommentResponse>> UpdateCommentAsync(Guid commentId, string content, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.Comments.SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return SocialResult<CommentResponse>.NotFound();
        }

        // The comment's recipe decides visibility: a comment under a soft-deleted recipe
        // (filtered) or a recipe the caller can't see is NotFound, never Forbidden (rule 2).
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == comment.RecipeId, cancellationToken);
        if (recipe is null || (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId))
        {
            return SocialResult<CommentResponse>.NotFound();
        }

        // Editing is comment-author-only — the recipe's author may delete, not rewrite.
        if (comment.UserId != currentUserId)
        {
            _logger.LogWarning("User {UserId} forbidden from editing comment {CommentId}.", currentUserId, commentId);
            return SocialResult<CommentResponse>.Forbidden();
        }

        comment.Content = content;
        comment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var username = await _db.Users
            .Where(u => u.Id == currentUserId)
            .Select(u => u.Username)
            .SingleAsync(cancellationToken);

        return SocialResult<CommentResponse>.Success(ToCommentResponse(comment, username));
    }

    public async Task<SocialResult<bool>> DeleteCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.Comments.SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return SocialResult<bool>.NotFound();
        }

        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == comment.RecipeId, cancellationToken);
        if (recipe is null || (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId))
        {
            return SocialResult<bool>.NotFound();
        }

        // Decision I6: the comment's author OR the recipe's author may delete (Instagram's
        // moderation model). Anyone else who can see it gets Forbidden.
        if (comment.UserId != currentUserId && recipe.CreatedByUserId != currentUserId)
        {
            _logger.LogWarning("User {UserId} forbidden from deleting comment {CommentId}.", currentUserId, commentId);
            return SocialResult<bool>.Forbidden();
        }

        _db.Comments.Remove(comment);
        await _db.SaveChangesAsync(cancellationToken);

        return SocialResult<bool>.Success(true);
    }

    // --- cp02: graph + profiles ---------------------------------------------------------

    public async Task<SocialResult<bool>> FollowUserAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<bool>.NotFound();
        }

        var alreadyFollowing = await _db.UserFollows.AnyAsync(
            f => f.FollowerId == currentUserId && f.FollowingId == targetUserId, cancellationToken);
        if (!alreadyFollowing)
        {
            _db.UserFollows.Add(new UserFollow { FollowerId = currentUserId, FollowingId = targetUserId });
            await SaveIgnoringDuplicateAsync(cancellationToken);
            _logger.LogInformation("User {UserId} followed user {TargetUserId}.", currentUserId, targetUserId);
        }

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<bool>> UnfollowUserAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<bool>.NotFound();
        }

        await _db.UserFollows
            .Where(f => f.FollowerId == currentUserId && f.FollowingId == targetUserId)
            .ExecuteDeleteAsync(cancellationToken);

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<FollowListResponse>> GetFollowersAsync(Guid targetUserId, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<FollowListResponse>.NotFound();
        }

        var follows = _db.UserFollows.Where(f => f.FollowingId == targetUserId);

        if (cursor is not null)
        {
            var cursorFollowedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            follows = follows.Where(f =>
                f.FollowedAt < cursorFollowedAt
                || (f.FollowedAt == cursorFollowedAt && f.FollowerId.CompareTo(cursorId) < 0));
        }

        var rows = await follows
            .OrderByDescending(f => f.FollowedAt)
            .ThenByDescending(f => f.FollowerId)
            .Take(limit + 1)
            .Select(f => new
            {
                f.FollowedAt,
                Summary = new UserSummaryResponse(f.FollowerId, f.Follower.Username, f.Follower.ProfileImageUrl),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.FollowedAt, last.Summary.Id).Encode();
        }

        return SocialResult<FollowListResponse>.Success(
            new FollowListResponse(rows.Select(r => r.Summary).ToList(), nextCursor));
    }

    public async Task<SocialResult<FollowListResponse>> GetFollowingAsync(Guid targetUserId, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<FollowListResponse>.NotFound();
        }

        var follows = _db.UserFollows.Where(f => f.FollowerId == targetUserId);

        if (cursor is not null)
        {
            var cursorFollowedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            follows = follows.Where(f =>
                f.FollowedAt < cursorFollowedAt
                || (f.FollowedAt == cursorFollowedAt && f.FollowingId.CompareTo(cursorId) < 0));
        }

        var rows = await follows
            .OrderByDescending(f => f.FollowedAt)
            .ThenByDescending(f => f.FollowingId)
            .Take(limit + 1)
            .Select(f => new
            {
                f.FollowedAt,
                Summary = new UserSummaryResponse(f.FollowingId, f.Following.Username, f.Following.ProfileImageUrl),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.FollowedAt, last.Summary.Id).Encode();
        }

        return SocialResult<FollowListResponse>.Success(
            new FollowListResponse(rows.Select(r => r.Summary).ToList(), nextCursor));
    }

    public async Task<SocialResult<UserProfileResponse>> GetUserProfileAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return SocialResult<UserProfileResponse>.NotFound();
        }

        var followerCount = await _db.UserFollows.CountAsync(f => f.FollowingId == targetUserId, cancellationToken);
        var followingCount = await _db.UserFollows.CountAsync(f => f.FollowerId == targetUserId, cancellationToken);
        // RecipeCount is caller-relative: rule 1 scoped to this author, so a profile never
        // advertises recipes its viewer can't open (soft-deleted rows already filtered).
        var recipeCount = await _db.Recipes
            .Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == currentUserId)
            .CountAsync(r => r.CreatedByUserId == targetUserId, cancellationToken);
        var followedByMe = await _db.UserFollows.AnyAsync(
            f => f.FollowerId == currentUserId && f.FollowingId == targetUserId, cancellationToken);

        return SocialResult<UserProfileResponse>.Success(new UserProfileResponse(
            user.Id,
            user.Username,
            user.Bio,
            user.ProfileImageUrl,
            user.CookingRank,
            user.CreatedAt,
            followerCount,
            followingCount,
            recipeCount,
            followedByMe));
    }

    public async Task<SocialResult<RecipeListResponse>> GetUserRecipesAsync(Guid targetUserId, KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<RecipeListResponse>.NotFound();
        }

        // Visibility rule 1: the visibility predicate composes FIRST, then the author
        // filter — same discipline as GET /recipes so this endpoint can't widen anything.
        var recipes = _db.Recipes
            .Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == currentUserId)
            .Where(r => r.CreatedByUserId == targetUserId);

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            recipes = recipes.Where(r =>
                r.CreatedAt < cursorCreatedAt
                || (r.CreatedAt == cursorCreatedAt && r.Id.CompareTo(cursorId) < 0));
        }

        var rows = await recipes
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        return SocialResult<RecipeListResponse>.Success(
            new RecipeListResponse(rows.Select(RecipeMapper.ToResponse).ToList(), nextCursor));
    }

    // --- cp03: feed ---------------------------------------------------------------------

    public async Task<FeedListResponse> GetFeedAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var followedIds = _db.UserFollows
            .Where(f => f.FollowerId == currentUserId)
            .Select(f => f.FollowingId);

        // Pull-based (plan decision): recipes are queried at request time from the followed
        // set — no fan-out-on-write table. Rule 1 is NOT widened here: the feed shows
        // followed authors' Public recipes only (FriendsOnly stays owner-only everywhere).
        IQueryable<Recipe> recipes;
        string source;
        if (await followedIds.AnyAsync(cancellationToken))
        {
            recipes = _db.Recipes.Where(r =>
                r.Visibility == RecipeVisibility.Public && followedIds.Contains(r.CreatedByUserId));
            source = "following";
        }
        else
        {
            // Cold start: a caller following nobody sees recent Public recipes by others,
            // labeled so the client can frame it as Discover rather than a followed feed.
            recipes = _db.Recipes.Where(r =>
                r.Visibility == RecipeVisibility.Public && r.CreatedByUserId != currentUserId);
            source = "discover";
        }

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            recipes = recipes.Where(r =>
                r.CreatedAt < cursorCreatedAt
                || (r.CreatedAt == cursorCreatedAt && r.Id.CompareTo(cursorId) < 0));
        }

        // The social envelope rides along as correlated subqueries in the page query —
        // one round trip, live counts, no denormalized counters until measured slow.
        var rows = await recipes
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .Select(r => new
            {
                Recipe = r,
                Author = new UserSummaryResponse(r.CreatedByUserId, r.CreatedByUser.Username, r.CreatedByUser.ProfileImageUrl),
                LikeCount = r.Likes.Count(),
                CommentCount = r.Comments.Count(),
                LikedByMe = r.Likes.Any(l => l.UserId == currentUserId),
                SavedByMe = r.SavedByUsers.Any(s => s.UserId == currentUserId),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.Recipe.CreatedAt, last.Recipe.Id).Encode();
        }

        var items = rows
            .Select(r => new FeedItemResponse(
                RecipeMapper.ToResponse(r.Recipe),
                r.Author,
                r.LikeCount,
                r.CommentCount,
                r.LikedByMe,
                r.SavedByMe))
            .ToList();

        return new FeedListResponse(items, nextCursor, source);
    }

    // --- helpers ------------------------------------------------------------------------

    // Visibility rule 2 as a single existence check: soft-deleted rows are excluded by the
    // global query filter, and a non-public recipe not owned by the caller doesn't "exist"
    // for interaction purposes — callers get NotFound, never Forbidden.
    private Task<bool> RecipeVisibleAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken) =>
        _db.Recipes.AnyAsync(
            r => r.Id == recipeId && (r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == currentUserId),
            cancellationToken);

    // Idempotency under races (plan decision): two concurrent likes/saves/follows both pass
    // the Any() check, and the loser's insert hits the composite-PK unique constraint. That
    // duplicate IS the desired end state, so swallow exactly that error (Postgres 23505) and
    // report success rather than a 500 on a double-tap.
    private async Task SaveIgnoringDuplicateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
        }
    }

    private static CommentResponse ToCommentResponse(Comment comment, string authorUsername) =>
        new(comment.Id, comment.Content, comment.CreatedAt, comment.UpdatedAt, comment.UserId, authorUsername, comment.RecipeId);
}
