using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Images;
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

    // Stream L: multipart framing and headers on top of a max-size photo, matching
    // ImageEndpoints' own ceiling so a photo import and a photo upload refuse the same file.
    private const long MaxDeclaredPhotoBytes = ImageUploadRules.MaxSizeBytes + 64 * 1024;

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

        // ── STREAM L: the two import routes ─────────────────────────────────────────────
        //
        // Under /recipes because the result is a recipe — 201 with the same RecipeResponse
        // every other recipe route returns, so the SPA routes straight to /recipes/{id}.
        //
        // Their OWN rate-limit lane rather than the chat one that /generate and /cook/ask take.
        // The convention in RateLimitPolicies is per-lane budgets, and import differs from
        // those two in a way that matters: they cost tokens, this also makes an outbound
        // request to a server the CALLER named. Sharing chat's budget would let a burst of
        // imports crowd out cook mode, and would leave this app usable as a traffic amplifier
        // pointed at a third party.
        //
        // Six outcomes, mapping onto four status codes. The two fetch failures are separate
        // deliberately: a private address is a 400 the user can fix by pasting a different
        // link, a blog that is down is a 502 about somebody else's machine.
        group.MapPost("/import/url", async (ImportRecipeFromUrlRequest request, IRecipeImportService import, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await import.ImportFromUrlAsync(request, userId, cancellationToken);
            return ImportResult(result);
        })
        .AddEndpointFilter<ValidationFilter<ImportRecipeFromUrlRequest>>()
        .RequireRateLimiting(RateLimitPolicies.Import);

        // Tier 2. Reads the multipart body by hand for exactly the reasons ImageEndpoints
        // documents: the declared Content-Length can be refused before the body is parsed, and
        // no inferred form parameter means no antiforgery requirement on this cookie-less API.
        //
        // The photo is validated through ImageUploadRules — the SAME magic-byte sniff an upload
        // gets — before a single byte reaches the provider. That ordering is the point: without
        // it, a mislabelled file becomes a paid vision call that fails at Gemini's allow-list
        // instead of a free 400 here.
        group.MapPost("/import/photo", async (HttpRequest httpRequest, IRecipeImportService import, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

            if (httpRequest.ContentLength is > MaxDeclaredPhotoBytes)
            {
                return PhotoProblem($"The photo exceeds the {ImageUploadRules.MaxSizeBytes / (1024 * 1024)} MB size limit.");
            }

            if (!httpRequest.HasFormContentType)
            {
                return PhotoProblem("The request must be multipart/form-data with a 'file' part.");
            }

            var form = await httpRequest.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
            {
                return PhotoProblem("No photo was uploaded. Send it as a multipart part named 'file'.");
            }

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var content = buffer.ToArray();

            var header = content.AsSpan(0, Math.Min(ImageUploadRules.HeaderSniffLength, content.Length));
            if (!ImageUploadRules.TryValidate(file.ContentType, content.Length, header, out _, out var error))
            {
                return PhotoProblem(error!);
            }

            var result = await import.ImportFromPhotoAsync(
                new RecipeImageContent(content, file.ContentType), userId, cancellationToken);
            return ImportResult(result);
        })
        .RequireRateLimiting(RateLimitPolicies.Import);

        // Stream M: the cook-mode assistant. Under /recipes/{id} because the question is ABOUT
        // a recipe and the recipe's own visibility rule is what decides whether it may be asked
        // at all — routing it under /chat would have meant re-deriving that rule somewhere it
        // does not live.
        //
        // It takes the Chat rate-limit lane for the reason POST /generate does: this is a route
        // that costs money per call, and RateLimitPolicies' convention is that money-gated
        // traffic stays isolated from the cheap DB-only budgets. Cook mode makes that matter
        // more than usual — three questions in ninety seconds is an ordinary session, not abuse.
        //
        // NO MIGRATION AND NO CONVERSATION (decision D14): the exchange is session-scoped and
        // the client posts its own history each turn. The only row a turn writes is the usage
        // record on the new AiUsageLanes.CookAssistant lane.
        group.MapPost("/{id:guid}/cook/ask", async (Guid id, CookQuestionRequest request, ICookAssistantService cookAssistant, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await cookAssistant.AskAsync(id, request, userId, cancellationToken);
            return result.Outcome switch
            {
                CookAssistantOutcome.Success => Results.Ok(result.Value),
                CookAssistantOutcome.AssistantUnavailable => CookAssistantUnavailable(),
                CookAssistantOutcome.QuotaExceeded => QuotaExhausted(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<CookQuestionRequest>>()
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

    // Stream M. Same 502 shape, different sentence: cook mode has nothing to save, so promising
    // that "nothing was saved" would answer a question the cook did not ask. What they need to
    // know is that the recipe in front of them is unaffected and the session continues.
    private static IResult CookAssistantUnavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The cooking assistant is temporarily unavailable.",
            detail: "The assistant could not answer just now. Your recipe and timers are unaffected — please try again.");

    // Stream L. Shared by both import routes so the two can never drift on what an outcome
    // means over the wire.
    //
    // The Detail carried by SourceRejected / SourceUnreachable / NotARecipe is surfaced
    // VERBATIM, which is unusual for this codebase — most failures answer with a fixed
    // sentence. It is safe and it is necessary here for the same reason: every one of those
    // messages is about the URL the caller just typed, and they are written to be client-safe
    // (no internal addresses, no provider bodies, no stack detail — see the fetcher). "That
    // page is too large to import" and "no ingredients could be read from the source" are
    // different problems with different fixes, and collapsing them into "import failed" would
    // leave the user re-pasting a link that was never going to work.
    private static IResult ImportResult(RecipeImportResult<ImportRecipeResponse> result) =>
        result.Outcome switch
        {
            RecipeImportOutcome.Success =>
                Results.Created($"/recipes/{result.Value!.Recipe.Id}", result.Value),

            // The request is wrong and the caller can fix it. No network call was made.
            RecipeImportOutcome.SourceRejected => Results.ValidationProblem(
                new Dictionary<string, string[]> { ["url"] = [result.Detail ?? "That address cannot be imported."] }),

            // Well-formed request, fetch worked, the CONTENT is the problem. 422 rather than
            // 400: re-typing the same URL will not help, and a 400 invites exactly that.
            RecipeImportOutcome.NotARecipe => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "No recipe could be read from that source.",
                detail: result.Detail),

            RecipeImportOutcome.SourceUnreachable => Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "That page could not be fetched.",
                detail: result.Detail),

            RecipeImportOutcome.QuotaExceeded => QuotaExhausted(),

            _ => Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "The recipe importer is temporarily unavailable.",
                detail: "The importer could not read a usable recipe. Nothing was saved — please try again."),
        };

    // Same 400 shape as ImageEndpoints' upload failures: RFC-7807 ValidationProblem keyed on
    // the offending field.
    private static IResult PhotoProblem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [message] });
}
