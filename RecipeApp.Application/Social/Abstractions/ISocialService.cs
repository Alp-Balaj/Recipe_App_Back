using RecipeApp.Application.Common;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;

namespace RecipeApp.Application.Social.Abstractions;

// Application-service seam for the social-feed lane (social-feed plan, cp01–03): recipe
// interactions (like/save/comment), the follow graph + public profiles, and the feed.
// Same plain-service pattern as IRecipeService/IChatService; RankEvent wiring is
// deliberately absent (07-gamification fires from these call sites later).
public interface ISocialService
{
    // --- cp01: interactions -------------------------------------------------------------

    /// <summary>Idempotent like. NotFound when the recipe isn't visible to the caller.</summary>
    Task<SocialResult<bool>> LikeRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Idempotent unlike. NotFound when the recipe isn't visible to the caller.</summary>
    Task<SocialResult<bool>> UnlikeRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Idempotent save. NotFound when the recipe isn't visible to the caller.</summary>
    Task<SocialResult<bool>> SaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent unsave. Requires NO visibility (ADR-0001): it destroys the caller's own
    /// row, so it keeps working after the author makes the recipe Private or removes it —
    /// otherwise the save is an orphan its owner can neither see nor delete. Always Success,
    /// including for a recipe that never existed, so the answer cannot be read as "this
    /// recipe is out there somewhere and you're not allowed to see it".
    /// </summary>
    Task<SocialResult<bool>> UnsaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's saved recipes, SavedAt DESC keyset-paged. Saved recipes that are
    /// soft-deleted or no longer visible are silently omitted (chat-suggestion convention).
    /// </summary>
    Task<RecipeListResponse> GetSavedRecipesAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<SocialResult<CommentResponse>> AddCommentAsync(Guid recipeId, string content, Guid currentUserId, CancellationToken cancellationToken = default);

    // Guest access (guest-access plan §3.2): read methods take a NULLABLE caller id — null
    // means an anonymous caller: Public-only visibility, caller-relative flags all false.
    Task<SocialResult<CommentListResponse>> GetCommentsAsync(Guid recipeId, KeysetCursor? cursor, int limit, Guid? currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Comment-author-only edit. Forbidden for anyone else who can see it.</summary>
    Task<SocialResult<CommentResponse>> UpdateCommentAsync(Guid commentId, string content, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Allowed for the comment's author OR the recipe's author (decision I6).</summary>
    Task<SocialResult<bool>> DeleteCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default);

    // --- open-loops slice 1: comment likes ------------------------------------------------

    /// <summary>
    /// Idempotent like on a comment; awards CommentReceivedLike (+1) to the comment's author
    /// on the first like by someone else. Visibility resolves through the comment's recipe,
    /// so a comment under a recipe the caller can't see is NotFound, never Forbidden.
    /// </summary>
    Task<SocialResult<bool>> LikeCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Idempotent unlike; reverses the award only on a real like -> unlike transition.</summary>
    Task<SocialResult<bool>> UnlikeCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default);

    // --- open-loops slice 1: cooked + rated -----------------------------------------------

