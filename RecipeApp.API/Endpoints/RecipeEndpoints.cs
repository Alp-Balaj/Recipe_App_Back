using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;

namespace RecipeApp.API.Endpoints;

public static class RecipeEndpoints
{
    // Page size policy for GET /recipes (recipe-management plan, Decisions §6):
    // default 20, above-cap values clamp silently to 50, zero/negative are a 400.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    // GET /ingredients pages differently from the recipe list, and on purpose. The
    // catalogue is a BOUNDED reference set (1,500 rows) that a client caches whole, not
    // a growing feed to keyset through — so the ceiling is high enough to fetch the lot
    // in a few calls and the default is sized for a picker's dropdown.
    private const int DefaultIngredientPageSize = 25;
    private const int MaxIngredientPageSize = 200;

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

        // GET /recipes/{id}/insights — stream G, slice G4.
        //
        // Its own route rather than fields on RecipeResponse: it costs a catalogue join
        // that the feed, the planner and the picker would all otherwise pay for data none
        // of them shows. Anonymous-capable like the detail read beside it; an anonymous
        // caller gets nutrition and no dietary checks, because the restrictions checked
        // are the CALLER's own.
        group.MapGet("/{id:guid}/insights", async (
            Guid id, IRecipeInsightService insights, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetOptionalUserId(user);
            var result = await insights.GetAsync(id, userId, cancellationToken);
            // 404-never-403, exactly as GET /recipes/{id} does it: insights describe a
            // recipe, so their existence must not confirm a private one's.
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

        // GET /ingredients/names was RETIRED by stream G, slice G3.
        //
        // It answered the recipe form's autocomplete by scanning the 500 most recent
        // recipes' jsonb and offering back whatever people had already typed — the best
        // available answer when the only vocabulary was the corpus itself. Its own
        // comment conceded what it bought: it "only slows the corpus from diverging
        // further". GET /ingredients replaces it with a curated set of 1,500 canonical
        // ingredients that the WRITE path then actually resolves against, so a suggestion
        // is now a promise rather than a hint.
        //
        // It also carried a visibility rule, because it read names out of RECIPES and had
        // to not leak a private one. The catalogue belongs to nobody, so its replacement
        // needs no caller at all — see /ingredients below.

        // GET /ingredients — the catalogue (stream G, slice G2).
        //
        // AllowAnonymous, and it is the first read in this codebase that is genuinely
        // unscoped. Every other list applies a visibility rule because its rows belong to
        // someone; a catalogue row belongs to nobody and is identical for every caller.
        // Contrast the retired /ingredients/names, which had to be caller-scoped
        // precisely because it read names out of RECIPES and could leak a private one.
        //
        // Social rate-limit lane, matching the other anonymous-capable reads.
        app.MapGet("/ingredients", async (
            string? q,
            int? limit,
            IIngredientCatalogueService catalogue,
            CancellationToken cancellationToken) =>
        {
            var effectiveLimit = limit is > 0 ? Math.Min(limit.Value, MaxIngredientPageSize) : DefaultIngredientPageSize;
            var response = await catalogue.SearchAsync(q, effectiveLimit, cancellationToken);
            return Results.Ok(response);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Social);
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

        // cuisine and tags get the same treatment since stream G, for the same reason and
        // one more. Before typing, an unrecognised ?cuisine=Klingon simply matched no rows —
        // a 200 with an empty list, indistinguishable from "we have no Klingon recipes". Now
        // it is a 400 that names the problem, so a client bug surfaces as a client bug.
        Cuisine? cuisineFilter = null;
        if (!string.IsNullOrEmpty(cuisine))
        {
            if (!Vocabulary.TryParseMember<Cuisine>(cuisine, out var parsedCuisine))
            {
                error = Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["cuisine"] = [$"'{cuisine}' is not a valid cuisine."],
                });
                return false;
            }
            cuisineFilter = parsedCuisine;
        }

        var tagFilters = new List<RecipeTag>();
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            if (!Vocabulary.TryParseMember<RecipeTag>(tag, out var parsedTag))
            {
                error = Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["tags"] = [$"'{tag}' is not a valid tag."],
                });
                return false;
            }
            tagFilters.Add(parsedTag);
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

        query = new RecipeListQuery(cuisineFilter, difficultyFilter, tagFilters, decodedCursor, effectiveLimit, ownedByUserId, searchTerm);
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
