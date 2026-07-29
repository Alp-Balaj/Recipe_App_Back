using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.MealPlanning;

/// <summary>
/// The shopping list as a PROJECTION. Nothing derived is stored: every read recomputes the
/// groups from the caller's plan entries and week-scoped manual rows, then overlays the
/// (user, week, key) marks. That is what makes a tick survive a later plan edit — the tick
/// no longer lives on a row the plan would regenerate.
/// </summary>
public class ShoppingListService : IShoppingListService
{
    private readonly ApplicationDbContext _db;

    public ShoppingListService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ShoppingListResponse> GetAsync(
        DateTime? weekStart, ShoppingListScope scope, Guid userId, CancellationToken cancellationToken = default)
    {
        List<DateTime> weeks = scope == ShoppingListScope.All
            ? await ResolveAllWeeksAsync(userId, cancellationToken)
            : [weekStart ?? CurrentWeekStart()];

        // One read of the caller's marks for every week in play, rather than per week — the
        // overlay is a dictionary lookup after this point.
        var marks = await _db.ShoppingListMarks
            .Where(m => m.UserId == userId && weeks.Contains(m.WeekStartDate))
            .ToListAsync(cancellationToken);

        var projected = new List<ShoppingListWeekResponse>(weeks.Count);
        foreach (var week in weeks)
        {
            projected.Add(await ProjectWeekAsync(week, userId, marks, cancellationToken));
        }

        if (scope == ShoppingListScope.All)
        {
            // A past week that is fully shopped is finished business — it drops out. The
            // current week always stays, even when it is empty, so the default surface has
            // something to render.
            var current = CurrentWeekStart();
            projected = projected
                .Where(w => w.WeekStartDate == current || w.Groups.Any(g => !g.IsPurchased))
                .OrderByDescending(w => w.WeekStartDate == current)
                .ThenByDescending(w => w.WeekStartDate)
                .ToList();
        }

        return new ShoppingListResponse(projected, OrphanedPurchasedNames(marks, projected));
    }

    public async Task<ShoppingListItemResponse> AddManualAsync(
        AddManualShoppingListItemRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // WeekStartDate is what makes the row visible in a week's projection at all — the old
        // MealPlanService.AddShoppingListItemAsync path never set it, so its rows are invisible
        // here. That path is dead once the generate endpoint goes (next task).
        var item = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            Ingredient = request.Ingredient,
            Quantity = request.Quantity,
            IsPurchased = false,
            WeekStartDate = request.WeekStartDate,
            UserId = userId,
            MealPlanId = null,
        };

        _db.ShoppingListItems.Add(item);
        await _db.SaveChangesAsync(cancellationToken);

