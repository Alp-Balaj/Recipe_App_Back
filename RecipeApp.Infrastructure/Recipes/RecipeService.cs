using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

public class RecipeService : IRecipeService
{
    private readonly ApplicationDbContext _db;
    private readonly IIngredientResolver _ingredientResolver;
    private readonly IContentModerationQueue _moderationQueue;
    private readonly ILogger<RecipeService> _logger;

    public RecipeService(
        ApplicationDbContext db,
        IIngredientResolver ingredientResolver,
        IContentModerationQueue moderationQueue,
        ILogger<RecipeService> logger)
    {
        _db = db;
        _ingredientResolver = ingredientResolver;
        _moderationQueue = moderationQueue;
        _logger = logger;
    }

    public async Task<RecipeResponse> CreateRecipeAsync(CreateRecipeRequest request, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        var recipe = ToRecipe(request, createdByUserId);

        // Resolution runs on WRITE (stream G, D8) — before the Add, so the ids are part
        // of the same jsonb document the insert writes rather than a second UPDATE.
        await _ingredientResolver.ResolveAsync(recipe.Ingredients, cancellationToken);

        _db.Recipes.Add(recipe);

        // Gamification: publishing a recipe awards the author RecipeCreated (+20). The rank
        // bump rides the same SaveChanges as the insert so the two commit atomically.
        var author = await _db.Users.SingleOrDefaultAsync(u => u.Id == createdByUserId, cancellationToken);
        if (author is not null)
        {
            author.CookingRank = RankingService.NewRank(author.CookingRank, RankEvent.RecipeCreated);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} created recipe {RecipeId}.", createdByUserId, recipe.Id);

        // Stream X: offer the new recipe for classification — AFTER the commit, deliberately.
        // TryEnqueue is synchronous, never blocks, never throws and never touches the
        // database; its result is ignored because there is no failure here the author could
        // act on and nothing to roll back. If the queue is full, or moderation is disabled,
        // or the classifier is down, this recipe was still created and this method still
        // returns the same response it always did.
        _moderationQueue.TryEnqueue(new ModerationWorkItem(ReportTargetType.Recipe, recipe.Id));

        return RecipeMapper.ToResponse(recipe);
    }

