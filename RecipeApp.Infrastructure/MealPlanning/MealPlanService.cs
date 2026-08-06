using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.Infrastructure.MealPlanning;

public class MealPlanService : IMealPlanService
{
    private readonly ApplicationDbContext _db;

    public MealPlanService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<MealPlanResult<MealPlanResponse>> CreateMealPlanAsync(CreateMealPlanRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // meal-planning-v1-semantics #2: one plan per (user, week). The pre-check is the
        // primary path; the unique index is the race backstop caught below.
        var duplicate = await _db.MealPlans.AnyAsync(
            mp => mp.UserId == userId && mp.WeekStartDate == request.WeekStartDate, cancellationToken);
        if (duplicate)
        {
            return MealPlanResult<MealPlanResponse>.Conflict();
        }

        var plan = new MealPlan
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WeekStartDate = request.WeekStartDate,
        };
        _db.MealPlans.Add(plan);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
            return MealPlanResult<MealPlanResponse>.Conflict();
        }

        return MealPlanResult<MealPlanResponse>.Success(new MealPlanResponse(plan.Id, plan.WeekStartDate, plan.CreatedAt, []));
    }

    public async Task<MealPlanResult<MealPlanResponse>> GetMealPlanByIdAsync(Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Caller-scoped, never 403 — a plan that exists but belongs to another user is
        // reported identically to an unknown id (meal-planning-v1-semantics / rule 404-never-403).
        var plan = await _db.MealPlans.SingleOrDefaultAsync(
            mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (plan is null)
        {
            return MealPlanResult<MealPlanResponse>.NotFound();
        }

        // Joined against _db.Recipes (which carries the global !IsDeleted filter) rather than
        // an Include/navigation — mirrors the chat-suggestion hydration convention: an entry
        // whose recipe fails the filter simply drops out of the join instead of erroring.
        // The DB-side projection stays an anonymous type (translatable + orderable); the
        // record construction happens client-side after materialization — EF Core can't
        // translate an ORDER BY over a constructed record's property.
        var rows = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Join(_db.Recipes, e => e.RecipeId, r => r.Id, (e, r) => new
            {
                e.Id,
                e.DayOfWeek,
                e.MealType,
                // TotalTimeMinutes is an unmapped computed property, so it is written out
                // as Prep + Cook here to stay translatable — same reason the summary's
                // TotalMinutes aggregate spells it out.
                Recipe = new MealPlanEntryRecipeSummary(
                    r.Id,
                    r.Title,
                    r.ImageUrl,
                    r.PrepTimeMinutes + r.CookTimeMinutes,
                    r.CaloriesPerServing),
            })
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.MealType)
            .ToListAsync(cancellationToken);

        var entries = rows
            .Select(r => new MealPlanEntryResponse(r.Id, r.DayOfWeek, r.MealType, r.Recipe))
            .ToList();

        return MealPlanResult<MealPlanResponse>.Success(new MealPlanResponse(plan.Id, plan.WeekStartDate, plan.CreatedAt, entries));
    }

    public async Task<MealPlanListResponse> GetMealPlansAsync(KeysetCursor? cursor, int limit, DateTime? weekStart, Guid userId, CancellationToken cancellationToken = default)
    {
        var plans = _db.MealPlans.Where(p => p.UserId == userId);

        if (weekStart is not null)
        {
            var week = weekStart.Value;
            plans = plans.Where(p => p.WeekStartDate == week);
        }

        if (cursor is not null)
        {
            var cursorWeek = cursor.Timestamp;
            var cursorId = cursor.Id;
            plans = plans.Where(p =>
                p.WeekStartDate < cursorWeek
                || (p.WeekStartDate == cursorWeek && p.Id.CompareTo(cursorId) < 0));
        }

        // DB-side projection stays an anonymous type (translatable + orderable); the record is
        // constructed client-side once the per-plan stats below are in hand — the same split
        // GetMealPlanByIdAsync uses.
        var rows = await plans
            .OrderByDescending(p => p.WeekStartDate)
            .ThenByDescending(p => p.Id)
            .Take(limit + 1)
            .Select(p => new { p.Id, p.WeekStartDate, p.CreatedAt })
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.WeekStartDate, last.Id).Encode();
        }

        // One aggregate query for the whole page rather than a correlated subquery per row —
        // and it runs AFTER the trim, so the dropped limit+1 probe row is never counted.
        // Joining _db.Recipes (global !IsDeleted filter) is what makes EntryCount agree with
        // GET /meal-plans/{id}: an entry whose recipe was soft-deleted is invisible there, so
        // counting it here would show a meal the week view can't render. TotalTimeMinutes is
        // an unmapped computed property, so the sum is written out as Prep + Cook to stay
        // translatable.
        var planIds = rows.Select(r => r.Id).ToList();
        var stats = (await _db.MealPlanEntries
            .Where(e => planIds.Contains(e.MealPlanId))
            .Join(_db.Recipes, e => e.RecipeId, r => r.Id, (e, r) => new
            {
                e.MealPlanId,
                Minutes = r.PrepTimeMinutes + r.CookTimeMinutes,
            })
            .GroupBy(x => x.MealPlanId)
            .Select(g => new
            {
                MealPlanId = g.Key,
                EntryCount = g.Count(),
                TotalMinutes = g.Sum(x => x.Minutes),
            })
            .ToListAsync(cancellationToken))
            .ToDictionary(s => s.MealPlanId);

        // A plan with no surviving entries has no group at all, hence the zero default.
        var items = rows
            .Select(r => stats.TryGetValue(r.Id, out var s)
                ? new MealPlanSummaryResponse(r.Id, r.WeekStartDate, r.CreatedAt, s.EntryCount, s.TotalMinutes)
                : new MealPlanSummaryResponse(r.Id, r.WeekStartDate, r.CreatedAt, 0, 0))
            .ToList();

        return new MealPlanListResponse(items, nextCursor);
    }

    public async Task<MealPlanResult<MealPlanEntryResponse>> AddEntryAsync(Guid mealPlanId, AddMealPlanEntryRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var ownsPlan = await _db.MealPlans.AnyAsync(mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (!ownsPlan)
        {
            return MealPlanResult<MealPlanEntryResponse>.NotFound();
        }

        // Visibility rule 1 (recipe-management plan), reused verbatim from GET /recipes/{id} /
        // RecipeService.GetRecipeByIdAsync — and now literally the same expression, so
        // "verbatim" cannot drift: a recipe is addable to your plan exactly when you can
        // open it. Since stream F (D6) that includes a mutual friend's FriendsOnly recipe.
        // Anything else (including soft-deleted, via the global filter) is NotFound.
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(userId))
            .Where(r => r.Id == request.RecipeId)
            .SingleOrDefaultAsync(cancellationToken);
        if (recipe is null)
        {
            return MealPlanResult<MealPlanEntryResponse>.NotFound();
        }

        // meal-planning-v1-semantics #4: slot exclusivity. Pre-check is the primary path; the
        // unique index is the race backstop caught below.
        var occupied = await _db.MealPlanEntries.AnyAsync(
            e => e.MealPlanId == mealPlanId && e.DayOfWeek == request.DayOfWeek && e.MealType == request.MealType,
            cancellationToken);
        if (occupied)
        {
            return MealPlanResult<MealPlanEntryResponse>.Conflict();
        }

        var entry = new MealPlanEntry
        {
            Id = Guid.NewGuid(),
            MealPlanId = mealPlanId,
            DayOfWeek = request.DayOfWeek,
            MealType = request.MealType,
            RecipeId = recipe.Id,
        };
        _db.MealPlanEntries.Add(entry);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
            return MealPlanResult<MealPlanEntryResponse>.Conflict();
        }

        return MealPlanResult<MealPlanEntryResponse>.Success(new MealPlanEntryResponse(
            entry.Id,
            entry.DayOfWeek,
            entry.MealType,
            // A materialised entity here, so the computed property is usable directly.
            new MealPlanEntryRecipeSummary(
                recipe.Id,
                recipe.Title,
                recipe.ImageUrl,
                recipe.TotalTimeMinutes,
                recipe.CaloriesPerServing)));
    }

    public async Task<MealPlanResult<bool>> RemoveEntryAsync(Guid mealPlanId, Guid entryId, Guid userId, CancellationToken cancellationToken = default)
    {
        var ownsPlan = await _db.MealPlans.AnyAsync(mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (!ownsPlan)
        {
            return MealPlanResult<bool>.NotFound();
        }

        // Entries are hard rows. Scoping the delete to (entryId, mealPlanId) means an entry
        // belonging to a different plan (even the caller's own) reports NotFound rather than
        // deleting the wrong row.
        var deleted = await _db.MealPlanEntries
            .Where(e => e.Id == entryId && e.MealPlanId == mealPlanId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0
            ? MealPlanResult<bool>.NotFound()
            : MealPlanResult<bool>.Success(true);
    }

    // --- grocery insight (week board) -----------------------------------------------------

    public async Task<MealPlanResult<GroceryInsightResponse>> GetGroceryInsightAsync(Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Caller-scoped, never 403 — same rule as GetMealPlanByIdAsync
        // (meal-planning-v1-semantics / 404-never-403).
        var plan = await _db.MealPlans.SingleOrDefaultAsync(
            mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (plan is null)
        {
            return MealPlanResult<GroceryInsightResponse>.NotFound();
        }

        // An outlier is a property of a DISH, not of how often it is cooked, so this collects
        // one row per DISTINCT recipe — unlike the shopping-list projection, which
        // deliberately expands per entry. Join against _db.Recipes (carries the global
        // !IsDeleted filter) so a soft-deleted recipe's entry silently drops, same idiom as
        // GetMealPlanByIdAsync.
        var recipeIds = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Join(_db.Recipes, e => e.RecipeId, r => r.Id, (e, r) => r.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Two-query hydrate: the jsonb Ingredients collection cannot ride the join projection
        // above, so the full recipe rows (incl. Title) come back separately, once per distinct
        // recipe — same split ShoppingListService.ProjectWeekAsync uses. Recipes.Id is the
        // primary key, so this naturally returns one row per recipe even without the Distinct
        // above; that call just keeps the IN clause small rather than doing any deduping work
        // that matters for correctness here.
        var recipes = await _db.Recipes
            .Where(r => recipeIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        // Per-recipe distinct key sets (a recipe repeating an ingredient name counts that key
        // once for this recipe), plus how many distinct recipes each key appears in.
        var recipeKeys = recipes
            .Select(r => (r.Id, r.Title, Keys: r.Ingredients.Select(i => IngredientKey.For(i.Name)).ToHashSet(StringComparer.Ordinal)))
            .ToList();

        var keyRecipeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, _, keys) in recipeKeys)
        {
            foreach (var key in keys)
            {
                keyRecipeCounts[key] = keyRecipeCounts.GetValueOrDefault(key) + 1;
            }
        }

        var distinctIngredientCount = keyRecipeCounts.Count;
        var sharedIngredientCount = keyRecipeCounts.Count(kv => kv.Value >= 2);

        // The outlier is the recipe with the most keys nobody else in the plan uses, ties
        // broken by title (ordinal); null when no recipe has any unique ingredient (covers
        // the empty-plan case too, since recipeKeys is then empty).
        var outlier = recipeKeys
            .Select(r => (r.Id, r.Title, UniqueCount: r.Keys.Count(k => keyRecipeCounts[k] == 1)))
            .Where(r => r.UniqueCount > 0)
            .OrderByDescending(r => r.UniqueCount)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .Select(r => new GroceryOutlierResponse(r.Id, r.Title, r.UniqueCount))
            .FirstOrDefault();

        return MealPlanResult<GroceryInsightResponse>.Success(
            new GroceryInsightResponse(distinctIngredientCount, sharedIngredientCount, outlier));
    }

    // --- computed nutrition (day ribbon, stream I / D12) -----------------------------------

    public async Task<MealPlanResult<MealPlanNutritionResponse>> GetNutritionAsync(
        Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Caller-scoped, never 403 — same rule as GetMealPlanByIdAsync
        // (meal-planning-v1-semantics / 404-never-403).
        var plan = await _db.MealPlans.SingleOrDefaultAsync(
            mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (plan is null)
        {
            return MealPlanResult<MealPlanNutritionResponse>.NotFound();
        }

        // Per ENTRY, not per distinct recipe — the opposite of the grocery insight above,
        // and deliberately: an outlier is a property of a dish, but eating the same dish
        // twice on Sunday really is twice the calories. Joining _db.Recipes (global
        // !IsDeleted filter) drops entries whose recipe was soft-deleted, so the ribbon
        // can never total a meal GET /meal-plans/{id} refuses to render.
        var entries = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Join(_db.Recipes, e => e.RecipeId, r => r.Id, (e, r) => new { e.DayOfWeek, RecipeId = r.Id })
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return MealPlanResult<MealPlanNutritionResponse>.Success(
                new MealPlanNutritionResponse(plan.Id, []));
        }

        // Two-query hydrate, then ONE catalogue read for the whole week — the reason this
        // endpoint exists at all. The alternative shape, a /recipes/{id}/insights call per
        // entry, is up to 21 requests and 21 catalogue loads for one week.
        var recipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();
        var recipes = await _db.Recipes
            .Where(r => recipeIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        var resolvedIds = recipes
            .SelectMany(r => r.Ingredients)
            .Where(i => i.IngredientId is not null)
            .Select(i => i.IngredientId!.Value)
            .Distinct()
            .ToList();

        var catalogue = resolvedIds.Count == 0
            ? []
            : await _db.Ingredients
                .Where(i => resolvedIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, cancellationToken);

        // Computed once per DISTINCT recipe even though it is counted once per entry:
        // the same dish twice costs two servings but only one pass over its lines.
        var perServing = recipes.ToDictionary(
            r => r.Id,
            r => RecipeNutrition.PerServing(r, catalogue));

        var days = entries
            .GroupBy(e => e.DayOfWeek)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var totals = g.Aggregate(
                    NutritionTotals.Nothing,
                    (running, entry) => running.Plus(perServing[entry.RecipeId]));

                // Rounded HERE, once, after the day is whole — rounding each meal first
                // would leave a day's total disagreeing with the meals it is showing.
                return new DayNutritionResponse(
                    g.Key,
                    g.Count(),
                    totals.Kcal is double kcal ? (int)Math.Round(kcal, MidpointRounding.AwayFromZero) : null,
                    totals.ProteinG is double protein ? Math.Round(protein, 1) : null,
                    totals.FatG is double fat ? Math.Round(fat, 1) : null,
                    totals.CarbsG is double carbs ? Math.Round(carbs, 1) : null,
                    totals.FibreG is double fibre ? Math.Round(fibre, 1) : null,
                    totals.CoveredLines,
                    totals.TotalLines,
                    totals.IsSufficientlyCovered);
            })
            .ToList();

        return MealPlanResult<MealPlanNutritionResponse>.Success(
            new MealPlanNutritionResponse(plan.Id, days));
    }
}
