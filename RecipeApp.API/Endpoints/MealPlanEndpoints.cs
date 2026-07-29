using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.API.Endpoints;

// Meal-planning endpoints (meal-planning plan, cp02–03). Mirrors SocialEndpoints/ChatEndpoints:
// manual mapping, ValidationFilter for bodies, ClaimsPrincipal -> JWT sub scoping,
// MealPlanOutcome switched onto status codes. The whole group runs under the caller-auth
// fallback policy and its own "meal" rate-limit budget (shared by cp2–cp4).
public static class MealPlanEndpoints
{
    // Same page-size policy as GET /recipes and the social lists: default 20, above-cap
    // clamps to 50, non-positive 400.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapMealPlanEndpoints(this WebApplication app)
    {
        MapPlanEndpoints(app);
        MapShoppingListEndpoints(app);
    }

    private static void MapPlanEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/meal-plans").RequireRateLimiting(RateLimitPolicies.Meal);

        group.MapPost("/", async (CreateMealPlanRequest request, IMealPlanService mealPlans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await mealPlans.CreateMealPlanAsync(request, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Created($"/meal-plans/{result.Value!.Id}", result.Value),
                MealPlanOutcome.Conflict => ConflictProblem("A meal plan for this week already exists."),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<CreateMealPlanRequest>>();

        // meal-planning-ui plan, Task 1. The SPA's entry point: POST /meal-plans 409s
        // without returning the existing plan's id, so "open this week" needs a query.
        // weekStart must be a pure UTC date, same rule as CreateMealPlanRequest — a
        // non-midnight or non-UTC value can never match a stored week, so 400 beats a
        // silently-empty list.
        group.MapGet("/", async (string? cursor, int? limit, DateTime? weekStart, IMealPlanService mealPlans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            if (weekStart is not null && (weekStart.Value.Kind != DateTimeKind.Utc || weekStart.Value.TimeOfDay != TimeSpan.Zero))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["weekStart"] = ["weekStart must be a UTC midnight date."],
                });
            }

            var response = await mealPlans.GetMealPlansAsync(decodedCursor, effectiveLimit, weekStart, GetUserId(user), cancellationToken);
            return Results.Ok(response);
        });

        // 404-never-403 (meal plans have no visibility tier): unknown id and another user's
        // plan are indistinguishable to the caller.
        group.MapGet("/{id:guid}", async (Guid id, IMealPlanService mealPlans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await mealPlans.GetMealPlanByIdAsync(id, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        group.MapPost("/{id:guid}/entries", async (Guid id, AddMealPlanEntryRequest request, IMealPlanService mealPlans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await mealPlans.AddEntryAsync(id, request, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Created($"/meal-plans/{id}/entries/{result.Value!.Id}", result.Value),
                MealPlanOutcome.Conflict => ConflictProblem("That day/meal slot is already occupied in this plan."),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<AddMealPlanEntryRequest>>();

        group.MapDelete("/{id:guid}/entries/{entryId:guid}", async (Guid id, Guid entryId, IMealPlanService mealPlans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await mealPlans.RemoveEntryAsync(id, entryId, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.NoContent(),
                _ => Results.NotFound(),
            };
        });

        // cp04: replace-per-plan generation (meal-planning-v1-semantics #5). 200 + the fresh
        // items (a bare array, mirroring the week view's Entries shape — no paging here).
        group.MapPost("/{id:guid}/generate-shopping-list", async (Guid id, IMealPlanService mealPlans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await mealPlans.GenerateShoppingListAsync(id, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });
    }

    private static void MapShoppingListEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/shopping-list").RequireRateLimiting(RateLimitPolicies.Meal);

        // Week/shopping rework (2026-07-29 design): the list is a PROJECTION, so there is no
        // row paging left — one read returns whole weeks of groups.
        group.MapGet("/", async (DateTime? weekStart, ShoppingListScope? scope,
            IShoppingListService shopping, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var effectiveScope = scope ?? ShoppingListScope.Week;

            // Same UTC-midnight rule as GET /meal-plans: a non-midnight or non-UTC value can
            // never match a stored week, so 400 beats a silently-empty list.
            if (weekStart is not null && (weekStart.Value.Kind != DateTimeKind.Utc || weekStart.Value.TimeOfDay != TimeSpan.Zero))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["weekStart"] = ["weekStart must be a UTC midnight date."],
                });
            }

            if (effectiveScope == ShoppingListScope.Week && weekStart is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["weekStart"] = ["weekStart is required when scope is Week."],
                });
            }

            return Results.Ok(await shopping.GetAsync(weekStart, effectiveScope, GetUserId(user), cancellationToken));
        });

        group.MapPost("/", async (AddManualShoppingListItemRequest request, IShoppingListService shopping,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var item = await shopping.AddManualAsync(request, GetUserId(user), cancellationToken);
            return Results.Created($"/shopping-list/{item.Id}", item);
        })
        .AddEndpointFilter<ValidationFilter<AddManualShoppingListItemRequest>>();

        // Explicit full set of both flags, mirroring the old PATCH's idempotence rule.
        group.MapPut("/marks", async (SetShoppingListMarkRequest request, IShoppingListService shopping,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            await shopping.SetMarkAsync(request, GetUserId(user), cancellationToken);
            return Results.NoContent();
        })
        .AddEndpointFilter<ValidationFilter<SetShoppingListMarkRequest>>();

        group.MapDelete("/{id:guid}", async (Guid id, IShoppingListService shopping,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await shopping.DeleteManualAsync(id, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.NoContent(),
                _ => Results.NotFound(),
            };
        });
    }

    // Shared cursor/limit handling, mirroring SocialEndpoints.TryResolvePaging: malformed
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

    private static Guid GetUserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // RFC-7807 ProblemDetails 409, mirroring ChatEndpoints.AssistantUnavailable's shape
    // (Results.Problem with an explicit statusCode) rather than the older Results.Conflict
    // plain-object shape AuthEndpoints uses.
    private static IResult ConflictProblem(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail);
}