        return new ShoppingListItemResponse(item.Id, item.Ingredient, item.Quantity, item.IsPurchased, item.CreatedAt, item.MealPlanId);
    }

    public async Task<MealPlanResult<bool>> DeleteManualAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        // Scoping to (id, userId) gives 404-never-403 for free: an unknown id and another
        // user's item both match zero rows.
        var deleted = await _db.ShoppingListItems
            .Where(s => s.Id == id && s.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0
            ? MealPlanResult<bool>.NotFound()
            : MealPlanResult<bool>.Success(true);
    }

    public async Task<MealPlanResult<bool>> SetMarkAsync(
        SetShoppingListMarkRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // Always Success, deliberately. The caller may be ticking a key the projection no
        // longer contains (a plan edit landed between their read and their tick); storing it
        // anyway is harmless — the group is simply absent, and the mark either resurfaces
        // when the ingredient comes back or shows up as an orphan notice.
        var existing = await _db.ShoppingListMarks.SingleOrDefaultAsync(
            m => m.UserId == userId && m.WeekStartDate == request.WeekStartDate && m.Key == request.Key,
            cancellationToken);

        if (existing is not null)
        {
            existing.IsPurchased = request.IsPurchased;
            existing.IsSuppressed = request.IsSuppressed;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return MealPlanResult<bool>.Success(true);
        }

        _db.ShoppingListMarks.Add(new ShoppingListMark
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WeekStartDate = request.WeekStartDate,
            Key = request.Key,
            IsPurchased = request.IsPurchased,
            IsSuppressed = request.IsSuppressed,
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Race backstop on the (UserId, WeekStartDate, Key) unique index — a concurrent
            // first write won. Fall back to the update path so the call still ends idempotent.
            _db.ChangeTracker.Clear();
            await _db.ShoppingListMarks
                .Where(m => m.UserId == userId && m.WeekStartDate == request.WeekStartDate && m.Key == request.Key)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(m => m.IsPurchased, request.IsPurchased)
                    .SetProperty(m => m.IsSuppressed, request.IsSuppressed)
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow),
                    cancellationToken);
        }

        return MealPlanResult<bool>.Success(true);
    }

    // --- projection -----------------------------------------------------------------------

    private async Task<ShoppingListWeekResponse> ProjectWeekAsync(
        DateTime weekStart, Guid userId, List<ShoppingListMark> marks, CancellationToken ct)
    {
        var plan = await _db.MealPlans
            .Where(p => p.UserId == userId && p.WeekStartDate == weekStart)
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);

        var parts = new List<(string Key, string RawName, string Quantity, string Dish)>();

        if (plan is not null)
        {
            // Per ENTRY, not per distinct recipe — two dinners need two dinners' worth.
            //
            // The (day, meal) sort runs CLIENT-side, deliberately. MealType is persisted via
            // .HasConversion<string>(), so a DB-side ORDER BY sorts it ALPHABETICALLY —
            // Breakfast, Dessert, Dinner, Lunch, Snack — and a day's Parts would read
            // Breakfast → Dinner → Lunch. Sorting the materialised list compares the enum's
            // underlying value instead, which is declaration order (the meal order a human
            // expects). MealPlanService's own queries have the same latent quirk; it is
            // invisible there because those results are keyed by day/meal rather than read as a
            // sequence.
            var entries = (await _db.MealPlanEntries
                .Where(e => e.MealPlanId == plan.Id)
                .Join(_db.Recipes, e => e.RecipeId, r => r.Id, (e, r) => new { e.DayOfWeek, e.MealType, RecipeId = r.Id })
                .ToListAsync(ct))
                .OrderBy(e => e.DayOfWeek)
                .ThenBy(e => e.MealType)
                .ToList();

            // Two-query hydrate: the jsonb Ingredients collection cannot ride the anonymous
            // join projection above, so the full recipe rows come back separately (once per
            // DISTINCT recipe, then expanded per entry).
            var recipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();
            var recipes = (await _db.Recipes.Where(r => recipeIds.Contains(r.Id)).ToListAsync(ct))
                .ToDictionary(r => r.Id);

            foreach (var entry in entries.Where(e => recipes.ContainsKey(e.RecipeId)))
            {
                var recipe = recipes[entry.RecipeId];
                foreach (var ingredient in recipe.Ingredients)
                {
                    parts.Add((
                        IngredientKey.For(ingredient.Name),
                        ingredient.Name,
                        FormatQuantity(ingredient.Quantity, ingredient.Unit),
                        recipe.Title));
                }
            }
        }

        // MealPlanId == null is what makes this query "MANUAL rows" rather than "rows". The
        // still-live generate endpoint writes rows with MealPlanId set and no WeekStartDate,
        // i.e. 0001-01-01; without this predicate they would surface as Manual-origin groups
        // labelled "Added by you" and offer a DELETE, in a phantom year-1 week.
        var manual = await _db.ShoppingListItems
            .Where(i => i.UserId == userId && i.MealPlanId == null && i.WeekStartDate == weekStart)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        var markByKey = marks
            .Where(m => m.WeekStartDate == weekStart)
            .ToDictionary(m => m.Key, StringComparer.Ordinal);

        var groups = new List<ShoppingListGroupResponse>();

        // Derived groups: one per exact key, ordered by display name so the list is stable
        // across reads (there is no aisle order to honour — see the design's Q7).
        foreach (var group in parts.GroupBy(p => p.Key, StringComparer.Ordinal))
        {
            markByKey.TryGetValue(group.Key, out var mark);
            if (mark?.IsSuppressed == true) continue;

            groups.Add(new ShoppingListGroupResponse(
                group.Key,
                IngredientKey.DisplayNameFor(group.Select(p => p.RawName)),
                group.Select(p => new ShoppingListPartResponse(p.Quantity, p.Dish)).ToList(),
                group.Select(p => p.Dish).Distinct(StringComparer.Ordinal).ToList(),
                mark?.IsPurchased ?? false,
                ShoppingListGroupOrigin.Derived,
                null));
        }

        // Manual rows stay one group each, keyed for tick storage but never merged into a
        // derived group — deleting a manual row must delete the row, not suppress a key.
        foreach (var item in manual)
        {
            var key = ShoppingListKeys.ForManual(item.Id);
            markByKey.TryGetValue(key, out var mark);

            groups.Add(new ShoppingListGroupResponse(
                key,
                item.Ingredient,
                [new ShoppingListPartResponse(item.Quantity, "Added by you")],
                [],
                mark?.IsPurchased ?? false,
                ShoppingListGroupOrigin.Manual,
                item.Id));
        }

        var ordered = groups
            .OrderBy(g => g.Origin)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ShoppingListWeekResponse(
            weekStart,
            ordered,
            ordered.Count(g => g.IsPurchased),
            ordered.Count);
    }

    /// <summary>
    /// scope=All's week set: the current week (always, even when empty) plus every week the
    /// caller has a plan or a manual row for. Whether those extra weeks actually SURVIVE is
    /// decided after projection — a week whose groups are all ticked is finished business.
    /// </summary>
    private async Task<List<DateTime>> ResolveAllWeeksAsync(Guid userId, CancellationToken ct)
    {
        var planWeeks = await _db.MealPlans
            .Where(p => p.UserId == userId)
            .Select(p => p.WeekStartDate)
            .Distinct()
            .ToListAsync(ct);

        // MealPlanId == null for the same reason as ProjectWeekAsync's manual query: a
        // generated row's unset WeekStartDate would otherwise nominate 0001-01-01 as a week.
        var manualWeeks = await _db.ShoppingListItems
            .Where(i => i.UserId == userId && i.MealPlanId == null)
            .Select(i => i.WeekStartDate)
            .Distinct()
            .ToListAsync(ct);

        return planWeeks
            .Concat(manualWeeks)
            .Append(CurrentWeekStart())
            .Distinct()
            .OrderByDescending(w => w)
            .ToList();
    }

    /// <summary>
    /// "1 item you'd already bought is no longer in your plan": purchased, UNsuppressed marks
    /// whose key matches no group in any projected week. A suppressed mark is not an orphan —
    /// the group is missing because the caller hid it, which is not news.
    ///
    /// Manual keys are excluded: a manual row leaves the list because the caller deleted it
    /// outright, so its stale mark is not a surprise either (and its key would render as the
    /// synthetic "manual:{id}" rather than anything a human typed).
    /// </summary>
    private static List<string> OrphanedPurchasedNames(
        List<ShoppingListMark> marks, List<ShoppingListWeekResponse> projected)
    {
        var liveKeys = projected
            .SelectMany(w => w.Groups.Select(g => g.Key))
            .ToHashSet(StringComparer.Ordinal);

        var projectedWeeks = projected.Select(w => w.WeekStartDate).ToHashSet();

        return marks
            .Where(m => m.IsPurchased && !m.IsSuppressed)
            .Where(m => projectedWeeks.Contains(m.WeekStartDate))
            .Where(m => !ShoppingListKeys.IsManual(m.Key))
            .Where(m => !liveKeys.Contains(m.Key))
            .Select(m => DisplayNameForKey(m.Key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The only name available for a vanished group is its key — the mark deliberately stores
    /// no display copy (a second source of truth for a name nobody edits). Keys are the
    /// lower-cased normal form, so sentence-casing the first letter is the whole transform.
    /// </summary>
    private static string DisplayNameForKey(string key) =>
        key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];

    /// <summary>The UTC-midnight Monday of the week containing "now".</summary>
    private static DateTime CurrentWeekStart()
    {
        var today = DateTime.UtcNow.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(today.AddDays(-daysSinceMonday), DateTimeKind.Utc);
    }

    // Copied (not moved) from MealPlanService while the generate endpoint still exists —
    // the original goes with it in the next task. Decimal rendered invariant-culture so the
    // string is deterministic regardless of server locale.
    private static string FormatQuantity(decimal quantity, string unit) =>
        $"{quantity.ToString(CultureInfo.InvariantCulture)} {unit}".Trim();
}
