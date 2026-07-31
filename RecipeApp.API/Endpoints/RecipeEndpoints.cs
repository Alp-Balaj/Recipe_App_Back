using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.API.Endpoints;

public static class RecipeEndpoints
{
    // Page size policy for GET /recipes (recipe-management plan, Decisions §6):
    // default 20, above-cap values clamp silently to 50, zero/negative are a 400.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapRecipeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/recipes");

        group.MapPost("/", async (CreateRecipeRequest request, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var response = await recipeService.CreateRecipeAsync(request, userId, cancellationToken);
            return Results.Created($"/recipes/{response.Id}", response);
        })
        .AddEndpointFilter<ValidationFilter<CreateRecipeRequest>>();

        // Stream E (decision D1): generate a recipe. Registered here rather than under
        // /chat because the RESULT is a recipe — it answers 201 Created with the same
        // RecipeResponse every other recipe route returns, so the SPA routes straight to
        // /recipes/{id} with no special case. Provenance (IsAiGenerated + the source
        // conversation) rides on the row, not on a separate lane.
        //
        // It DOES take the chat rate-limit lane, though: this is the one recipe route that
        // costs money per call, and RateLimitPolicies' own convention is that money-gated
        // traffic stays isolated from the cheap DB-only budgets. The per-user daily quota
        // (stream B) sits behind it in the service — the IP window is the outer bound, the
        // budget is the personal one.
        group.MapPost("/generate", async (GenerateRecipeRequest request, IRecipeGenerationService generation, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await generation.GenerateRecipeAsync(request, userId, cancellationToken);
            return result.Outcome switch
            {
                RecipeGenerationOutcome.Success => Results.Created($"/recipes/{result.Value!.Recipe.Id}", result.Value),
                RecipeGenerationOutcome.AssistantUnavailable => GeneratorUnavailable(),
                RecipeGenerationOutcome.QuotaExceeded => QuotaExhausted(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<GenerateRecipeRequest>>()
        .RequireRateLimiting(RateLimitPolicies.Chat);

        // Guest access (§3.1): the two read routes are anonymous-capable — the caller id is
        // read as nullable and the services degrade to Public-only for a null caller. They
        // also pick up the IP-partitioned Social rate-limit policy (§3.4): anonymous traffic
        // is now possible here, and these were the only unbudgeted public reads.
        group.MapGet("/", async (
            string? cuisine,
            string? difficulty,
            string[]? tags,
            string? cursor,
            int? limit,
            string? search,
            IRecipeService recipeService,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var userId = GetOptionalUserId(user);

            if (!TryResolveListQuery(cuisine, difficulty, tags, cursor, limit, ownedByUserId: null, search, out var query, out var error))
            {
                return error!;
            }

            var response = await recipeService.GetRecipesAsync(query!, userId, cancellationToken);
            return Results.Ok(response);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Social);

        // The caller's own recipes, private drafts included. Registered BEFORE /{id:guid} for
        // readability only — "mine" can never match the guid constraint, so the two cannot
        // shadow each other in either order.
        //
        // Why a route and not a client-side filter: the SPA previously paged the global list
        // and filtered by author in the browser, so a recipe sitting on page four simply did
        // not exist for "My recipes" until the user paged that far — silently lossy, and the
        // recipe picker's "Mine" segment inherited the same hole. Narrowing server-side means
        // the keyset pages over the caller's rows, so the pages are dense and complete.
        //
        // No AllowAnonymous: "mine" is meaningless without a caller, and the global
        // RequireAuthenticatedUser fallback policy turns that into a 401 for free. It also
        // keeps the parsing GetUserId shape (auth guarantees the claim) rather than the
        // nullable GetOptionalUserId the two anonymous-capable reads use.
        group.MapGet("/mine", async (
            string? cuisine,
            string? difficulty,
            string[]? tags,
            string? cursor,
            int? limit,
            string? search,
            IRecipeService recipeService,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

            if (!TryResolveListQuery(cuisine, difficulty, tags, cursor, limit, ownedByUserId: userId, search, out var query, out var error))
            {
                return error!;
            }

            var response = await recipeService.GetRecipesAsync(query!, userId, cancellationToken);
            return Results.Ok(response);
        })
        .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapGet("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetOptionalUserId(user);
            var result = await recipeService.GetRecipeByIdAsync(id, userId, cancellationToken);
            // GET detail never returns 403 — everything non-Success collapses to 404 so
            // the response can't confirm a private recipe exists. For an anonymous caller
            // every non-Public recipe lands here too — existence never leaks to guests.
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapPut("/{id:guid}", async (Guid id, UpdateRecipeRequest request, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.UpdateRecipeAsync(id, request, userId, cancellationToken);
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                RecipeOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<UpdateRecipeRequest>>();

        group.MapDelete("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.DeleteRecipeAsync(id, userId, cancellationToken);
            // Soft delete: 204 on success (no body). Same 403-vs-404 owner/visibility split
            // as PUT; a repeat DELETE lands in NotFound once the row is behind the filter.
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.NoContent(),
                RecipeOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        });

        // task-10 (meal-planning-week-shopping-rework): recipe-form autocomplete. A shared
        // corpus, but only over the recipes the CALLER MAY SEE — Public plus their own, the
        // same visibility rule 1 predicate the list routes use. The original spec said "all
        // non-deleted recipes"; that was wrong, and it made every user's private recipes'
        // ingredient names readable by every authenticated caller. Public recipes dominate the
        // corpus, so autocomplete still converges — the feature is unharmed.
        //
        // No AllowAnonymous (the global auth fallback applies, same as the write routes above),
        // so this keeps the parsing caller-id shape /mine uses rather than the nullable
        // GetOptionalUserId of the two anonymous-capable reads. On its own Meal rate-limit lane
        // per the plan.
        app.MapGet("/ingredients/names", async (string? q, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var names = await recipeService.GetIngredientNamesAsync(q, userId, cancellationToken);
            return Results.Ok(names);
        })
        .RequireRateLimiting(RateLimitPolicies.Meal);
    }

    // Shared wire-level parsing for the two list routes (GET /recipes and GET /recipes/mine),
    // mirroring MealPlanEndpoints.TryResolvePaging: malformed cursor, undefined difficulty or
    // non-positive limit -> 400; above-cap limit clamps silently to MaxPageSize. Extracted when
    // /mine landed so the two routes can never drift on filter or paging semantics.
    private static bool TryResolveListQuery(
        string? cuisine,
        string? difficulty,
        string[]? tags,
        string? cursor,
        int? limit,
        Guid? ownedByUserId,
        string? search,
        out RecipeListQuery? query,
        out IResult? error)
    {
        query = null;
        error = null;

        // difficulty is bound as a string and parsed by hand so that undefined values
        // (including numeric strings outside the enum, e.g. "99") are a 400 rather
        // than an undefined enum value that silently matches nothing.
        DifficultyLevel? difficultyFilter = null;
        if (!string.IsNullOrEmpty(difficulty))
        {
            if (!Enum.TryParse<DifficultyLevel>(difficulty, ignoreCase: true, out var parsedDifficulty)
                || !Enum.IsDefined(parsedDifficulty))
            {
                error = Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["difficulty"] = [$"'{difficulty}' is not a valid difficulty."],
                });
                return false;
            }
            difficultyFilter = parsedDifficulty;
        }

        RecipeListCursor? decodedCursor = null;
        if (!string.IsNullOrEmpty(cursor) && !RecipeListCursor.TryDecode(cursor, out decodedCursor))
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cursor"] = ["The cursor is malformed."],
            });
            return false;
        }

        var effectiveLimit = limit ?? DefaultPageSize;
        if (effectiveLimit <= 0)
        {
            error = Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = ["limit must be a positive integer."],
            });
            return false;
        }
        effectiveLimit = Math.Min(effectiveLimit, MaxPageSize);

        // Search needs no validation of its own: websearch_to_tsquery accepts any string
        // without throwing, so there is no malformed-input 400 to raise here. A blank or
        // whitespace-only term collapses to null so it does not become a no-op predicate.
        var searchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        query = new RecipeListQuery(cuisine, difficultyFilter, tags ?? [], decodedCursor, effectiveLimit, ownedByUserId, searchTerm);
        return true;
    }

    // Guest access (§3.1): the anonymous-capable read routes resolve the caller id as
    // nullable — no claim (or an unparsable one) is an anonymous caller, not a throw.
    // The write routes keep the parsing GetUserId shape above (auth guarantees the claim).
    private static Guid? GetOptionalUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    // Stream E. Both mirror the chat lane's problem responses verbatim (ChatEndpoints):
    // the two AI failure modes read the same to a client wherever they happen, so the SPA
    // can share one handler.
    private static IResult QuotaExhausted() =>
        Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Daily AI budget exhausted.",
            detail: "You have used today's AI allowance. The budget resets at 00:00 UTC.");

    private static IResult GeneratorUnavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The recipe generator is temporarily unavailable.",
            detail: "The assistant could not write a usable recipe. Nothing was saved — please try again.");
}