    /// <summary>
    /// Logs that the caller cooked this recipe: inserts the row with TimesCooked = 1, or
    /// increments it and bumps LastCookedAt. Cooking alone earns the author nothing — only
    /// rating does — so no rank event fires here.
    /// </summary>
    Task<SocialResult<CookedRecipeResponse>> MarkCookedAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the caller's 1-5 rating, creating the row if they never logged a cook. Awards
    /// RecipeCookedAndRated (+15) to the author ONLY on the null -> rated transition, so
    /// re-rating cannot farm points.
    /// </summary>
    Task<SocialResult<CookedRecipeResponse>> RateRecipeAsync(Guid recipeId, int rating, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the caller's cooked/rated row entirely (the undo path for a mis-tap), and
    /// reverses the award if a rating had been given. Idempotent. Like
    /// <see cref="UnsaveRecipeAsync"/>, and in contrast to <see cref="MarkCookedAsync"/> and
    /// <see cref="RateRecipeAsync"/> above, this requires NO visibility (ADR-0001): a cook is
    /// a fact about the user, so an author withdrawing the recipe must not strand the record
    /// of it.
    /// </summary>
    Task<SocialResult<CookedRecipeResponse>> ClearCookedAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The feed's social envelope for one recipe (F1 / I3-single-recipe revisit): author,
    /// live counts, caller-relative flags. Visibility matches GET /recipes/{id} — a recipe
    /// the caller can't see (or that is soft-deleted) is NotFound, never Forbidden (rule 2).
    /// Works for the recipe's own author — the whole point of F1.
    /// </summary>
    Task<SocialResult<RecipeSocialResponse>> GetRecipeSocialAsync(Guid recipeId, Guid? currentUserId, CancellationToken cancellationToken = default);

    // --- cp02: graph + profiles ---------------------------------------------------------

    /// <summary>Idempotent follow. NotFound for an unknown target. Self-follow is rejected at the endpoint (400).</summary>
    Task<SocialResult<bool>> FollowUserAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Idempotent unfollow. NotFound for an unknown target.</summary>
    Task<SocialResult<bool>> UnfollowUserAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Followers of <paramref name="targetUserId"/>, FollowedAt DESC keyset.
    /// <paramref name="viewerId"/> is the caller (null when anonymous) and makes both
    /// FollowedByMe and RecipeCount caller-relative. <paramref name="q"/> filters by
    /// username, case-insensitive contains; null or whitespace means no filter.
    /// </summary>
    Task<SocialResult<FollowListResponse>> GetFollowersAsync(Guid targetUserId, Guid? viewerId, string? q, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Who <paramref name="targetUserId"/> follows. Same parameter contract as GetFollowersAsync.</summary>
    Task<SocialResult<FollowListResponse>> GetFollowingAsync(Guid targetUserId, Guid? viewerId, string? q, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Public profile with counts. RecipeCount counts only recipes the caller can see.</summary>
    Task<SocialResult<UserProfileResponse>> GetUserProfileAsync(Guid targetUserId, Guid? currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller updates their own account (account settings / Edit profile): username,
    /// bio, profile image, default recipe visibility. Conflict when the new username is
    /// already taken by another user. Returns the refreshed profile on success.
    /// </summary>
    Task<SocialResult<UserProfileResponse>> UpdateProfileAsync(UpdateProfileRequest request, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The post-register wizard's one write (stream K): cuisine preferences and dietary
    /// restrictions, plus the stamp that marks onboarding answered. Idempotent — running it
    /// twice simply overwrites both lists. Empty lists are the SKIP case and still stamp,
    /// which is what stops the wizard being raised again.
    /// </summary>
    Task<SocialResult<UserProfileResponse>> CompleteOnboardingAsync(CompleteOnboardingRequest request, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>An author's recipes visible to the caller (rule 1 composed first), keyset-paged.</summary>
    Task<SocialResult<RecipeListResponse>> GetUserRecipesAsync(Guid targetUserId, KeysetCursor? cursor, int limit, Guid? currentUserId, CancellationToken cancellationToken = default);

    // --- cp03: feed ---------------------------------------------------------------------

    /// <summary>
    /// Pull-based feed, CreatedAt DESC keyset, social envelope per item. Scope selects the
    /// mode: ForYou = recent Public recipes by others; Following = followed authors only
    /// (no fallback — empty when they've posted nothing). Null scope keeps the original
    /// behavior: Public recipes by followed authors (source "following"), falling back to
    /// the cold-start discover feed (source "discover") when the caller follows nobody.
    /// Anonymous caller (null): ForYou/omitted = all recent Public recipes; an explicit
    /// Following scope returns an empty page rather than throwing (guest-access plan §3.3).
    /// </summary>
    Task<FeedListResponse> GetFeedAsync(KeysetCursor? cursor, int limit, Guid? currentUserId, FeedScope? scope = null, CancellationToken cancellationToken = default);

    // --- feed redesign (2026-08-09): the activity strip ----------------------------------

    /// <summary>
    /// The newest interactions (posted / liked / saved / cooked) on recipes the caller may
    /// see, newest first. Scope selects WHOSE activity: Following (the default) = users the
    /// caller follows; ForYou = anyone but the caller. Never the caller's own activity —
    /// the strip is about other people's kitchens.
    /// <para>
    /// Uncursored by design (see FeedActivityResponse): <paramref name="limit"/> caps the
    /// whole answer. An anonymous caller has no follow graph and gets an empty list.
    /// </para>
    /// </summary>
    Task<FeedActivityListResponse> GetFeedActivityAsync(int limit, Guid? currentUserId, FeedScope? scope = null, CancellationToken cancellationToken = default);
}
