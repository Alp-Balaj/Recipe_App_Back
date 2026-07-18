using RecipeApp.Application.Recipes.Dtos;

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
    bool FollowedByMe);

// The feed's social envelope around each recipe (social-feed plan, cp03). Counts are
// live-computed correlated subqueries — no denormalized counters until measured slow.
public record FeedItemResponse(
    RecipeResponse Recipe,
    UserSummaryResponse Author,
    int LikeCount,
    int CommentCount,
    bool LikedByMe,
    bool SavedByMe);

// source: "following" (recipes from followed authors) or "discover" (cold-start fallback
// for a caller who follows nobody — recent public recipes by others, clearly labeled).
public record FeedListResponse(
    IReadOnlyList<FeedItemResponse> Items,
    string? NextCursor,
    string Source);
