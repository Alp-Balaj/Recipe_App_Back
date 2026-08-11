using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RecipeApp.Application.Common;
using RecipeApp.Application.Events;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social;
using RecipeApp.Application.Social.Abstractions;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.Infrastructure.Social;

public class SocialService : ISocialService
{
    private readonly ApplicationDbContext _db;
    private readonly IContentModerationQueue _moderationQueue;
    private readonly IAppEventLogger _events;
    private readonly ILogger<SocialService> _logger;

    // Feed redesign (2026-08-09): how many makers ride the "N made this" row. The design
    // overlaps three avatars before the count, so fetching more would be rows nobody paints —
    // and the CAP is what keeps the nested projection cheap on a recipe hundreds have cooked.
    private const int RecentMakerLimit = 3;

    public SocialService(
        ApplicationDbContext db,
        IContentModerationQueue moderationQueue,
        IAppEventLogger events,
        ILogger<SocialService> logger)
    {
        _db = db;
        _moderationQueue = moderationQueue;
        _events = events;
        _logger = logger;
    }

    // --- cp01: interactions -------------------------------------------------------------

    public async Task<SocialResult<bool>> LikeRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await VisibleRecipeAuthorAsync(recipeId, currentUserId, cancellationToken);
        if (authorId is null)
        {
            return SocialResult<bool>.NotFound();
        }

