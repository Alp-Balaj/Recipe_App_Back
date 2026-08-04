using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
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
        // here. That path is gone: the generate endpoint it belonged to was deleted, and this
        // is now the only writer of ShoppingListItem rows.
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

        // Quantity and Unit are carried as VALUES now, not as a pre-rendered string (stream
        // G). The display string is derived at the end; keeping the number means the group
        // can add its parts up, which is the whole point of the slice.
        // IngredientId rides along since slice G3: it decides the group's KEY (an id
        // beats a spelling) and unlocks the density that lets mass and volume merge.
        var parts = new List<(string Key, string RawName, decimal Quantity, UnitOfMeasure Unit, Guid? IngredientId, string Dish)>();

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
                        // Slice G3: the catalogue id when the line resolved, the name's
                        // key when it did not. "prawns" and "shrimp" become ONE row.
                        ingredient.IngredientId is Guid resolved
                            ? ShoppingListKeys.ForIngredient(resolved)
                            : IngredientKey.For(ingredient.Name),
                        ingredient.Name,
                        ingredient.Quantity,
                        ingredient.Unit,
                        ingredient.IngredientId,
                        recipe.Title));
                }
            }
        }

        // MealPlanId == null is what makes this query "MANUAL rows" rather than "rows". It is
        // now purely defensive: the deleted generate endpoint used to write rows with
        // MealPlanId set and no WeekStartDate (i.e. 0001-01-01), and no code path can create
        // such a row any more — AddManualAsync is the only writer and always leaves MealPlanId
        // null. LEGACY rows from before the rework can still be sitting in the table, though,
        // and without this predicate they would surface as Manual-origin groups labelled
        // "Added by you", offering a DELETE, in a phantom year-1 week.
        var manual = await _db.ShoppingListItems
            .Where(i => i.UserId == userId && i.MealPlanId == null && i.WeekStartDate == weekStart)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        // Densities for every ingredient this week resolved to — one query, then a
        // dictionary lookup per group. Only the resolved ones have a density to fetch,
        // which is exactly why the catalogue had to exist before this could work.
        var ingredientIds = parts
            .Where(p => p.IngredientId is not null)
            .Select(p => p.IngredientId!.Value)
            .Distinct()
            .ToList();

        var densities = ingredientIds.Count == 0
            ? []
            : await _db.Ingredients
                .Where(i => ingredientIds.Contains(i.Id) && i.GramsPerMillilitre != null)
                .ToDictionaryAsync(i => i.Id, i => i.GramsPerMillilitre!.Value, ct);

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
                group.Select(p => new ShoppingListPartResponse(Units.Format(p.Quantity, p.Unit), p.Dish)).ToList(),
                group.Select(p => p.Dish).Distinct(StringComparer.Ordinal).ToList(),
                mark?.IsPurchased ?? false,
                ShoppingListGroupOrigin.Derived,
                null,
                SumWithinDimensions(group, DensityFor(group, densities))));
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
                item.Id,
                // A manual row's quantity is free text ("a couple of bags"), deliberately —
                // it is a note to self, not a measurement. Nothing to sum.
                []));
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
    /// The density to use when collapsing a group's volume into its mass, or null.
    ///
    /// Null in two cases, and the second is the interesting one:
    ///
    ///   * the group did not resolve, so there is no catalogue row to ask;
    ///   * the group resolved to an ingredient USDA published no volume portion for.
    ///
    /// Both stay null rather than falling back to water's 1.0 g/ml. A wrong density is
    /// worse than two totals: two totals are a shopper reading "300 g + 2 cups", which
    /// is exactly what the recipes said, while a guessed one silently reports a single
    /// confident number that is wrong by however much flour differs from water (about
    /// 40%). The group having MIXED ingredient ids cannot happen — a resolved group is
    /// keyed BY the id, so every part in it shares one.
    /// </summary>
    private static decimal? DensityFor(
        IEnumerable<(string Key, string RawName, decimal Quantity, UnitOfMeasure Unit, Guid? IngredientId, string Dish)> parts,
        IReadOnlyDictionary<Guid, double> densities)
    {
        var id = parts.Select(p => p.IngredientId).FirstOrDefault(i => i is not null);
        return id is Guid resolved && densities.TryGetValue(resolved, out var density)
            ? (decimal)density
            : null;
    }

    /// <summary>
    /// The summation (stream G, slice G1). One total per bucket, where a bucket is:
    ///
    ///   • a CONVERTIBLE dimension — every mass part folds into one gram figure, every volume
    ///     part into one millilitre figure, regardless of which unit each was written in;
    ///   • a single COUNT unit — cloves sum with cloves, cans with cans, and never with each
    ///     other, because there is no rate between them;
    ///   • nothing at all for IMPRECISE parts — a pinch plus a dash is not two of anything,
    ///     and a shopper acting on an invented number is worse off than one reading "a pinch".
    ///
    /// A group with a single part still gets a total, and that is deliberate: the total is
    /// where the list states a quantity in the unit you BUY in ("1.2 kg"), while the parts
    /// stay in the units each recipe was WRITTEN in ("3 cups", "450 g"). Both are wanted.
    ///
    /// Ordered by dimension then unit so a group's totals are stable across reads — the same
    /// reasoning as the group ordering itself.
    /// </summary>
    private static List<ShoppingListTotalResponse> SumWithinDimensions(
        IEnumerable<(string Key, string RawName, decimal Quantity, UnitOfMeasure Unit, Guid? IngredientId, string Dish)> parts,
        decimal? gramsPerMillilitre)
    {
        var convertible = new Dictionary<UnitDimension, decimal>();
        var counted = new Dictionary<UnitOfMeasure, decimal>();

        foreach (var part in parts)
        {
            var dimension = Units.DimensionOf(part.Unit);

            if (Units.IsConvertible(dimension) && Units.ToBase(part.Quantity, part.Unit) is decimal inBase)
            {
                // Slice G3, and the whole reason the catalogue carries a density: with
                // one, volume becomes mass and "2 cups of flour" finally adds to "300 g
                // of flour". Without one the two stay separate, which is the honest
                // answer rather than a guess — see DensityFor.
                if (dimension == UnitDimension.Volume && gramsPerMillilitre is decimal density)
                {
                    convertible[UnitDimension.Mass] =
                        convertible.GetValueOrDefault(UnitDimension.Mass) + (inBase * density);
                }
                else
                {
                    convertible[dimension] = convertible.GetValueOrDefault(dimension) + inBase;
                }
            }
            else if (dimension == UnitDimension.Count)
            {
                counted[part.Unit] = counted.GetValueOrDefault(part.Unit) + part.Quantity;
            }
            // Imprecise falls through deliberately — see the summary.
        }

        var totals = new List<ShoppingListTotalResponse>();

        foreach (var (dimension, total) in convertible.OrderBy(e => e.Key))
        {
            // Reported in the unit FormatBase promoted to, so Quantity and Display agree —
            // a client that formats the number itself must not end up rendering "1500 kg".
            var promoted = dimension == UnitDimension.Mass && total >= 1000m ? UnitOfMeasure.Kilogram
                : dimension == UnitDimension.Volume && total >= 1000m ? UnitOfMeasure.Litre
                : Units.BaseUnitOf(dimension);

            totals.Add(new ShoppingListTotalResponse(
                Units.Round(Units.FromBase(total, promoted)),
                promoted,
                Units.FormatBase(total, dimension)));
        }

        foreach (var (unit, total) in counted.OrderBy(e => e.Key))
        {
            totals.Add(new ShoppingListTotalResponse(Units.Round(total), unit, Units.Format(total, unit)));
        }

        return totals;
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

    // FormatQuantity (a decimal and a free-text unit joined by a space) retired with stream
    // G. Units.Format replaces it: it owns pluralisation, the invariant-culture decimal, and
    // the ToTaste case where a quantity should not be printed at all.
}
