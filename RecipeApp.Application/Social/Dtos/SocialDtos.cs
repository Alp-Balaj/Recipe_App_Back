using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Social.Dtos;

// Wire contract for the social-feed endpoints (social-feed plan, cp01–03). Shapes shared
// with the frontend — see the plan's "Wire contract" section.

// One request type for both comment-creating and comment-editing bodies (they share the
// { content } shape and one validator), mirroring how chat's SendMessageRequest is shared.
public record CommentRequest(string Content);

public record CommentResponse(
    Guid Id,
    string Content,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    Guid AuthorId,
    string AuthorUsername,
    Guid RecipeId);

public record CommentListResponse(IReadOnlyList<CommentResponse> Items, string? NextCursor);

public record UserSummaryResponse(Guid Id, string Username, string? ProfileImageUrl);

public record FollowListResponse(IReadOnlyList<UserSummaryResponse> Items, string? NextCursor);

public record UserProfileResponse(
    Guid Id,
    string Username,
    string? Bio,
    string? ProfileImageUrl,
    int CookingRank,
    DateTime CreatedAt,
    int FollowerCount,
    int FollowingCount,
    int RecipeCount,
    bool FollowedByMe,
    RecipeVisibility DefaultRecipeVisibility);

// Body of PUT /users/me — the caller updating their own account (account settings /
// Edit profile). Username must stay unique; Bio/ProfileImageUrl clear to null when empty.
public record UpdateProfileRequest(
    string Username,
    string? Bio,
    string? ProfileImageUrl,
    RecipeVisibility DefaultRecipeVisibility);

// The feed's social envelope around each recipe (social-feed plan, cp03). Counts are
// live-computed correlated subqueries — no denormalized counters until measured slow.
public record FeedItemResponse(
    RecipeResponse Recipe,
    UserSummaryResponse Author,
    int LikeCount,
    int CommentCount,
    bool LikedByMe,
    bool SavedByMe);

// Caller-requested feed mode (?scope= on GET /feed, feed-tabs addition 2026-07-22):
// ForYou = recent Public recipes by others (the discover query, on demand);
// Following = followed authors only, NO cold-start fallback (empty is empty).
// A missing scope keeps the original cp03 behavior: following with discover fallback.
public enum FeedScope
{
    ForYou,
    Following,
}

// source: "following" (recipes from followed authors), "discover" (cold-start fallback
// for a caller who follows nobody — recent public recipes by others, clearly labeled),
// or "forYou" (the explicitly requested ?scope=forYou everyone-feed).
public record FeedListResponse(
    IReadOnlyList<FeedItemResponse> Items,
    string? NextCursor,
    string Source);

// F1 resolution (decision I3 revisited for the SINGLE-recipe case, 2026-07-19): the feed's
// social envelope, fetchable per recipe via GET /recipes/{id}/social — FeedItemResponse
// minus the recipe itself. Exists so any surface (in particular the recipe's own author,
// whose posts never appear in their own feed) can read live counts + caller-relative flags
// without a feed-cache hit. GET /recipes list stays envelope-free (the I3 list decision holds).
public record RecipeSocialResponse(
    UserSummaryResponse Author,
    int LikeCount,
    int CommentCount,
    bool LikedByMe,
    bool SavedByMe);
