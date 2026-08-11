using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.API.Endpoints;

// The cook log (plan-page redesign / roadmap spec 2, 2026-08-10) — one row per cook event,
// behind /plan's "How did it go?" card and /plan/cooks.
//
// Same shape as MealPlanEndpoints: manual mapping, ValidationFilter for bodies, JWT sub
// scoping, MealPlanOutcome switched onto status codes, the "meal" rate-limit budget. Every
// route is the caller's own private history, so none of them is AllowAnonymous.
//
// POST /recipes/{id}/cooked is untouched and still serves MealCard and RecipeDetailPage.
// This group is the richer path: it is the one that can carry a plan entry id.
public static class CookLogEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapCookLogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/cook-log").RequireRateLimiting(RateLimitPolicies.Meal);

        group.MapPost("/", async (LogCookRequest request, ICookLogService cookLog,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await cookLog.LogAsync(
                request.RecipeId, request.MealPlanEntryId, request.CookedAt, request.Note,
                GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Created($"/cook-log/{result.Value!.Id}", result.Value),
                // KAN-6's two date bounds. A 400 rather than the group's usual 404 because the
                // caller CAN see this recipe and CAN cook it — the day they named is the problem,
                // and it is one they can fix by naming another. Keyed on "cookedAt" so the same
                // RFC-7807 shape ValidationFilter produces comes back, and the client's existing
                // ApiValidationError handling surfaces the sentence without a second code path.
                MealPlanOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["cookedAt"] = [result.Detail ?? "That date cannot be used."],
                }),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<LogCookRequest>>();

        group.MapGet("/", async (string? cursor, int? limit, ICookLogService cookLog,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (!TryResolvePaging(cursor, limit, out var decodedCursor, out var effectiveLimit, out var error))
            {
                return error!;
            }

            var result = await cookLog.ListAsync(GetUserId(user), decodedCursor, effectiveLimit, cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        // Everything the "How did it go?" card renders, in one request: the newest row and the
        // lifetime count its "All N cooks ›" link needs. 200 with a null Latest on an empty
        // log — see CookLogLatestResponse for why this is not a 204.
        group.MapGet("/latest", async (ICookLogService cookLog,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await cookLog.GetLatestAsync(GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        // PATCH, not PUT: this replaces one field of a row whose other fields are not the
        // client's to send back. It increments nothing — see ICookLogService.UpdateNoteAsync.
        group.MapPatch("/{id:guid}", async (Guid id, UpdateCookNoteRequest request, ICookLogService cookLog,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await cookLog.UpdateNoteAsync(id, request.Note, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<UpdateCookNoteRequest>>();

        // The undo half of POST /cook-log's plan-linked case. Entry-scoped rather than
        // row-scoped: the client knows which meal it is un-cooking, not which log row, and
        // making it find the row first would be a read it should not need.
        //
        // 204, unlike POST's 201: there is no row left to return, and the client refetches
        // the plan for the authoritative cookedAt rather than patching a body.
        group.MapDelete("/entries/{entryId:guid}", async (Guid entryId, ICookLogService cookLog,
            ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var result = await cookLog.UncookEntryAsync(entryId, GetUserId(user), cancellationToken);
            return result.Outcome switch
            {
                MealPlanOutcome.Success => Results.NoContent(),
                _ => Results.NotFound(),
            };
        });
    }

    // Same policy as every other list in the app: default 20, above-cap clamps to 50,
    // non-positive 400, malformed cursor 400. Copied from MealPlanEndpoints rather than
    // shared, matching how SocialEndpoints and MealPlanEndpoints already each hold their own.
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
}
