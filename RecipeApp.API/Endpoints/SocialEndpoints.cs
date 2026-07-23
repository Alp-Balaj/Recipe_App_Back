using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Common;
using RecipeApp.Application.Social;
using RecipeApp.Application.Social.Abstractions;
using RecipeApp.Application.Social.Dtos;

namespace RecipeApp.API.Endpoints;

// Social-feed endpoints (social-feed plan, cp01–03): recipe interactions, the follow graph +
// profiles, and the feed. Mirrors MapRecipeEndpoints/MapChatEndpoints: manual mapping,
// ValidationFilter for bodies, ClaimsPrincipal -> JWT sub scoping, SocialOutcome switched
// onto status codes. Every group runs under the caller-auth fallback policy and the shared
// "social" rate-limit budget.
public static class SocialEndpoints
{
    // Same page-size policy as GET /recipes: default 20, above-cap clamps to 50, non-positive 400.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapSocialEndpoints(this WebApplication app)
    {
        MapRecipeInteractionEndpoints(app);
        MapCommentEndpoints(app);
        MapUserEndpoints(app);
        MapFeedEndpoint(app);
    }

    private static void MapRecipeInteractionEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/recipes/{recipeId:guid}").RequireRateLimiting(RateLimitPolicies.Social);

        // Idempotent toggles: repeat likes/saves (and unlikes/unsaves of nothing) are 204 —
        // the response reports the end state, not whether a row changed.
        group.MapPost("/likes", async (Guid recipeId, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.LikeRecipeAsync(recipeId, GetUserId(user), cancellationToken);
            return ToNoContent(result);
        });

        group.MapDelete("/likes", async (Guid recipeId, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.UnlikeRecipeAsync(recipeId, GetUserId(user), cancellationToken);
            return ToNoContent(result);
        });

        group.MapPost("/saves", async (Guid recipeId, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.SaveRecipeAsync(recipeId, GetUserId(user), cancellationToken);
            return ToNoContent(result);
        });

        group.MapDelete("/saves", async (Guid recipeId, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.UnsaveRecipeAsync(recipeId, GetUserId(user), cancellationToken);
            return ToNoContent(result);
        });

        group.MapPost("/comments", async (Guid recipeId, CommentRequest request, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.AddCommentAsync(recipeId, request.Content, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Created($"/comments/{result.Value!.Id}", result.Value),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<CommentRequest>>();

        // F1 resolution (I3 revisited, single-recipe only): the feed's envelope for ONE
        // recipe, so surfaces without a feed-cache hit — above all the recipe's own author,
        // who never appears in their own feed — can read counts + caller-relative flags.
        // Same visibility as GET /recipes/{id}: visible → 200, else 404 (rule 2, never 403).
        group.MapGet("/social", async (Guid recipeId, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.GetRecipeSocialAsync(recipeId, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        group.MapGet("/comments", async (Guid recipeId, string? cursor, int? limit, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var result = await social.GetCommentsAsync(recipeId, decodedCursor, effectiveLimit, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });
    }

    private static void MapCommentEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/comments").RequireRateLimiting(RateLimitPolicies.Social);

        group.MapPut("/{commentId:guid}", async (Guid commentId, CommentRequest request, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.UpdateCommentAsync(commentId, request.Content, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                SocialOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<CommentRequest>>();

        group.MapDelete("/{commentId:guid}", async (Guid commentId, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.DeleteCommentAsync(commentId, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.NoContent(),
                SocialOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        });
    }

    private static void MapUserEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/users").RequireRateLimiting(RateLimitPolicies.Social);

        // Literal "me" segment can't collide with the {id:guid}-constrained routes below.
        group.MapGet("/me/saved-recipes", async (string? cursor, int? limit, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var response = await social.GetSavedRecipesAsync(decodedCursor, effectiveLimit, GetUserId(user), cancellationToken);
            return Results.Ok(response);
        });

        // Account settings — the caller edits their own profile. Identity comes from the
        // token (no route param), so this literal "me" route sits with the others above the
        // {id:guid} matchers. 409 when the requested username is already taken.
        group.MapPut("/me", async (UpdateProfileRequest request, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.UpdateProfileAsync(request, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                SocialOutcome.Conflict => Results.Conflict(new { error = "That username is already taken." }),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<UpdateProfileRequest>>();

        group.MapPost("/{id:guid}/follow", async (Guid id, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            if (id == userId)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["id"] = ["You cannot follow yourself."],
                });
            }

            var result = await social.FollowUserAsync(id, userId, cancellationToken);
            return ToNoContent(result);
        });

        group.MapDelete("/{id:guid}/follow", async (Guid id, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.UnfollowUserAsync(id, GetUserId(user), cancellationToken);
            return ToNoContent(result);
        });

        group.MapGet("/{id:guid}/followers", async (Guid id, string? cursor, int? limit, ISocialService social, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var result = await social.GetFollowersAsync(id, decodedCursor, effectiveLimit, cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        group.MapGet("/{id:guid}/following", async (Guid id, string? cursor, int? limit, ISocialService social, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var result = await social.GetFollowingAsync(id, decodedCursor, effectiveLimit, cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        group.MapGet("/{id:guid}", async (Guid id, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await social.GetUserProfileAsync(id, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        group.MapGet("/{id:guid}/recipes", async (Guid id, string? cursor, int? limit, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var result = await social.GetUserRecipesAsync(id, decodedCursor, effectiveLimit, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                SocialOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });
    }

    private static void MapFeedEndpoint(WebApplication app)
    {
        app.MapGet("/feed", async (string? cursor, int? limit, string? scope, ISocialService social, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            // Feed tabs (2026-07-22): ?scope=forYou|following selects the mode explicitly;
            // omitted keeps the original following-with-discover-fallback behavior.
            FeedScope? feedScope = null;
            if (!string.IsNullOrEmpty(scope))
            {
                if (string.Equals(scope, "forYou", StringComparison.OrdinalIgnoreCase))
                {
                    feedScope = FeedScope.ForYou;
                }
                else if (string.Equals(scope, "following", StringComparison.OrdinalIgnoreCase))
                {
                    feedScope = FeedScope.Following;
                }
                else
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["scope"] = ["scope must be 'forYou' or 'following'."],
                    });
                }
            }

            var response = await social.GetFeedAsync(decodedCursor, effectiveLimit, GetUserId(user), feedScope, cancellationToken);
            return Results.Ok(response);
        })
        .RequireRateLimiting(RateLimitPolicies.Social);
    }

    private static Guid GetUserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult ToNoContent<T>(SocialResult<T> result) =>
        result.Outcome switch
        {
            SocialOutcome.Success => Results.NoContent(),
            _ => Results.NotFound(),
        };

    // Shared cursor/limit handling, mirroring GET /recipes and the chat lists: malformed
    // cursor or non-positive limit -> 400; above-cap limit clamps silently to MaxPageSize.
    private static bool TryResolvePaging(string? cursor, int? limit, out KeysetCursor? decodedCursor, out int effectiveLimit, out IResult? error)
    {
        decodedCursor = null;
        effectiveLimit = 0;
        error = null;

        if (!string.IsNullOrEmpty(cursor) && !KeysetCursor.TryDecode(cursor, out decodedCursor))
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cursor"] = ["The cursor is malformed."],
            });
            return false;
        }

        effectiveLimit = limit ?? DefaultPageSize;
        if (effectiveLimit <= 0)
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = ["limit must be a positive integer."],
            });
            return false;
        }

        effectiveLimit = Math.Min(effectiveLimit, MaxPageSize);
        return true;
    }
}