    public async Task<RecipeResult<RecipeResponse>> GetRecipeByIdAsync(Guid id, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        // Visibility rule 2 (recipe-management plan): a recipe the caller may not read is
        // reported as NotFound — never Forbidden — so 404s don't leak that a private recipe
        // exists. An anonymous caller (guest access, null id) owns nothing and is in no
        // follow graph, so only Public rows reach them. Since stream F (D6) the readable
        // set is RecipeVisibilityPolicy's — Public, own, or a mutual friend's FriendsOnly —
        // and it composes into the same query rather than a second in-memory check.
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted).
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        return RecipeResult<RecipeResponse>.Success(RecipeMapper.ToResponse(recipe));
    }

    public async Task<RecipeListResponse> GetRecipesAsync(RecipeListQuery query, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        // Visibility rule 1 (recipe-management plan): the visibility predicate is the FIRST
        // predicate composed onto the query, before any user-supplied filter, so no filter
        // combination can widen what the caller may see. Since stream F (D6) FriendsOnly is
        // readable by a mutual friend as well as the author — RecipeVisibilityPolicy owns
        // that rule, including the anonymous-caller branch, for every read path in the app.
        // Soft-deleted rows are excluded by the global filter.
        var recipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(currentUserId));

        // Author filter (GET /recipes/mine). Composed straight after the visibility predicate
        // and before any user-supplied filter — it only ever NARROWS, and it is deliberately
        // not allowed to widen: asking for another user's id here still yields only what the
        // visibility predicate above already allowed — their Public rows, plus their
        // FriendsOnly rows when the two of you follow each other, and nothing more. When the
        // id is the caller's own, the two predicates AND down to "everything I wrote",
        // private drafts included — which is the whole point of the endpoint.
        if (query.OwnedByUserId is Guid ownerId)
        {
            recipes = recipes.Where(r => r.CreatedByUserId == ownerId);
        }

        if (query.Cuisine is Cuisine cuisine)
        {
            // Plain equality since stream G. The old lower()-equality dance (and its note
            // about % and _ not acting as wildcards) existed because the column was free
            // text; a typed enum has no case to normalise and no user input to neutralise —
            // the endpoint already rejected anything that is not a member.
            recipes = recipes.Where(r => r.CuisineType == cuisine);
        }

        if (query.Difficulty is not null)
        {
            recipes = recipes.Where(r => r.Difficulty == query.Difficulty);
        }

        // Match-ALL tags (Decisions §3): each Contains translates to jsonb containment
        // ("Tags" @> to_jsonb(@tag)), AND-composed across the requested tags. The containment
        // operand is now the enum's NAME, which is what the curated vocabulary buys — the
        // case-sensitivity of this comparison used to make "Vegan" and "vegan" two facets.
        foreach (var tag in query.Tags)
        {
            recipes = recipes.Where(r => r.Tags.Contains(tag));
        }

        // Full-text search (open-loops slice 2) over title + description + ingredient names,
        // against the stored generated tsvector column configured in ApplicationDbContext.
        //
        // websearch_to_tsquery is the parser to use for user input: it accepts whatever
        // someone types — bare words, "quoted phrases", or/-negation — and NEVER throws on
        // malformed input, unlike to_tsquery. So there is nothing to escape here, which is
        // also why this is not the ILIKE path where % and _ would need neutralising.
        //
        // Composed AFTER the visibility predicate like every other filter: it can only
        // narrow. A private recipe is not findable by another caller, by construction.
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            recipes = recipes.Where(r =>
                EF.Property<NpgsqlTsVector>(r, "SearchVector")
                    .Matches(EF.Functions.WebSearchToTsQuery("english", term)));
        }

        if (query.Cursor is not null)
        {
            var cursorCreatedAt = query.Cursor.CreatedAt;
            var cursorId = query.Cursor.Id;
            // Explicit two-branch keyset predicate — EF Core can't translate a row-value
            // (a, b) < (c, d) comparison from LINQ. CompareTo becomes uuid < in SQL.
            recipes = recipes.Where(r =>
                r.CreatedAt < cursorCreatedAt
                || (r.CreatedAt == cursorCreatedAt && r.Id.CompareTo(cursorId) < 0));
        }

        // limit + 1: the extra row only signals that a further page exists; it is trimmed
        // from the response, and the last returned item becomes the next cursor.
        //
        // Ordering stays CreatedAt DESC even for a search — deliberately, not by omission.
        // RecipeListCursor carries only (CreatedAt, Id), so ordering by ts_rank would make
        // the cursor unable to describe a position in the result set and pagination would
        // silently repeat or skip rows. Relevance ranking needs a new cursor shape first.
        var rows = await recipes
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > query.Limit)
        {
            rows.RemoveAt(query.Limit);
            var last = rows[^1];
            nextCursor = new RecipeListCursor(last.CreatedAt, last.Id).Encode();
        }

        return new RecipeListResponse(rows.Select(RecipeMapper.ToResponse).ToList(), nextCursor);
    }

    public async Task<RecipeResult<RecipeResponse>> UpdateRecipeAsync(Guid id, UpdateRecipeRequest request, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted).
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        // Visibility rule 2 (recipe-management plan): a recipe the caller cannot READ is
        // NotFound — never Forbidden — so 403s don't leak that a private recipe exists.
        // Only a recipe the caller can see but doesn't own is Forbidden. Writing is
        // author-only and stream F did NOT widen that: a mutual friend who can now read a
        // FriendsOnly recipe gets 403 here (they can see it, so there is nothing left to
        // hide), and everyone else still gets 404. The extra query runs only for non-owners.
        if (recipe.CreatedByUserId != currentUserId)
        {
            var readable = await _db.Recipes
                .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
                .AnyAsync(r => r.Id == id, cancellationToken);
            if (!readable)
            {
                return RecipeResult<RecipeResponse>.NotFound();
            }

            _logger.LogWarning("User {UserId} forbidden from updating recipe {RecipeId}.", currentUserId, id);
            return RecipeResult<RecipeResponse>.Forbidden();
        }

        // Full replace: every request field overwrites the row, and the jsonb collections
        // (Ingredients/Steps/Tags) are swapped wholesale — no merge semantics.
        // Id, CreatedAt, CreatedByUserId, IsDeleted/DeletedAt are never touched.
        recipe.Title = request.Title;
        recipe.Description = request.Description;
        recipe.PrepTimeMinutes = request.PrepTimeMinutes;
        recipe.CookTimeMinutes = request.CookTimeMinutes;
        recipe.Servings = request.Servings;
        recipe.Difficulty = request.Difficulty;
        recipe.CuisineType = request.CuisineType;
        recipe.CaloriesPerServing = request.CaloriesPerServing;
        recipe.ImageUrl = request.ImageUrl;
        recipe.Visibility = request.Visibility;
        recipe.Ingredients = request.Ingredients;
        recipe.Steps = request.Steps;
        recipe.Tags = request.Tags;
        recipe.UpdatedAt = DateTime.UtcNow;

        // Resolution runs on WRITE (stream G, D8), on update as well as create: an
        // author correcting "flor" to "flour" gains the catalogue id, and one changing
        // "flour" to "gochujang" loses it. Never throws, never drops a line, never
        // rewrites Name — see IIngredientResolver.
        await _ingredientResolver.ResolveAsync(recipe.Ingredients, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        // Stream X: an EDIT is classified too, and this is not scope creep — PUT is a full
        // replace, so "create something innocuous, then edit it into the real payload" is the
        // one-step bypass of a create-only check. The worker re-reads by id, so it always
        // judges the current text rather than whatever was enqueued.
        _moderationQueue.TryEnqueue(new ModerationWorkItem(ReportTargetType.Recipe, recipe.Id));

        return RecipeResult<RecipeResponse>.Success(RecipeMapper.ToResponse(recipe));
    }

    public async Task<RecipeResult<bool>> DeleteRecipeAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted),
        // so a repeat DELETE of the same recipe naturally falls through to NotFound.
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<bool>.NotFound();
        }

        // Visibility rule 2 (recipe-management plan), exactly as UpdateRecipeAsync applies
        // it: unreadable is NotFound, readable-but-not-mine is Forbidden. Deleting stays
        // author-only — stream F widened reading, never writing.
        if (recipe.CreatedByUserId != currentUserId)
        {
            var readable = await _db.Recipes
                .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
                .AnyAsync(r => r.Id == id, cancellationToken);
            if (!readable)
            {
                return RecipeResult<bool>.NotFound();
            }

            _logger.LogWarning("User {UserId} forbidden from deleting recipe {RecipeId}.", currentUserId, id);
            return RecipeResult<bool>.Forbidden();
        }

        // Soft delete: flip the flag and stamp DeletedAt, then save — no SQL DELETE is ever
        // issued, so the implicit cascades on Like/Comment/SavedRecipe/MealPlanEntry never
        // fire and interaction history survives. The global query filter hides the row from
        // every later read.
        recipe.IsDeleted = true;
        recipe.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return RecipeResult<bool>.Success(true);
    }

    // Manual DTO->entity mapping (per 02-01/02-04): a private method colocated with the
    // service, named To<Entity>(dto). CreatedByUserId is passed in explicitly from the
    // authenticated user's JWT claims, never taken from the request body.
    private static Recipe ToRecipe(CreateRecipeRequest request, Guid createdByUserId)
    {
        return new Recipe
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            PrepTimeMinutes = request.PrepTimeMinutes,
            CookTimeMinutes = request.CookTimeMinutes,
            Servings = request.Servings,
            Difficulty = request.Difficulty,
            CuisineType = request.CuisineType,
            CaloriesPerServing = request.CaloriesPerServing,
            ImageUrl = request.ImageUrl,
            Visibility = request.Visibility,
            Ingredients = request.Ingredients,
            Steps = request.Steps,
            Tags = request.Tags,
            CreatedByUserId = createdByUserId,
        };
    }
}