        var alreadyLiked = await _db.Likes.AnyAsync(l => l.UserId == currentUserId && l.RecipeId == recipeId, cancellationToken);
        if (!alreadyLiked)
        {
            _db.Likes.Add(new Like { UserId = currentUserId, RecipeId = recipeId });
            // Gamification: the author earns RecipeReceivedLike (+5) on the first like by
            // someone else. The bump rides the same SaveChanges as the insert, so if this
            // instance loses the concurrent-double-tap race the tracker clear reverts both.
            await AwardAuthorAsync(authorId.Value, currentUserId, RankEvent.RecipeReceivedLike, cancellationToken);
            Notify(authorId.Value, currentUserId, NotificationType.RecipeLiked, recipeId: recipeId);
            await SaveIgnoringDuplicateAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    // ADR-0001 (KAN-11): unliking DESTROYS the caller's own Like row, so — unlike
    // LikeRecipeAsync above — it needs no visibility. Gating it meant an author making the
    // recipe Private or removing it stranded the like permanently: the caller could never
    // retract it, and the author kept the RecipeReceivedLike (+5) for a like nobody could
    // withdraw. The author id is still needed, but only to reverse that award.
    public async Task<SocialResult<bool>> UnlikeRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await RecipeAuthorRegardlessOfAvailabilityAsync(recipeId, cancellationToken);

        // Interaction rows are hard rows (unlike recipes there is nothing to soft-delete);
        // ExecuteDelete is naturally idempotent — deleting a like that isn't there is a no-op.
        var deleted = await _db.Likes
            .Where(l => l.UserId == currentUserId && l.RecipeId == recipeId)
            .ExecuteDeleteAsync(cancellationToken);

        // Gamification (symmetric reversal): a real like->unlike transition subtracts the
        // RecipeReceivedLike award from the author. A repeated/no-op unlike deleted nothing,
        // so it never touches the rank. A null author is defensive rather than reachable
        // (see RecipeAuthorRegardlessOfAvailabilityAsync) — skipping the reversal is the
        // safe half, same as ClearCookedAsync.
        if (deleted > 0 && authorId is Guid author)
        {
            await RevertAuthorAsync(author, currentUserId, RankEvent.RecipeReceivedLike, cancellationToken);
            await WithdrawUnreadNotificationAsync(author, currentUserId, NotificationType.RecipeLiked, cancellationToken, recipeId: recipeId);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<bool>> SaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await VisibleRecipeAuthorAsync(recipeId, currentUserId, cancellationToken);
        if (authorId is null)
        {
            return SocialResult<bool>.NotFound();
        }

        var alreadySaved = await _db.SavedRecipes.AnyAsync(s => s.UserId == currentUserId && s.RecipeId == recipeId, cancellationToken);
        if (!alreadySaved)
        {
            _db.SavedRecipes.Add(new SavedRecipe { UserId = currentUserId, RecipeId = recipeId });
            // Gamification: SavedByOtherUser (+8) to the author on the first save by someone
            // else — same-SaveChanges bump, reverted with the insert on a lost duplicate race.
            await AwardAuthorAsync(authorId.Value, currentUserId, RankEvent.SavedByOtherUser, cancellationToken);
            await SaveIgnoringDuplicateAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    // ADR-0001: unsaving DESTROYS the caller's own row, so unlike SaveRecipeAsync above it
    // does not require the recipe to be visible. Gating it meant an author flipping a recipe
    // to Private stranded every save of it — a row its owner could neither see (the saved
    // list omits it) nor delete (this 404'd). Nothing here reads recipe content, so there is
    // nothing to withhold.
    public async Task<SocialResult<bool>> UnsaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await RecipeAuthorRegardlessOfAvailabilityAsync(recipeId, cancellationToken);

        var deleted = await _db.SavedRecipes
            .Where(s => s.UserId == currentUserId && s.RecipeId == recipeId)
            .ExecuteDeleteAsync(cancellationToken);

        // Gamification (symmetric reversal): a real save->unsave transition subtracts the
        // SavedByOtherUser award from the author; a no-op unsave leaves the rank untouched.
        // The award was made when the recipe was visible and is not undone by it becoming
        // unavailable (removal never touches rank), so withdrawing the save must still
        // withdraw the points — otherwise the author keeps them for a save nobody holds.
        if (deleted > 0 && authorId is Guid author)
        {
            await RevertAuthorAsync(author, currentUserId, RankEvent.SavedByOtherUser, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    public async Task<RecipeListResponse> GetSavedRecipesAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // The Recipe navigation carries the soft-delete query filter, and the visibility
        // predicate composes on top — a saved recipe that was deleted or went non-visible
        // is silently omitted rather than erroring (chat-suggestion convention). Since
        // stream F that includes a friend's FriendsOnly recipe you saved and then fell out
        // of a mutual follow with: it drops out of this list the moment the rule stops
        // holding, because the readable set is recomputed per request, never cached.
        //
        // The rule lives in ONE expression (RecipeVisibilityPolicy) shaped for Recipe, so
        // reaching it from a non-Recipe row is a projection to ids + Contains — a plain
        // EXISTS subquery in SQL — rather than a second hand-written copy of the predicate.
        var visibleRecipeIds = _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .Select(r => r.Id);
        var saved = _db.SavedRecipes
            .Where(s => s.UserId == currentUserId)
            .Where(s => visibleRecipeIds.Contains(s.RecipeId));

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

    // Cooked — the dish list (KAN-4, design docs/superpowers/specs/2026-08-11-cooked-design.md).
    //
    // Shaped after GetSavedRecipesAsync above — same keyset idiom, same limit+1 probe, the same
    // one visibility policy reached as a projected id set. It differs in the one place the two
    // collections genuinely differ, and the difference is the design:
    //
    //   Saved recipes DROP when they stop being visible, because a bookmark of something you
    //   can no longer open is worthless. A COOK is a fact about the user, so an unavailable
    //   dish stays in the list and renders from the title its cook snapshotted (ADR-0001).
    //   Visibility therefore feeds the PROJECTION here — title, image, RecipeAvailable — and
    //   is never a Where. `readable` is composed exactly as CookLogService.Project composes
    //   it, for the same reasons spelled out there.
    public async Task<CookedDishListResponse> GetCookedDishesAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var readable = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(currentUserId));

        // D8, and the reason it is a filter rather than a data fix: RateRecipeAsync has been
        // creating rows with TimesCooked = 0 since 30 July, so Cooked — a record of what you
        // have MADE — would otherwise open listing dishes the user only ever rated.
        var dishes = _db.CookedRecipes
            .Where(cr => cr.UserId == currentUserId)
            .Where(cr => cr.TimesCooked > 0);

        if (cursor is not null)
        {
            var cursorLastCookedAt = cursor.Timestamp;
            var cursorRecipeId = cursor.Id;
            dishes = dishes.Where(cr =>
                cr.LastCookedAt < cursorLastCookedAt
                || (cr.LastCookedAt == cursorLastCookedAt && cr.RecipeId.CompareTo(cursorRecipeId) < 0));
        }

        // Project LAST, after Where/OrderBy/Take — CookLogService's first rule, and the one
        // that fails at runtime rather than at compile time if it is broken.
        //
        // Every extra is a correlated subquery, never the cr.Recipe navigation: that
        // relationship is required, so projecting through it compiles to an INNER JOIN and
        // silently drops exactly the dishes whose recipe was removed — the ones this list
        // exists to keep. The latest-note pair follows MealPlanService's per-entry cook-time
        // subquery (:89-97) rather than a GroupBy, which EF cannot express here.
        var rows = await dishes
            .OrderByDescending(cr => cr.LastCookedAt)
            .ThenByDescending(cr => cr.RecipeId)
            .Take(limit + 1)
            .Select(cr => new
            {
                cr.RecipeId,
                cr.TimesCooked,
                cr.LastCookedAt,
                cr.Rating,
                // ONE subquery for all three readable facts, not three. Every
                // evaluation of `readable` re-runs RecipeVisibilityPolicy, whose
                // FriendsOnly branch is two EXISTS over UserFollows — so three
                // copies is 150 policy evaluations on a 50-row page where 50 do.
                // Recipe.Title is non-nullable, which is what lets availability
                // be read off the same projection: no row back means no readable
                // recipe.
                Readable = readable
                    .Where(r => r.Id == cr.RecipeId)
                    .Select(r => new { r.Title, r.ImageUrl })
                    .FirstOrDefault(),
                // The fallback title: what the dish was called the last time it was cooked.
                // CookedRecipe has no snapshot of its own, and CookLog's is the only one in
                // the schema — see its RecipeTitle remarks for why it is denormalised.
                SnapshotTitle = _db.CookLogs
                    .Where(cl => cl.UserId == currentUserId && cl.RecipeId == cr.RecipeId)
                    .OrderByDescending(cl => cl.CookedAt)
                    .ThenByDescending(cl => cl.Id)
                    .Select(cl => cl.RecipeTitle)
                    .FirstOrDefault(),
                // D4 — the most recent NON-EMPTY note, and its OWN cook's date. `Note != null`
                // is exactly "carries a note": UpdateNoteAsync is the column's only writer and
                // normalises blank to null on the way in, so a written-then-cleared note falls
                // back to the one before it. Two scalar subqueries rather than one projecting
                // a pair: identical Where and identical total ordering (CookedAt then Id, so
                // same-timestamp cooks cannot disagree), which is what keeps the halves from
                // ever describing different rows.
                LatestNote = _db.CookLogs
                    .Where(cl => cl.UserId == currentUserId && cl.RecipeId == cr.RecipeId && cl.Note != null)
                    .OrderByDescending(cl => cl.CookedAt)
                    .ThenByDescending(cl => cl.Id)
                    .Select(cl => cl.Note)
                    .FirstOrDefault(),
                LatestNoteCookedAt = _db.CookLogs
                    .Where(cl => cl.UserId == currentUserId && cl.RecipeId == cr.RecipeId && cl.Note != null)
                    .OrderByDescending(cl => cl.CookedAt)
                    .ThenByDescending(cl => cl.Id)
                    .Select(cl => (DateTime?)cl.CookedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.LastCookedAt, last.RecipeId).Encode();
        }

        // The cursor above is taken from the last row the SCAN reached, before the
        // renderability filter below — deliberately, so a dropped row cannot make the next
        // page start past a dish that was never returned. A page may therefore come back
        // shorter than `limit` with a cursor still set, which is ordinary for a keyset page
        // with a post-filter and is why the client pages on NextCursor rather than on count.
        var items = rows
            .Where(r => r.Readable is not null || r.SnapshotTitle is not null)
            .Select(r => new CookedDishResponse(
                r.RecipeId,
                // Readable title first, snapshot second: the recipe may have been renamed
                // since, and its current name is what the user will see if they open it.
                r.Readable?.Title ?? r.SnapshotTitle!,
                r.Readable?.ImageUrl,
                r.TimesCooked,
                r.LastCookedAt,
                r.Rating,
                r.LatestNote,
                r.LatestNoteCookedAt,
                // Availability IS "the readable projection came back", so the flag and the
                // withheld image cannot drift apart the way two separate reads could.
                r.Readable is not null))
            .ToList();

        return new CookedDishListResponse(items, nextCursor);
    }

    public async Task<SocialResult<CommentResponse>> AddCommentAsync(Guid recipeId, string content, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await VisibleRecipeAuthorAsync(recipeId, currentUserId, cancellationToken);
        if (authorId is null)
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
        // Gamification: RecipeReceivedComment (+3) to the author when someone else comments.
        // Comments aren't idempotent — each new comment by a non-author awards again (and its
        // deletion reverses). Bump rides the same SaveChanges as the insert.
        await AwardAuthorAsync(authorId.Value, currentUserId, RankEvent.RecipeReceivedComment, cancellationToken);
        Notify(authorId.Value, currentUserId, NotificationType.RecipeCommented, recipeId: recipeId, commentId: comment.Id);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} commented on recipe {RecipeId}.", currentUserId, recipeId);
        await _events.LogAsync(AppEventType.CommentCreated, actorUserId: currentUserId, targetId: comment.Id);

        // Stream X: offer the comment for classification, after the commit and outside the
        // unit of work. Never inline — a comment must post at the speed of a database insert,
        // not at the speed of a model round-trip, and a classifier that is down must not be
        // able to turn "post a comment" into a 500.
        _moderationQueue.TryEnqueue(new ModerationWorkItem(ReportTargetType.Comment, comment.Id));

        var username = await _db.Users
            .Where(u => u.Id == currentUserId)
            .Select(u => u.Username)
            .SingleAsync(cancellationToken);

        // A brand-new comment has no likes yet, so the envelope is known without a query.
        return SocialResult<CommentResponse>.Success(ToCommentResponse(comment, username, likeCount: 0, likedByMe: false));
    }

    public async Task<SocialResult<CommentListResponse>> GetCommentsAsync(Guid recipeId, KeysetCursor? cursor, int limit, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        if (await VisibleRecipeAuthorAsync(recipeId, currentUserId, cancellationToken) is null)
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

        // Guest access: the caller-relative flag collapses to a constant FALSE for an
        // anonymous caller rather than comparing against NULL (same idiom as the recipe
        // envelope below).
        var isAuthenticated = currentUserId is not null;
        var callerId = currentUserId ?? Guid.Empty;

        var rows = await comments
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(limit + 1)
            .Select(c => new CommentResponse(
                c.Id, c.Content, c.CreatedAt, c.UpdatedAt, c.UserId, c.User.Username, c.RecipeId,
                c.Likes.Count(),
                isAuthenticated && c.Likes.Any(l => l.UserId == callerId)))
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
        // The readable set is RecipeVisibilityPolicy's, so a mutual friend can edit their
        // own comment on a FriendsOnly recipe they can now read.
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .SingleOrDefaultAsync(r => r.Id == comment.RecipeId, cancellationToken);
        if (recipe is null)
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

        // Stream X: same reasoning as the recipe edit path — a create-only check is bypassed
        // by posting something bland and editing it afterwards.
        _moderationQueue.TryEnqueue(new ModerationWorkItem(ReportTargetType.Comment, comment.Id));

        var username = await _db.Users
            .Where(u => u.Id == currentUserId)
            .Select(u => u.Username)
            .SingleAsync(cancellationToken);

        // Unlike a freshly-created comment, an edited one may already carry likes — read
        // them rather than reporting a zero the SPA would patch over the real count.
        var likeCount = await _db.CommentLikes.CountAsync(cl => cl.CommentId == commentId, cancellationToken);
        var likedByMe = await _db.CommentLikes.AnyAsync(cl => cl.CommentId == commentId && cl.UserId == currentUserId, cancellationToken);

        return SocialResult<CommentResponse>.Success(ToCommentResponse(comment, username, likeCount, likedByMe));
    }

    public async Task<SocialResult<bool>> DeleteCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.Comments.SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return SocialResult<bool>.NotFound();
        }

        Guid? recipeAuthorId;
        if (comment.UserId == currentUserId)
        {
            // ADR-0001 (KAN-10): deleting the caller's OWN comment destroys only their own
            // row, so — unlike the moderation branch below — it needs no visibility. Gating
            // it meant an author making the recipe Private or removing it stranded every
            // commenter's own writing on their account with no way to take it down: "the
            // note they wrote is their own writing, not the author's". The recipe's author
            // is still needed, but only to reverse the comment's award below.
            recipeAuthorId = await RecipeAuthorRegardlessOfAvailabilityAsync(comment.RecipeId, cancellationToken);
        }
        else
        {
            // Same readable set as UpdateCommentAsync — one policy, both comment paths. This
            // is the genuinely visibility-gated branch: decision I6 lets the recipe's author
            // delete anyone's comment on their own recipe, and that moderation power reaches
            // no further than what they can see.
            var recipe = await _db.Recipes
                .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
                .SingleOrDefaultAsync(r => r.Id == comment.RecipeId, cancellationToken);
            if (recipe is null)
            {
                return SocialResult<bool>.NotFound();
            }

            if (recipe.CreatedByUserId != currentUserId)
            {
                _logger.LogWarning("User {UserId} forbidden from deleting comment {CommentId}.", currentUserId, commentId);
                return SocialResult<bool>.Forbidden();
            }

            recipeAuthorId = recipe.CreatedByUserId;
        }

        _db.Comments.Remove(comment);
        // Gamification (symmetric reversal): removing a comment reverses the +3 the author
        // earned for it. Passing the comment's author as the "acting user" makes the self-
        // guard fire exactly when the award was skipped (author commenting on their own
        // recipe), so a self-comment's deletion never docks the author. A null author is
        // defensive rather than reachable (see RecipeAuthorRegardlessOfAvailabilityAsync);
        // skipping the reversal there is the safe half, same as ClearCookedAsync.
        if (recipeAuthorId is Guid author)
        {
            await RevertAuthorAsync(author, comment.UserId, RankEvent.RecipeReceivedComment, cancellationToken);
        }
        // No explicit notification withdrawal here: Notification.CommentId cascades, so
        // deleting the comment removes every notification about it — the "commented on
        // your recipe" line AND any "liked your comment" ones. That is deliberately
        // stronger than the unread-only rule the unlike/unfollow paths use: those undo an
        // ACTION and leave read history intact, whereas this destroys the SUBJECT, and a
        // notification pointing at a comment that no longer exists is not history, it is a
        // dead link.
        await _db.SaveChangesAsync(cancellationToken);

        return SocialResult<bool>.Success(true);
    }

    // --- open-loops slice 1: comment likes ------------------------------------------------

    public async Task<SocialResult<bool>> LikeCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await VisibleCommentAuthorAsync(commentId, currentUserId, cancellationToken);
        if (authorId is null)
        {
            return SocialResult<bool>.NotFound();
        }

        var alreadyLiked = await _db.CommentLikes.AnyAsync(cl => cl.UserId == currentUserId && cl.CommentId == commentId, cancellationToken);
        if (!alreadyLiked)
        {
            _db.CommentLikes.Add(new CommentLike { UserId = currentUserId, CommentId = commentId });
            // Gamification: the COMMENT's author earns CommentReceivedLike (+1) — not the
            // recipe's author. This is the call site that makes the enum member reachable.
            await AwardAuthorAsync(authorId.Value, currentUserId, RankEvent.CommentReceivedLike, cancellationToken);
            Notify(authorId.Value, currentUserId, NotificationType.CommentLiked, commentId: commentId);
            await SaveIgnoringDuplicateAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    // ADR-0001 (KAN-11), the comment-like counterpart of UnlikeRecipeAsync above: unliking a
    // comment destroys only the caller's own CommentLike row, so it needs no visibility
    // either. VisibleCommentAuthorAsync gates the COMMENT's author through the RECIPE's
    // visibility, so unlike above there is no single existing helper to reuse — this reads
    // the comment's author with no gate of its own, exactly as VisibleCommentAuthorAsync
    // would with the visibility clause removed.
    public async Task<SocialResult<bool>> UnlikeCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await CommentAuthorRegardlessOfAvailabilityAsync(commentId, cancellationToken);

        var deleted = await _db.CommentLikes
            .Where(cl => cl.UserId == currentUserId && cl.CommentId == commentId)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0 && authorId is Guid author)
        {
            await RevertAuthorAsync(author, currentUserId, RankEvent.CommentReceivedLike, cancellationToken);
            await WithdrawUnreadNotificationAsync(author, currentUserId, NotificationType.CommentLiked, cancellationToken, commentId: commentId);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    // --- open-loops slice 1: cooked + rated -----------------------------------------------

    public async Task<SocialResult<CookedRecipeResponse>> MarkCookedAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        if (await VisibleRecipeAuthorAsync(recipeId, currentUserId, cancellationToken) is null)
        {
            return SocialResult<CookedRecipeResponse>.NotFound();
        }

        var now = DateTime.UtcNow;
        var row = await _db.CookedRecipes
            .SingleOrDefaultAsync(cr => cr.UserId == currentUserId && cr.RecipeId == recipeId, cancellationToken);

        if (row is null)
        {
            row = new CookedRecipe
            {
                UserId = currentUserId,
                RecipeId = recipeId,
                TimesCooked = 1,
                FirstCookedAt = now,
                LastCookedAt = now,
            };
            _db.CookedRecipes.Add(row);
        }
        else
        {
            row.TimesCooked += 1;
            row.LastCookedAt = now;
        }

        // Roadmap spec 2: the log is the complete record of every cook, so the recipe-page
        // gesture writes one too — with a null entry ref, because there is no plan context
        // here. Without this row, "clear cooked" and the shopping list's resolution would be
        // reasoning over a log that is missing half the app's cooks.
        //
        // Written inline rather than by calling CookLogService: that method owns its own
        // SaveChanges and its own aggregate bump, and calling it here would double-count.
        var title = await _db.Recipes
            .Where(r => r.Id == recipeId)
            .Select(r => r.Title)
            .SingleAsync(cancellationToken);

        _db.CookLogs.Add(new CookLog
        {
            Id = Guid.NewGuid(),
            UserId = currentUserId,
            RecipeId = recipeId,
            RecipeTitle = title,
            MealPlanEntryId = null,
            CookedAt = now,
        });

        // No rank award: cooking is a private act and awarding it would let anyone farm an
        // author's rank — or their own, by cooking their own recipe — by tapping a button.
        // The award hangs off rating, which at least carries information.
        await _db.SaveChangesAsync(cancellationToken);

        return SocialResult<CookedRecipeResponse>.Success(ToCookedResponse(recipeId, row));
    }

    public async Task<SocialResult<CookedRecipeResponse>> RateRecipeAsync(Guid recipeId, int rating, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await VisibleRecipeAuthorAsync(recipeId, currentUserId, cancellationToken);
        if (authorId is null)
        {
            return SocialResult<CookedRecipeResponse>.NotFound();
        }

        var now = DateTime.UtcNow;
        var row = await _db.CookedRecipes
            .SingleOrDefaultAsync(cr => cr.UserId == currentUserId && cr.RecipeId == recipeId, cancellationToken);

        // Rating without having logged a cook is allowed — you can rate something you cooked
        // before this feature existed. TimesCooked stays 0 to keep the two facts honest.
        var wasUnrated = row?.Rating is null;
        if (row is null)
        {
            row = new CookedRecipe
            {
                UserId = currentUserId,
                RecipeId = recipeId,
                TimesCooked = 0,
                FirstCookedAt = now,
                LastCookedAt = now,
            };
            _db.CookedRecipes.Add(row);
        }

        row.Rating = rating;
        row.RatedAt = now;

        // Gamification: RecipeCookedAndRated (+15) to the author, ONLY on the transition
        // from unrated to rated. Re-rating (5 -> 1 -> 5) must not award again, or rank is
        // farmable by toggling a star.
        //
        // Stream E note (2026-07-31): Recipe.IsAiGenerated now EXISTS, so the "when the
        // generator lands" half of this comment is spent — but the award is deliberately
        // NOT narrowed to flagged recipes, and stream E did not narrow it. Narrowing it
        // alone would silently strip the +15 from every hand-written recipe. What D1
        // actually required is already true: generation awards nothing on creation
        // (RecipeGenerationService makes no rank call), and a generated recipe first scores
        // here, when somebody cooked it and said what they thought.
        //
        // Stream H closed that out on 2026-08-06 by RENAMING the member —
        // AiRecipeCookedAndRated -> RecipeCookedAndRated. E's note reached for "a sibling
        // RankEvent for the non-AI case", which would have been the wrong shape: there was
        // never an AI case and a non-AI case, only one event whose name claimed otherwise.
        // The name now says what the line below has always done.
        if (wasUnrated)
        {
            await AwardAuthorAsync(authorId.Value, currentUserId, RankEvent.RecipeCookedAndRated, cancellationToken);
        }

        await SaveIgnoringDuplicateAsync(cancellationToken);

        return SocialResult<CookedRecipeResponse>.Success(ToCookedResponse(recipeId, row));
    }

    // ADR-0001, the destroying half of the rule that gates MarkCookedAsync and
    // RateRecipeAsync above: clearing a dish removes only the caller's own rows, so it must
    // keep working after the recipe becomes unavailable. A cook is a fact about the user,
    // and the note they wrote is their own writing — an author withdrawing their recipe
    // must not leave that stranded, visible nowhere and deletable nowhere.
    public async Task<SocialResult<CookedRecipeResponse>> ClearCookedAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var authorId = await RecipeAuthorRegardlessOfAvailabilityAsync(recipeId, cancellationToken);

        var row = await _db.CookedRecipes
            .SingleOrDefaultAsync(cr => cr.UserId == currentUserId && cr.RecipeId == recipeId, cancellationToken);

        if (row is not null)
        {
            var hadRating = row.Rating is not null;
            _db.CookedRecipes.Remove(row);

            // Symmetric reversal, and only when an award was actually made: a row that was
            // never rated never awarded, so removing it must not dock the author. The null
            // author branch is defensive rather than reachable: removal is a SOFT delete, and
            // a hard one would have taken this row with it (CookedRecipes cascades on the
            // recipe FK) — or been refused outright, since CookLogs restricts it. If a row
            // ever does outlive its recipe, skipping the reversal is the safe half: the
            // author keeps points they earned, which beats docking the wrong person.
            if (hadRating && authorId is Guid author)
            {
                await RevertAuthorAsync(author, currentUserId, RankEvent.RecipeCookedAndRated, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        // Deliberately wide, and stated in the spec as such: "I have never cooked this" must
        // mean the same thing on the recipe page, the plan and the shopping list. Plan-linked
        // rows go too, so any shopping group they were resolving stops resolving on the next
        // read. If this proves too blunt in practice the fix is a narrower "un-log one cook"
        // gesture, which CookLog already supports — not a quieter delete here.
        //
        // Unconditional — outside the `if (row is not null)` above, not inside it — because a
        // user can hold CookLog rows with no CookedRecipe aggregate (e.g. the aggregate was
        // already cleared once and the log was left behind before this change existed); a
        // delete gated on the aggregate existing would strand exactly those rows.
        await _db.CookLogs
            .Where(cl => cl.UserId == currentUserId && cl.RecipeId == recipeId)
            .ExecuteDeleteAsync(cancellationToken);

        return SocialResult<CookedRecipeResponse>.Success(new CookedRecipeResponse(recipeId, 0, null, null));
    }

    // F1 resolution (I3 revisited for the single-recipe case, 2026-07-19): the same
    // envelope projection GetFeedAsync rides — correlated subqueries, one round trip,
    // live counts — folded into the visibility check itself so a recipe the caller can't
    // see (or a soft-deleted one, via the global filter) is a NotFound, never a Forbidden.
    public async Task<SocialResult<RecipeSocialResponse>> GetRecipeSocialAsync(Guid recipeId, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        // Guest access: an anonymous caller (null id) sees Public recipes only and never a
        // caller-relative flag. RecipeVisibilityPolicy owns the row filter (its own explicit
        // null branch); isAuthenticated remains a parameterized constant in the SQL for the
        // flags below, so the membership subqueries collapse to FALSE for guests instead of
        // comparing = NULL.
        var isAuthenticated = currentUserId is not null;
        var callerId = currentUserId ?? Guid.Empty;

        // Anonymous projection then client-side construction, the same idiom BuildFeedPageAsync
        // uses: the nested makers list has to come back as a plain shape EF can materialize,
        // and the parity test below pins that both paths build the identical envelope.
        var envelope = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .Where(r => r.Id == recipeId)
            .Select(r => new
            {
                Author = new UserSummaryResponse(r.CreatedByUserId, r.CreatedByUser.Username, r.CreatedByUser.ProfileImageUrl),
                LikeCount = r.Likes.Count(),
                CommentCount = r.Comments.Count(),
                LikedByMe = isAuthenticated && r.Likes.Any(l => l.UserId == callerId),
                SavedByMe = isAuthenticated && r.SavedByUsers.Any(s => s.UserId == callerId),
                // AVG over no rows is SQL NULL, which is exactly the "nobody has rated this"
                // signal — the cast to double? is what keeps it from collapsing to 0.
                AverageRating = r.CookedBy.Where(c => c.Rating != null).Average(c => (double?)c.Rating),
                RatingCount = r.CookedBy.Count(c => c.Rating != null),
                CookedByMe = isAuthenticated && r.CookedBy.Any(c => c.UserId == callerId),
                // Guest callers carry Guid.Empty, which matches no row, so this is null for
                // them without needing the isAuthenticated guard the booleans use.
                MyRating = r.CookedBy.Where(c => c.UserId == callerId).Select(c => c.Rating).FirstOrDefault(),
                MadeItCount = r.CookedBy.Count(),
                RecentMakers = r.CookedBy
                    .OrderByDescending(c => c.LastCookedAt)
                    .Take(RecentMakerLimit)
                    .Select(c => new UserSummaryResponse(c.UserId, c.User.Username, c.User.ProfileImageUrl))
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return envelope is null
            ? SocialResult<RecipeSocialResponse>.NotFound()
            : SocialResult<RecipeSocialResponse>.Success(new RecipeSocialResponse(
                envelope.Author,
                envelope.LikeCount,
                envelope.CommentCount,
                envelope.LikedByMe,
                envelope.SavedByMe,
                envelope.AverageRating,
                envelope.RatingCount,
                envelope.CookedByMe,
                envelope.MyRating,
                envelope.MadeItCount,
                envelope.RecentMakers));
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
            // The followed user is the recipient; no recipe or comment context applies.
            Notify(targetUserId, currentUserId, NotificationType.UserFollowed);
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

        var removed = await _db.UserFollows
            .Where(f => f.FollowerId == currentUserId && f.FollowingId == targetUserId)
            .ExecuteDeleteAsync(cancellationToken);

        // Only a real follow -> unfollow transition withdraws anything; a no-op unfollow
        // leaves an unrelated earlier notification alone.
        if (removed > 0)
        {
            await WithdrawUnreadNotificationAsync(targetUserId, currentUserId, NotificationType.UserFollowed, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return SocialResult<bool>.Success(true);
    }

    public async Task<SocialResult<FollowListResponse>> GetFollowersAsync(Guid targetUserId, Guid? viewerId, string? q, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<FollowListResponse>.NotFound();
        }

        var follows = _db.UserFollows.Where(f => f.FollowingId == targetUserId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Escape backslash FIRST, then the two LIKE metacharacters — reversing the order
            // would double-escape the escapes. Same treatment as IngredientCatalogueService.
            var pattern = "%" + q.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_") + "%";
            follows = follows.Where(f => EF.Functions.ILike(f.Follower.Username, pattern, "\\"));
        }

        if (cursor is not null)
        {
            var cursorFollowedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            follows = follows.Where(f =>
                f.FollowedAt < cursorFollowedAt
                || (f.FollowedAt == cursorFollowedAt && f.FollowerId.CompareTo(cursorId) < 0));
        }

        // Hoisted out of the expression tree: a captured bool short-circuits the EXISTS
        // entirely for anonymous callers, and Guid.Empty keeps the comparison non-nullable.
        var hasViewer = viewerId.HasValue;
        var viewer = viewerId ?? Guid.Empty;

        // Same caller-relative rule as GetUserProfileAsync — a row that advertises a number
        // the previewed profile then contradicts is worse than no number at all.
        var visibleRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(viewerId));

        var rows = await follows
            .OrderByDescending(f => f.FollowedAt)
            .ThenByDescending(f => f.FollowerId)
            .Take(limit + 1)
            .Select(f => new
            {
                f.FollowedAt,
                Item = new FollowListItemResponse(
                    f.FollowerId,
                    f.Follower.Username,
                    f.Follower.ProfileImageUrl,
                    hasViewer && _db.UserFollows.Any(x => x.FollowerId == viewer && x.FollowingId == f.FollowerId),
                    visibleRecipes.Count(r => r.CreatedByUserId == f.FollowerId)),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.FollowedAt, last.Item.Id).Encode();
        }

        return SocialResult<FollowListResponse>.Success(
            new FollowListResponse(rows.Select(r => r.Item).ToList(), nextCursor));
    }

    public async Task<SocialResult<FollowListResponse>> GetFollowingAsync(Guid targetUserId, Guid? viewerId, string? q, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<FollowListResponse>.NotFound();
        }

        var follows = _db.UserFollows.Where(f => f.FollowerId == targetUserId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_") + "%";
            follows = follows.Where(f => EF.Functions.ILike(f.Following.Username, pattern, "\\"));
        }

        if (cursor is not null)
        {
            var cursorFollowedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            follows = follows.Where(f =>
                f.FollowedAt < cursorFollowedAt
                || (f.FollowedAt == cursorFollowedAt && f.FollowingId.CompareTo(cursorId) < 0));
        }

        // Hoisted out of the expression tree: a captured bool short-circuits the EXISTS
        // entirely for anonymous callers, and Guid.Empty keeps the comparison non-nullable.
        var hasViewer = viewerId.HasValue;
        var viewer = viewerId ?? Guid.Empty;

        // Same caller-relative rule as GetUserProfileAsync — a row that advertises a number
        // the previewed profile then contradicts is worse than no number at all.
        var visibleRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(viewerId));

        var rows = await follows
            .OrderByDescending(f => f.FollowedAt)
            .ThenByDescending(f => f.FollowingId)
            .Take(limit + 1)
            .Select(f => new
            {
                f.FollowedAt,
                Item = new FollowListItemResponse(
                    f.FollowingId,
                    f.Following.Username,
                    f.Following.ProfileImageUrl,
                    hasViewer && _db.UserFollows.Any(x => x.FollowerId == viewer && x.FollowingId == f.FollowingId),
                    visibleRecipes.Count(r => r.CreatedByUserId == f.FollowingId)),
            })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.FollowedAt, last.Item.Id).Encode();
        }

        return SocialResult<FollowListResponse>.Success(
            new FollowListResponse(rows.Select(r => r.Item).ToList(), nextCursor));
    }

    public async Task<SocialResult<UserProfileResponse>> GetUserProfileAsync(Guid targetUserId, Guid? currentUserId, CancellationToken cancellationToken = default)
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
        // Guest access is RecipeVisibilityPolicy's explicit null branch — Public only.
        // Since stream F the count a mutual friend sees includes this author's FriendsOnly
        // recipes, and it MUST, or the profile would advertise a number its own recipe list
        // (GetUserRecipesAsync, same policy) then contradicts.
        var visibleRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(currentUserId));
        var recipeCount = await visibleRecipes
            .CountAsync(r => r.CreatedByUserId == targetUserId, cancellationToken);
        var followedByMe = currentUserId is not null && await _db.UserFollows.AnyAsync(
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
            followedByMe,
            user.DefaultRecipeVisibility,
            user.DietaryRestrictions,
            user.CuisinePreferences));
    }

    public async Task<SocialResult<UserProfileResponse>> UpdateProfileAsync(UpdateProfileRequest request, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        if (user is null)
        {
            // The token authenticated a user that no longer exists — treat as not found.
            return SocialResult<UserProfileResponse>.NotFound();
        }

        var newUsername = request.Username.Trim();

        // Username uniqueness (case-sensitive, same as register), excluding the caller so a
        // no-op save of the same name is allowed. The DB unique index is the race backstop.
        if (newUsername != user.Username)
        {
            var taken = await _db.Users.AnyAsync(
                u => u.Username == newUsername && u.Id != currentUserId, cancellationToken);
            if (taken)
            {
                _logger.LogInformation("Profile update rejected: username already taken.");
                return SocialResult<UserProfileResponse>.Conflict();
            }
        }

        user.Username = newUsername;
        // Empty strings clear the optional fields to null (mirrors register's null defaults).
        user.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
        user.ProfileImageUrl = string.IsNullOrWhiteSpace(request.ProfileImageUrl) ? null : request.ProfileImageUrl.Trim();
        user.DefaultRecipeVisibility = request.DefaultRecipeVisibility;
        // Distinct(), because the list reaches the AI prompts as a comma-joined sentence and
        // "Vegan, Vegan" reads as emphasis the user did not intend. A new list rather than a
        // mutation of the request's: the entity must not alias a DTO the caller still holds.
        user.DietaryRestrictions = request.DietaryRestrictions is null
            ? []
            : request.DietaryRestrictions.Distinct().ToList();
        // Stream K, on the same terms as the line above — Distinct(), and a new list rather
        // than the request's.
        user.CuisinePreferences = request.CuisinePreferences is null
            ? []
            : request.CuisinePreferences.Distinct().ToList();

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a username race between the pre-check and save.
            _db.ChangeTracker.Clear();
            return SocialResult<UserProfileResponse>.Conflict();
        }

        return await GetUserProfileAsync(currentUserId, currentUserId, cancellationToken);
    }

    public async Task<SocialResult<UserProfileResponse>> CompleteOnboardingAsync(CompleteOnboardingRequest request, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        if (user is null)
        {
            return SocialResult<UserProfileResponse>.NotFound();
        }

        // Only the three fields the wizard owns. Note what is NOT here: username, bio, photo
        // and visibility are untouched, which is the whole reason this is not PUT /users/me.
        // Distinct() and a fresh list for the same reasons UpdateProfileAsync gives.
        user.CuisinePreferences = request.CuisinePreferences is null
            ? []
            : request.CuisinePreferences.Distinct().ToList();
        user.DietaryRestrictions = request.DietaryRestrictions is null
            ? []
            : request.DietaryRestrictions.Distinct().ToList();

        // Stamped on every completion INCLUDING a skip — see the entity. Overwritten rather
        // than set-once, so a second run (a user reopening the wizard) is harmless.
        user.OnboardingCompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "User {UserId} completed onboarding with {CuisineCount} cuisine preferences and {RestrictionCount} restrictions.",
            currentUserId, user.CuisinePreferences.Count, user.DietaryRestrictions.Count);

        return await GetUserProfileAsync(currentUserId, currentUserId, cancellationToken);
    }

    public async Task<SocialResult<RecipeListResponse>> GetUserRecipesAsync(Guid targetUserId, KeysetCursor? cursor, int limit, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, cancellationToken))
        {
            return SocialResult<RecipeListResponse>.NotFound();
        }

        // Visibility rule 1: the visibility predicate composes FIRST, then the author
        // filter — same discipline as GET /recipes so this endpoint can't widen anything.
        // Guest access is the policy's explicit Public-only branch. A mutual friend browsing
        // this profile sees the author's FriendsOnly recipes here (stream F, D6); a one-way
        // follower in either direction, and a stranger, see exactly the Public ones.
        var visibleRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(currentUserId));
        var recipes = visibleRecipes.Where(r => r.CreatedByUserId == targetUserId);

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

    public async Task<FeedListResponse> GetFeedAsync(KeysetCursor? cursor, int limit, Guid? currentUserId, FeedScope? scope = null, CancellationToken cancellationToken = default)
    {
        // Guest access (plan §3.3): an anonymous caller has no follow graph and no "self"
        // to exclude. ForYou/omitted scope degrades to "recent Public recipes"; an explicit
        // Following scope answers an empty page (defensive — the client gates the tab, but
        // a crafted request must not 500).
        if (currentUserId is not Guid callerId)
        {
            if (scope == FeedScope.Following)
            {
                return new FeedListResponse([], null, "following");
            }

            var guestRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(null));
            var guestSource = scope == FeedScope.ForYou ? "forYou" : "discover";
            return await BuildFeedPageAsync(guestRecipes, guestSource, cursor, limit, null, cancellationToken);
        }

        var followedIds = _db.UserFollows
            .Where(f => f.FollowerId == callerId)
            .Select(f => f.FollowingId);

        // Pull-based (plan decision): recipes are queried at request time from the followed
        // set — no fan-out-on-write table. Every branch below composes the SAME shared
        // visibility policy and then narrows; none of them writes its own predicate.
        //
        // Stream F (D6) changes what that policy admits, and the feed inherits it: a
        // FriendsOnly recipe reaches this caller's feed when the two of you follow EACH
        // OTHER. A one-way follow does not — which is why the old note here ("rule 1 is NOT
        // widened by following") had to be rewritten rather than deleted: following alone
        // still buys nothing, it is the follow-BACK that completes the rule.
        //
        // The `!= callerId` and `followedIds.Contains(...)` clauses are audience narrowing,
        // not visibility: they keep your own recipes out of a feed of other people's work.
        // A mutual friend is by definition someone you follow, so their FriendsOnly recipes
        // pass the Following branch's author filter without any special-casing.
        var visible = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(callerId));
        IQueryable<Recipe> recipes;
        string source;
        if (scope == FeedScope.ForYou)
        {
            // The everyone-feed, requested explicitly (the client's "For You" tab) —
            // same query as the cold-start discover fallback, but available regardless
            // of the caller's follow count.
            recipes = visible.Where(r => r.CreatedByUserId != callerId);
            source = "forYou";
        }
        else if (scope == FeedScope.Following)
        {
            // Followed authors only, no fallback: an empty follow graph (or quiet
            // follows) is an empty page, which the client renders as a follow prompt.
            recipes = visible.Where(r => followedIds.Contains(r.CreatedByUserId));
            source = "following";
        }
        else if (await followedIds.AnyAsync(cancellationToken))
        {
            recipes = visible.Where(r => followedIds.Contains(r.CreatedByUserId));
            source = "following";
        }
        else
        {
            // Cold start: a caller following nobody sees recent visible recipes by others,
            // labeled so the client can frame it as Discover rather than a followed feed.
            // Following nobody means no mutual follow exists, so this is Public-by-others
            // in practice — by the policy, not by a second hand-written predicate.
            recipes = visible.Where(r => r.CreatedByUserId != callerId);
            source = "discover";
        }

        return await BuildFeedPageAsync(recipes, source, cursor, limit, callerId, cancellationToken);
    }

    // The shared page-building tail of GetFeedAsync: keyset paging + the social envelope.
    // callerId null = anonymous — the caller-relative flags are compile-time false and the
    // membership subqueries collapse to FALSE in SQL (isAuthenticated is parameterized).
    private async Task<FeedListResponse> BuildFeedPageAsync(
        IQueryable<Recipe> recipes,
        string source,
        KeysetCursor? cursor,
        int limit,
        Guid? callerId,
        CancellationToken cancellationToken)
    {

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            recipes = recipes.Where(r =>
                r.CreatedAt < cursorCreatedAt
                || (r.CreatedAt == cursorCreatedAt && r.Id.CompareTo(cursorId) < 0));
        }

        var isAuthenticated = callerId is not null;
        var callerIdValue = callerId ?? Guid.Empty;

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
                LikedByMe = isAuthenticated && r.Likes.Any(l => l.UserId == callerIdValue),
                SavedByMe = isAuthenticated && r.SavedByUsers.Any(s => s.UserId == callerIdValue),
                AverageRating = r.CookedBy.Where(c => c.Rating != null).Average(c => (double?)c.Rating),
                RatingCount = r.CookedBy.Count(c => c.Rating != null),
                CookedByMe = isAuthenticated && r.CookedBy.Any(c => c.UserId == callerIdValue),
                MyRating = r.CookedBy.Where(c => c.UserId == callerIdValue).Select(c => c.Rating).FirstOrDefault(),
                // Feed redesign: the "N made this" row. Count is every cook; RecentMakers is
                // only the handful the avatars can hold — see FeedItemResponse for why the
                // count is rows, not the sum of TimesCooked.
                MadeItCount = r.CookedBy.Count(),
                RecentMakers = r.CookedBy
                    .OrderByDescending(c => c.LastCookedAt)
                    .Take(RecentMakerLimit)
                    .Select(c => new UserSummaryResponse(c.UserId, c.User.Username, c.User.ProfileImageUrl))
                    .ToList(),
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
                r.SavedByMe,
                r.AverageRating,
                r.RatingCount,
                r.CookedByMe,
                r.MyRating,
                r.MadeItCount,
                r.RecentMakers))
            .ToList();

        return new FeedListResponse(items, nextCursor, source);
    }

    // --- feed redesign (2026-08-09): the activity strip ----------------------------------

    public async Task<FeedActivityListResponse> GetFeedActivityAsync(int limit, Guid? currentUserId, FeedScope? scope = null, CancellationToken cancellationToken = default)
    {
        // Anonymous callers have no follow graph and no "self" to exclude, and the strip is
        // explicitly about people you have a relationship with — so a guest gets nothing
        // rather than a stranger's shoulder-surf of who liked what.
        if (currentUserId is not Guid callerId)
        {
            return new FeedActivityListResponse([]);
        }

        var visible = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(callerId));

        // Whose activity. Following (the default) narrows to the follow graph; ForYou opens
        // it to everyone else. Either way the caller's own actions are excluded — a strip
        // that told you what you just saved would be a mirror, not a signal.
        IQueryable<Guid> actorIds;
        if (scope == FeedScope.ForYou)
        {
            actorIds = _db.Users.Where(u => u.Id != callerId).Select(u => u.Id);
        }
        else
        {
            actorIds = _db.UserFollows.Where(f => f.FollowerId == callerId).Select(f => f.FollowingId);
        }

        // Four sources, four queries, merged in memory. A UNION would be one round trip, but
        // these tables are keyed differently (Recipe.CreatedAt vs Like.CreatedAt vs
        // SavedRecipe.SavedAt vs CookedRecipe.LastCookedAt) and each row still has to be
        // ordered by its own column before the cap, so the union would be over four
        // already-sorted subqueries anyway. `limit` bounds every leg, so the merge is over at
        // most 4*limit rows and the final Take is what makes the answer correct: whichever
        // leg holds the newest rows wins them, no leg is starved by a fixed per-leg quota.
        var posted = await visible
            .Where(r => actorIds.Contains(r.CreatedByUserId))
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(r => new ActivityRow(
                new UserSummaryResponse(r.CreatedByUserId, r.CreatedByUser.Username, r.CreatedByUser.ProfileImageUrl),
                FeedActivityKind.Posted,
                r.Id,
                r.Title,
                r.CreatedAt))
            .ToListAsync(cancellationToken);

        var liked = await _db.Likes
            .Where(l => actorIds.Contains(l.UserId) && visible.Any(r => r.Id == l.RecipeId))
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(l => new ActivityRow(
                new UserSummaryResponse(l.UserId, l.User.Username, l.User.ProfileImageUrl),
                FeedActivityKind.Liked,
                l.RecipeId,
                l.Recipe.Title,
                l.CreatedAt))
            .ToListAsync(cancellationToken);

        var saved = await _db.SavedRecipes
            .Where(s => actorIds.Contains(s.UserId) && visible.Any(r => r.Id == s.RecipeId))
            .OrderByDescending(s => s.SavedAt)
            .Take(limit)
            .Select(s => new ActivityRow(
                new UserSummaryResponse(s.UserId, s.User.Username, s.User.ProfileImageUrl),
                FeedActivityKind.Saved,
                s.RecipeId,
                s.Recipe.Title,
                s.SavedAt))
            .ToListAsync(cancellationToken);

        var cooked = await _db.CookedRecipes
            .Where(c => actorIds.Contains(c.UserId) && visible.Any(r => r.Id == c.RecipeId))
            .OrderByDescending(c => c.LastCookedAt)
            .Take(limit)
            .Select(c => new ActivityRow(
                new UserSummaryResponse(c.UserId, c.User.Username, c.User.ProfileImageUrl),
                FeedActivityKind.Cooked,
                c.RecipeId,
                c.Recipe.Title,
                c.LastCookedAt))
            .ToListAsync(cancellationToken);

        var items = posted
            .Concat(liked)
            .Concat(saved)
            .Concat(cooked)
            .OrderByDescending(a => a.OccurredAt)
            // One actor doing three things in a row would fill the whole strip with one
            // person; one row per actor keeps it a picture of the kitchen, not of one cook.
            .DistinctBy(a => a.Actor.Id)
            .Take(limit)
            .Select(a => new FeedActivityResponse(a.Actor, a.Kind, a.RecipeId, a.RecipeTitle, a.OccurredAt))
            .ToList();

        return new FeedActivityListResponse(items);
    }

    // The in-memory merge shape for GetFeedActivityAsync's four legs. A named record rather
    // than an anonymous type because the legs are separate queries whose results have to
    // Concat into one sequence.
    private sealed record ActivityRow(
        UserSummaryResponse Actor,
        FeedActivityKind Kind,
        Guid RecipeId,
        string RecipeTitle,
        DateTime OccurredAt);

    // --- helpers ------------------------------------------------------------------------

    // Visibility rule 2 folded into the author lookup: returns the recipe's author id when
    // the caller may interact with it, else null. "May interact" is exactly "may read" —
    // the shared RecipeVisibilityPolicy — so liking, saving, commenting on and rating a
    // mutual friend's FriendsOnly recipe all became possible in one edit, and all stay
    // impossible for a one-way follower. Soft-deleted rows are excluded by the global query
    // filter, so a hidden/missing recipe reads as null — callers turn that into NotFound,
    // never Forbidden. The author id is also what the gamification award/revert path needs,
    // so this one query serves both. Guest access: a null caller (anonymous comment reads)
    // takes the policy's Public-only branch; write paths keep passing a real Guid, which
    // widens to Guid? implicitly.
    private Task<Guid?> VisibleRecipeAuthorAsync(Guid recipeId, Guid? currentUserId, CancellationToken cancellationToken) =>
        _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .Where(r => r.Id == recipeId)
            .Select(r => (Guid?)r.CreatedByUserId)
            .SingleOrDefaultAsync(cancellationToken);

    // The counterpart for ADR-0001's DESTRUCTIVE writes (UnsaveRecipeAsync,
    // ClearCookedAsync): the recipe's author whether or not the caller may still read the
    // recipe, and whether or not it has been soft-deleted. Deliberately takes no caller id
    // — there is no access decision here to make. It is NOT an authorization bypass: the
    // only thing the id is used for is reversing an award on the author's rank, a number
    // the caller never sees and cannot influence beyond withdrawing their own row. No call
    // site may return it, or leak the fact that a row exists, to the caller.
    //
    // IgnoreQueryFilters is what lets the soft-delete case work at all; without it a
    // withdrawn recipe reads as null and the author silently keeps points for a save or
    // rating that no longer exists. Null therefore means the row is genuinely gone (a hard
    // delete or an id that never existed), and callers skip the reversal rather than fail.
    private Task<Guid?> RecipeAuthorRegardlessOfAvailabilityAsync(Guid recipeId, CancellationToken cancellationToken) =>
        _db.Recipes
            .IgnoreQueryFilters()
            .Where(r => r.Id == recipeId)
            .Select(r => (Guid?)r.CreatedByUserId)
            .SingleOrDefaultAsync(cancellationToken);

    // Gamification award: stage a rank increase on the recipe's author for a social event.
    // Never awards an author for acting on their own recipe (authorId == actingUserId), and
    // only stages the change — the caller's SaveChanges commits it alongside the interaction.
    private async Task AwardAuthorAsync(Guid authorId, Guid actingUserId, RankEvent rankEvent, CancellationToken cancellationToken)
    {
        if (authorId == actingUserId)
        {
            return;
        }

        var author = await _db.Users.SingleOrDefaultAsync(u => u.Id == authorId, cancellationToken);
        if (author is not null)
        {
            author.CookingRank = RankingService.NewRank(author.CookingRank, rankEvent);
        }
    }

    // Symmetric to AwardAuthorAsync: stage the reversal of a previously-granted award. The
    // same self-guard means an action that never awarded (own-recipe) is never docked.
    private async Task RevertAuthorAsync(Guid authorId, Guid actingUserId, RankEvent rankEvent, CancellationToken cancellationToken)
    {
        if (authorId == actingUserId)
        {
            return;
        }

        var author = await _db.Users.SingleOrDefaultAsync(u => u.Id == authorId, cancellationToken);
        if (author is not null)
        {
            author.CookingRank = RankingService.RevertRank(author.CookingRank, rankEvent);
        }
    }

    // --- open-loops slice 3: notification fan-out ----------------------------------------
    //
    // Staged, never saved here — the caller's SaveChanges commits the notification in the
    // same transaction as the interaction that caused it, so a like and its notification
    // cannot half-commit. Exactly the contract AwardAuthorAsync already has, and the reason
    // these are private helpers rather than a second injected service.
    //
    // The self-guard is the same one the awards use: acting on your own content notifies
    // nobody. It is checked here rather than at each call site so a future call site cannot
    // forget it.
    private void Notify(
        Guid recipientId,
        Guid actorId,
        NotificationType type,
        Guid? recipeId = null,
        Guid? commentId = null)
    {
        if (recipientId == actorId)
        {
            return;
        }

        _db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientId = recipientId,
            ActorId = actorId,
            Type = type,
            RecipeId = recipeId,
            CommentId = commentId,
        });
    }

    // The reverse transition: an unlike/unfollow withdraws the notification it created —
    // but only if it is still UNREAD. Once someone has seen "X liked your recipe", that
    // happened; deleting it would rewrite history and make the unread count drift against
    // a list the user already read. Staged like Notify, committed by the caller.
    private async Task WithdrawUnreadNotificationAsync(
        Guid recipientId,
        Guid actorId,
        NotificationType type,
        CancellationToken cancellationToken,
        Guid? recipeId = null,
        Guid? commentId = null)
    {
        if (recipientId == actorId)
        {
            return;
        }

        var pending = await _db.Notifications
            .Where(n => n.RecipientId == recipientId
                && n.ActorId == actorId
                && n.Type == type
                && n.RecipeId == recipeId
                && n.CommentId == commentId
                && n.ReadAt == null)
            .ToListAsync(cancellationToken);

        _db.Notifications.RemoveRange(pending);
    }

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

    // The comment-like counterpart to VisibleRecipeAuthorAsync: returns the COMMENT's author
    // id when the caller may interact with it, else null. Visibility is the recipe's — a
    // comment under a soft-deleted (query-filtered) or non-visible recipe reads as null, so
    // callers turn it into NotFound. The author id is what the award/revert path needs, so
    // one query serves both, exactly as it does for recipes. The readable recipe set is
    // projected to ids and matched with Contains rather than restating the policy for
    // Comment: one predicate, one place to get wrong.
    private Task<Guid?> VisibleCommentAuthorAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken)
    {
        var visibleRecipeIds = _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .Select(r => r.Id);
        return _db.Comments
            .Where(c => c.Id == commentId && visibleRecipeIds.Contains(c.RecipeId))
            .Select(c => (Guid?)c.UserId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    // The counterpart for ADR-0001's DESTRUCTIVE write on a comment (UnlikeCommentAsync):
    // the comment's author whether or not its recipe is visible to the caller, or has been
    // soft-deleted. Comments carry no query filter of their own (unlike Recipe), so — unlike
    // RecipeAuthorRegardlessOfAvailabilityAsync — there is no IgnoreQueryFilters to add: a
    // comment under a soft-deleted recipe is still a live row here.
    private Task<Guid?> CommentAuthorRegardlessOfAvailabilityAsync(Guid commentId, CancellationToken cancellationToken) =>
        _db.Comments
            .Where(c => c.Id == commentId)
            .Select(c => (Guid?)c.UserId)
            .SingleOrDefaultAsync(cancellationToken);

    private static CookedRecipeResponse ToCookedResponse(Guid recipeId, CookedRecipe row) =>
        new(recipeId, row.TimesCooked, row.Rating, row.TimesCooked > 0 ? row.LastCookedAt : null);

    private static CommentResponse ToCommentResponse(Comment comment, string authorUsername, int likeCount, bool likedByMe) =>
        new(comment.Id, comment.Content, comment.CreatedAt, comment.UpdatedAt, comment.UserId, authorUsername, comment.RecipeId, likeCount, likedByMe);
}
