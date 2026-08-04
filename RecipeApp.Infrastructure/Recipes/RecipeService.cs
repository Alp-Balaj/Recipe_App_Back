using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
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
    private readonly ILogger<RecipeService> _logger;

    public RecipeService(ApplicationDbContext db, ILogger<RecipeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RecipeResponse> CreateRecipeAsync(CreateRecipeRequest request, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        var recipe = ToRecipe(request, createdByUserId);
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
        return RecipeMapper.ToResponse(recipe);
    }

    public async Task<RecipeResult<RecipeResponse>> GetRecipeByIdAsync(Guid id, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted).
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        // Visibility rule 2 (recipe-management plan): a non-public recipe not owned by the
        // caller is reported as NotFound — never Forbidden — so 404s don't leak that a
        // private recipe exists. An anonymous caller (guest access, null id) owns nothing,
        // so any non-public recipe is NotFound for them.
        if (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        return RecipeResult<RecipeResponse>.Success(RecipeMapper.ToResponse(recipe));
    }

    public async Task<RecipeListResponse> GetRecipesAsync(RecipeListQuery query, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        // Visibility rule 1 (recipe-management plan): the visibility predicate is the FIRST
        // predicate composed onto the query, before any user-supplied filter, so no filter
        // combination can widen what the caller may see. FriendsOnly is owner-only until
        // social-features adds follows. Soft-deleted rows are excluded by the global filter.
        // Anonymous caller (guest access): explicit null branch — Public only. Branching
        // beats passing the nullable into EF, where `= NULL` would silently be always-false.
        var recipes = currentUserId is Guid callerId
            ? _db.Recipes.Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == callerId)
            : _db.Recipes.Where(r => r.Visibility == RecipeVisibility.Public);

        // Author filter (GET /recipes/mine). Composed straight after the visibility predicate
        // and before any user-supplied filter — it only ever NARROWS, and it is deliberately
        // not allowed to widen: asking for another user's id here still yields just their
        // Public rows, because the visibility predicate above already ran. When the id is the
        // caller's own, the two predicates AND down to "everything I wrote", private drafts
        // included — which is the whole point of the endpoint.
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

        // Visibility rule 2 (recipe-management plan): a non-public recipe not owned by the
        // caller is NotFound — never Forbidden — so 403s don't leak that a private recipe
        // exists. Only a recipe the caller can see but doesn't own is Forbidden.
        if (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        if (recipe.CreatedByUserId != currentUserId)
        {
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

        await _db.SaveChangesAsync(cancellationToken);

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

        // Visibility rule 2 (recipe-management plan): a non-public recipe not owned by the
        // caller is NotFound — never Forbidden — so 403s don't leak that a private recipe
        // exists. Only a recipe the caller can see but doesn't own is Forbidden.
        if (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId)
        {
            return RecipeResult<bool>.NotFound();
        }

        if (recipe.CreatedByUserId != currentUserId)
        {
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

    // task-10 (meal-planning-week-shopping-rework): backs the recipe-form autocomplete.
    // Recipe.Ingredients is a jsonb column (RecipeApp.Domain gotchas) — there is no relational
    // child table to DISTINCT over, and per the established idiom a jsonb collection can't ride
    // an anonymous join projection either. So: project just the Ingredients column for a bounded
    // window of recipes, then distinct/filter/cap in memory.
    //
    // IngredientNamesScanCap bounds how many recipes are pulled so this stays predictable as the
    // corpus grows — most-recently-created first, so the scan favours the current, most likely
    // to still be typed, vocabulary over long-tail history. 500 recipes is generous for today's
    // corpus and cheap to project (one jsonb column, no joins); revisit if that changes.
    private const int IngredientNamesScanCap = 500;

    public async Task<List<string>> GetIngredientNamesAsync(string? prefix, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Visibility rule 1, same as GetRecipesAsync above: the visibility predicate is the
        // FIRST predicate composed onto the query. Without it this endpoint leaked every
        // user's PRIVATE recipes' ingredient names to every authenticated caller. No nullable
        // branch is needed here (unlike GetRecipesAsync) because the route is not
        // AllowAnonymous — auth guarantees a caller id.
        //
        // Soft-deleted recipes are already excluded by the global query filter (r => !r.IsDeleted).
        // ThenBy(Id) is a deterministic tiebreaker: two recipes can share a CreatedAt timestamp
        // exactly, and which one wins the scan-order "first encountered casing" for a shared
        // ingredient name (below) must not depend on undefined ORDER BY behaviour.
        var ingredientLists = await _db.Recipes
            .Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == currentUserId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Take(IngredientNamesScanCap)
            .Select(r => r.Ingredients)
            .ToListAsync(cancellationToken);

        // Case-insensitively distinct: "Flour" and "flour" collapse to one suggestion. The
        // first-encountered casing (scan order = most recent recipe first) is kept as the
        // display form, tracked alongside how many recipes used that name for the blank-q case.
        var byKey = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ingredients in ingredientLists)
        {
            foreach (var ingredient in ingredients)
            {
                var name = ingredient.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (byKey.TryGetValue(name, out var existing))
                {
                    byKey[name] = (existing.Display, existing.Count + 1);
                }
                else
                {
                    byKey[name] = (name, 1);
                }
            }
        }

        IEnumerable<(string Display, int Count)> matches = byKey.Values;
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            // Prefix given: alphabetical selection AND display — the alphabetically-first 20
            // matches are the 20 returned.
            return matches
                .Where(c => c.Display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .Select(c => c.Display)
                .ToList();
        }

        // Blank/absent q: select the 20 most common names first (most recipes using the name
        // wins), THEN sort the selected 20 alphabetically for stable display.
        return matches
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(c => c.Display)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
