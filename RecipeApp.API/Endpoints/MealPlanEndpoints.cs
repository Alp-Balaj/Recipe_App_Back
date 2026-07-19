using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.API.Endpoints;

// Meal-planning endpoints (meal-planning plan, cp02). Mirrors SocialEndpoints/ChatEndpoints:
// manual mapping, ValidationFilter for bodies, ClaimsPrincipal -> JWT sub scoping,
// MealPlanOutcome switched onto status codes. The whole group runs under the caller-auth
// fallback policy and its own "meal" rate-limit budget (shared by cp3/cp4 later).
public static class MealPlanEndpoints
{
    public static void MapMealPlanEndpoints(this WebApplication app)
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
