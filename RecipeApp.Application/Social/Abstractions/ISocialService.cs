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

    /// <summary>Idempotent unsave. NotFound when the recipe isn't visible to the caller.</summary>
    Task<SocialResult<bool>> UnsaveRecipeAsync(Guid recipeId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's saved recipes, SavedAt DESC keyset-paged. Saved recipes that are
    /// soft-deleted or no longer visible are silently omitted (chat-suggestion convention).
    /// </summary>
    Task<RecipeListResponse> GetSavedRecipesAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<SocialResult<CommentResponse>> AddCommentAsync(Guid recipeId, string content, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<SocialResult<CommentListResponse>> GetCommentsAsync(Guid recipeId, KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Comment-author-only edit. Forbidden for anyone else who can see it.</summary>
    Task<SocialResult<CommentResponse>> UpdateCommentAsync(Guid commentId, string content, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Allowed for the comment's author OR the recipe's author (decision I6).</summary>
    Task<SocialResult<bool>> DeleteCommentAsync(Guid commentId, Guid currentUserId, CancellationToken cancellationToken = default);

    // --- cp02: graph + profiles ---------------------------------------------------------

    /// <summary>Idempotent follow. NotFound for an unknown target. Self-follow is rejected at the endpoint (400).</summary>
    Task<SocialResult<bool>> FollowUserAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>Idempotent unfollow. NotFound for an unknown target.</summary>
    Task<SocialResult<bool>> UnfollowUserAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<SocialResult<FollowListResponse>> GetFollowersAsync(Guid targetUserId, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default);

    Task<SocialResult<FollowListResponse>> GetFollowingAsync(Guid targetUserId, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Public profile with counts. RecipeCount counts only recipes the caller can see.</summary>
    Task<SocialResult<UserProfileResponse>> GetUserProfileAsync(Guid targetUserId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>An author's recipes visible to the caller (rule 1 composed first), keyset-paged.</summary>
    Task<SocialResult<RecipeListResponse>> GetUserRecipesAsync(Guid targetUserId, KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default);

    // --- cp03: feed ---------------------------------------------------------------------

    /// <summary>
    /// Pull-based feed: Public recipes by followed authors (source "following"), or the
    /// cold-start fallback of recent Public recipes by others (source "discover") when the
    /// caller follows nobody. CreatedAt DESC keyset, social envelope per item.
    /// </summary>
    Task<FeedListResponse> GetFeedAsync(KeysetCursor? cursor, int limit, Guid currentUserId, CancellationToken cancellationToken = default);
}
