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

        // NOT joined to _db.Recipes any more (KAN-1). The join was doubling as an availability
        // check, and it was wrong in both directions: it dropped a soft-deleted recipe's entry
        // out of the week silently, and it carried a WITHDRAWN recipe's title, photo, time and
        // calories in full, because _db.Recipes composes the global !IsDeleted filter but not
        // the visibility policy. The entries now come back whole and the recipe summary is
        // hydrated separately, through the policy, in the query below.
        //
        // The ORDER BY stays DB-side exactly as it was, so this keeps the ordering the clients
        // already see. (It has the latent MealType quirk ShoppingListService.HydratePlanWeekAsync
        // documents — string conversion makes a DB-side sort alphabetical — but that is not this
        // ticket's to change, and callers key these by day/meal rather than reading them as a
        // sequence.)
        var rows = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Select(e => new
            {
                e.Id,
                e.DayOfWeek,
                e.MealType,
                e.RecipeId,
                // A correlated subquery, NOT a join or a navigation: the caller's own cooks
                // only, and the newest one when a slot was somehow logged twice. Reaching
                // this through cl.Recipe would drag CookLog's required Recipe navigation —
                // and its soft-delete query filter — into the projection.
                CookedAt = _db.CookLogs
                    .Where(cl => cl.UserId == userId && cl.MealPlanEntryId == e.Id)
                    .OrderByDescending(cl => cl.CookedAt)
                    .Select(cl => (DateTime?)cl.CookedAt)
                    .FirstOrDefault(),
                // KAN-8. Same correlated-subquery shape and the same caller scoping as
                // CookedAt above, for the same reasons — this is a second question about the
                // same set of rows, not a second source of truth.
                //
                // "Carries a note" is exactly Note != null: UpdateNoteAsync is the only writer
                // of the column and it normalises empty and whitespace to null on the way in,
                // so a written-then-blanked note reads as no note. If a second writer ever
                // appears, that invariant is what this predicate depends on.
                CookNoteCount = _db.CookLogs
                    .Count(cl => cl.UserId == userId && cl.MealPlanEntryId == e.Id && cl.Note != null),
            })
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.MealType)
            .ToListAsync(cancellationToken);

        // The summaries for the entries whose recipe this caller may READ — the canonical
        // predicate GET /recipes/{id} composes, so a slot renders exactly when opening the
        // dish would work. Anything it does not admit (withdrawn, or soft-deleted via the
        // global filter) is simply absent from the dictionary and the entry reads as
        // unavailable; the two causes are indistinguishable on the wire by construction,
        // because neither produces a row here.
        //
        // TotalTimeMinutes is an unmapped computed property, so it is written out as
        // Prep + Cook to stay translatable — the same reason the list page's TotalMinutes
        // aggregate spells it out.
        var recipeIds = rows.Select(r => r.RecipeId).Distinct().ToList();
        var summaries = recipeIds.Count == 0
            ? []
            : await _db.Recipes
                .Where(RecipeVisibilityPolicy.VisibleTo(userId))
                .Where(r => recipeIds.Contains(r.Id))
                .Select(r => new MealPlanEntryRecipeSummary(
                    r.Id,
                    r.Title,
                    r.ImageUrl,
                    r.PrepTimeMinutes + r.CookTimeMinutes,
                    r.CaloriesPerServing))
                .ToDictionaryAsync(s => s.Id, cancellationToken);

        // rows is already in (day, meal) order from the query — do not re-sort here, or the
        // client-side comparison would silently differ from the DB's.
        var entries = rows
            .Select(r => new MealPlanEntryResponse(
                r.Id,
                r.DayOfWeek,
                r.MealType,
                summaries.GetValueOrDefault(r.RecipeId),
                r.CookedAt,
                r.CookNoteCount))
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

        // Two aggregates rather than one, and the split is the point (KAN-1). These used to be
        // a single grouping over a Join against _db.Recipes, which made an entry's recipe do
        // double duty: it decided both how many meals the week HAS and how long they take.
        // Since GET /meal-plans/{id} now renders an entry whose recipe the caller cannot read
        // as an unavailable slot rather than dropping it, the count has to follow it there —
        // otherwise the list says "2 meals" about a week showing three.
        //
        // Both run AFTER the trim, so the dropped limit+1 probe row is never counted.
        var planIds = rows.Select(r => r.Id).ToList();

        // Every entry the plan holds, available or not — the same set the week view renders.
        var counts = (await _db.MealPlanEntries
            .Where(e => planIds.Contains(e.MealPlanId))
            .GroupBy(e => e.MealPlanId)
            .Select(g => new { MealPlanId = g.Key, EntryCount = g.Count() })
            .ToListAsync(cancellationToken))
            .ToDictionary(s => s.MealPlanId, s => s.EntryCount);

        // Cook time, on the other hand, is the AUTHOR's content and can only be summed over
        // recipes the caller may read — an unavailable slot contributes nothing rather than a
        // number derived from a recipe they are no longer shown. Composing the policy here is
        // what stops the withdrawn recipe's cook time leaking as an arithmetic difference.
        // TotalTimeMinutes is an unmapped computed property, so the sum is written out as
        // Prep + Cook to stay translatable.
        var minutes = (await _db.MealPlanEntries
            .Where(e => planIds.Contains(e.MealPlanId))
            .Join(
                _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(userId)),
                e => e.RecipeId,
                r => r.Id,
                (e, r) => new { e.MealPlanId, Minutes = r.PrepTimeMinutes + r.CookTimeMinutes })
            .GroupBy(x => x.MealPlanId)
            .Select(g => new { MealPlanId = g.Key, TotalMinutes = g.Sum(x => x.Minutes) })
            .ToListAsync(cancellationToken))
            .ToDictionary(s => s.MealPlanId, s => s.TotalMinutes);

        // A plan with no entries at all has no group in either dictionary, hence the zero
        // defaults; a plan whose every entry is unavailable has a count and no minutes.
        var items = rows
            .Select(r => new MealPlanSummaryResponse(
                r.Id,
                r.WeekStartDate,
                r.CreatedAt,
                counts.GetValueOrDefault(r.Id),
                minutes.GetValueOrDefault(r.Id)))
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
                recipe.CaloriesPerServing),
            // A slot created one line ago has no cook against it. Not a placeholder: null IS
            // the answer, and reading CookLog here would be a query with one possible result.
            null,
            // And no cook means no note to lose (KAN-8) — same reasoning, same query avoided.
            0));
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
        // deliberately expands per entry.
        //
        // KAN-1: joined against the VISIBLE recipes, not bare _db.Recipes. This endpoint reads
        // ingredient names and returns a recipe's Title in GroceryOutlierResponse, so a
        // withdrawn recipe leaked here too — the ticket lists the week, list and nutrition
        // reads but not this one, which is exactly why every call site gets pinned separately
        // rather than trusting a list.
        var visibleRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(userId));

        var recipeIds = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Join(visibleRecipes, e => e.RecipeId, r => r.Id, (e, r) => r.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Two-query hydrate: the jsonb Ingredients collection cannot ride the join projection
        // above, so the full recipe rows (incl. Title) come back separately, once per distinct
        // recipe — same split ShoppingListService.ProjectWeekAsync uses. Recipes.Id is the
        // primary key, so this naturally returns one row per recipe even without the Distinct
        // above; that call just keeps the IN clause small rather than doing any deduping work
        // that matters for correctness here.
        //
        // Filtered by the policy AGAIN even though recipeIds came from a query that already
        // applied it. The ids are only as trustworthy as the query that produced them, and the
        // two queries are edited independently — KAN-2 shipped green with a leak precisely
        // because one of a pair of sibling reads was gated and the other was not.
        var recipes = await visibleRecipes
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
        // twice on Sunday really is twice the calories.
        //
        // KAN-1: joined against the VISIBLE recipes. Nutrition is computed from the author's
        // ingredient lines, so an unavailable dish must contribute nothing — a day's kcal that
        // silently includes a recipe the caller is no longer shown is the same leak as the
        // week view's, arrived at by arithmetic. The ribbon therefore totals exactly the meals
        // GET /meal-plans/{id} renders WITH a recipe, and an unavailable slot is absent from
        // MealsCounted rather than counted with nothing behind it.
        var visibleRecipes = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(userId));

        var entries = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Join(visibleRecipes, e => e.RecipeId, r => r.Id, (e, r) => new { e.DayOfWeek, RecipeId = r.Id })
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return MealPlanResult<MealPlanNutritionResponse>.Success(
                new MealPlanNutritionResponse(plan.Id, []));
        }

        // Two-query hydrate, then ONE catalogue read for the whole week — the reason this
        // endpoint exists at all. The alternative shape, a /recipes/{id}/insights call per
        // entry, is up to 21 requests and 21 catalogue loads for one week.
        // Filtered by the policy again, for the same reason as the grocery insight's second
        // query: the ids are only as trustworthy as the query that produced them, and the two
        // are edited independently.
        var recipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();
        var recipes = await visibleRecipes
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
