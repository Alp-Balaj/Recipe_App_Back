using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.ValueObjects;
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

        // A hide's snapshot is its expiry condition; a pure tick carries none.
        var snapshot = request.IsSuppressed
            ? await ContributingEntryIdsAsync(request.WeekStartDate, userId, request.Key, cancellationToken)
            : null;

        if (existing is not null)
        {
            existing.IsPurchased = request.IsPurchased;
            existing.IsSuppressed = request.IsSuppressed;
            existing.SuppressedEntryIds = snapshot;
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
            SuppressedEntryIds = snapshot,
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
                    .SetProperty(m => m.SuppressedEntryIds, snapshot)
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow),
                    cancellationToken);
        }

        return MealPlanResult<bool>.Success(true);
    }

    /// <summary>
    /// The group KEY one recipe line resolves to — catalogue id when the line resolved,
    /// the name's key when it did not. The single source for write path and projection:
    /// SetMarkAsync snapshots by it and ProjectWeekAsync groups by it, so they cannot drift.
    /// </summary>
    private static string KeyOf(RecipeIngredient ingredient) =>
        ingredient.IngredientId is Guid resolved
            ? ShoppingListKeys.ForIngredient(resolved)
            : IngredientKey.For(ingredient.Name);

    /// <summary>
    /// The ids of the week's plan entries currently contributing `key` — the projection's
    /// first half re-run for ONE key, at hide time. The snapshot is the hide's whole world:
    /// contributors outside it (added later) render the group again.
    /// </summary>
    private async Task<List<Guid>> ContributingEntryIdsAsync(
        DateTime weekStart, Guid userId, string key, CancellationToken ct)
    {
        var plan = await _db.MealPlans
            .Where(p => p.UserId == userId && p.WeekStartDate == weekStart)
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);
        if (plan is null) return [];

        var entries = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == plan.Id)
            .Select(e => new { e.Id, e.RecipeId })
            .ToListAsync(ct);

        var recipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();
        var recipes = (await _db.Recipes.Where(r => recipeIds.Contains(r.Id)).ToListAsync(ct))
            .ToDictionary(r => r.Id);

        return entries
            .Where(e => recipes.TryGetValue(e.RecipeId, out var recipe)
                        && recipe.Ingredients.Any(i => KeyOf(i) == key))
            .Select(e => e.Id)
            .ToList();
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
        var parts = new List<ProjectedPart>();
        var liveEntryIds = new HashSet<Guid>();
        var hiddenItems = new List<ShoppingListHiddenItemResponse>();

        // Diagnostics defaults: declared here, before the `plan is not null` branch below, so
        // the no-plan path still returns a Diagnostics object (empty/zero) rather than null.
        var silentMeals = new List<ShoppingListSilentMealResponse>();
        var unavailableCount = 0;

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
            //
            // The DAY sort goes through DayOffset rather than the enum, and the shop redesign
            // is what made that matter. System.DayOfWeek numbers Sunday 0, so ordering by the
            // enum in a MONDAY-start week put Sunday's dishes first — harmless while the order
            // was only a display sequence, wrong now that the FIRST part is the row's owning
            // dish ("bought once, under the first dish of the week that needs it"). A Sunday
            // roast would have owned every ingredient it shared with Monday's dinner.
            // A plain SELECT off MealPlanEntries, deliberately not joined to _db.Recipes: the
            // join used to double as an existence check, but Recipes carries a global
            // HasQueryFilter(r => !r.IsDeleted) — a soft-deleted recipe's entry would fall out
            // of the join silently, and UnavailableRecipeCount below needs exactly that entry
            // to still be here to count it.
            var entries = (await _db.MealPlanEntries
                .Where(e => e.MealPlanId == plan.Id)
                .Select(e => new { EntryId = e.Id, e.DayOfWeek, e.MealType, e.RecipeId })
                .ToListAsync(ct))
                .OrderBy(e => DayOffset(e.DayOfWeek))
                .ThenBy(e => e.MealType)
                .ToList();

            liveEntryIds.UnionWith(entries.Select(e => e.EntryId));

            // Two-query hydrate: the jsonb Ingredients collection cannot ride the anonymous
            // join projection above, so the full recipe rows come back separately (once per
            // DISTINCT recipe, then expanded per entry).
            var recipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();
            var recipes = (await _db.Recipes.Where(r => recipeIds.Contains(r.Id)).ToListAsync(ct))
                .ToDictionary(r => r.Id);

            // Diagnostics: run on the UNFILTERED entries list, before the part-building loop
            // below filters to `recipes.ContainsKey(...)`. Getting this backwards would make
            // UnavailableRecipeCount always zero — the very thing it exists to report.
            silentMeals = entries
                .Where(e => recipes.TryGetValue(e.RecipeId, out var r) && r.Ingredients.Count == 0)
                .Select(e => new ShoppingListSilentMealResponse(
                    recipes[e.RecipeId].Title,
                    weekStart.AddDays(DayOffset(e.DayOfWeek)),
                    e.MealType))
                .ToList();
            unavailableCount = entries.Count(e => !recipes.ContainsKey(e.RecipeId));

            foreach (var entry in entries.Where(e => recipes.ContainsKey(e.RecipeId)))
            {
                var recipe = recipes[entry.RecipeId];
                foreach (var ingredient in recipe.Ingredients)
                {
                    parts.Add(new ProjectedPart(
                        entry.EntryId,
                        // Slice G3: the catalogue id when the line resolved, the name's
                        // key when it did not. "prawns" and "shrimp" become ONE row.
                        KeyOf(ingredient),
                        ingredient.Name,
                        ingredient.Quantity,
                        ingredient.Unit,
                        ingredient.IngredientId,
                        recipe.Title,
                        // The calendar date the entry sits on, not the day name: the client
                        // renders "Mon" from it, and comparing dates is what orders the
                        // owning dish first.
                        weekStart.AddDays(DayOffset(entry.DayOfWeek)),
                        entry.MealType));
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

        // The catalogue rows for every ingredient this week resolved to — one query, then a
        // dictionary lookup per group. Only the resolved ones have anything to fetch, which
        // is exactly why the catalogue had to exist before either of these could work.
        //
        // Two fields come back now. The density (slice G3) collapses volume into mass; the
        // CATEGORY (shop redesign) becomes the aisle the row is shelved under. The rows are
        // no longer filtered to `GramsPerMillilitre != null` — an ingredient with no density
        // still has an aisle, and filtering it out here would shelve half the catalogue in
        // "Other".
        var ingredientIds = parts
            .Where(p => p.IngredientId is not null)
            .Select(p => p.IngredientId!.Value)
            .Distinct()
            .ToList();

        var catalogue = ingredientIds.Count == 0
            ? []
            : await _db.Ingredients
                .Where(i => ingredientIds.Contains(i.Id))
                .Select(i => new { i.Id, i.Category, i.GramsPerMillilitre })
                .ToDictionaryAsync(i => i.Id, i => (i.Category, i.GramsPerMillilitre), ct);

        var densities = catalogue
            .Where(e => e.Value.GramsPerMillilitre is not null)
            .ToDictionary(e => e.Key, e => e.Value.GramsPerMillilitre!.Value);

        // The other half of the aisle question, and the one the query above cannot answer: a
        // group that resolved to NOTHING still needs a heading, and it has no id to fetch by.
        // "Other" was the largest heading on a real week because of exactly this.
        //
        // So the lookup is BY KEY. ShoppingAisles.FallbackKeysFor names the candidate keys it
        // would walk for one group ("plum tomato", then "tomato"); collecting them across the
        // whole week first means one query answers every unresolved group at once, in the same
        // shape the density lookup uses. One extra query per week projection — never one per
        // row, which is what a per-group lookup would have cost.
        //
        // The join reaches Ingredients for the CATEGORY only. Nothing here can return an id
        // and nothing writes back: the fallback shelves a row and leaves it otherwise exactly
        // as unresolved as it was.
        var fallbackKeys = parts
            .Where(p => p.IngredientId is null)
            .Select(p => p.Key)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(ShoppingAisles.FallbackKeysFor)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var fallbackCategories = fallbackKeys.Count == 0
            ? []
            : await _db.IngredientAliases
                .Where(a => fallbackKeys.Contains(a.MatchKey))
                .Join(_db.Ingredients, a => a.IngredientId, i => i.Id, (a, i) => new { a.MatchKey, i.Category })
                .ToDictionaryAsync(x => x.MatchKey, x => x.Category, StringComparer.Ordinal, ct);

        var markByKey = marks
            .Where(m => m.WeekStartDate == weekStart)
            .ToDictionary(m => m.Key, StringComparer.Ordinal);

        var groups = new List<ShoppingListGroupResponse>();

        // Derived groups: one per exact key. Parts (and therefore Dishes) come out in
        // (date, meal) order, which is the shop redesign's buy-once rule made mechanical —
        // the first part is the dish the row is filed under, the rest are the "+ also"
        // names. Entries were already sorted that way above, and GroupBy preserves
        // encounter order within a group, so there is nothing to re-sort here.
        foreach (var group in parts.GroupBy(p => p.Key, StringComparer.Ordinal))
        {
            markByKey.TryGetValue(group.Key, out var mark);
            if (HideApplies(mark, group))
            {
                hiddenItems.Add(new ShoppingListHiddenItemResponse(
                    group.Key, IngredientKey.DisplayNameFor(group.Select(p => p.RawName))));
                continue;
            }

            groups.Add(new ShoppingListGroupResponse(
                group.Key,
                IngredientKey.DisplayNameFor(group.Select(p => p.RawName)),
                group.Select(p => new ShoppingListPartResponse(
                    Units.Format(p.Quantity, p.Unit), p.Dish, p.Date, p.Meal)).ToList(),
                group.Select(p => p.Dish).Distinct(StringComparer.Ordinal).ToList(),
                mark?.IsPurchased ?? false,
                ShoppingListGroupOrigin.Derived,
                null,
                SumWithinDimensions(group, DensityFor(group, densities)),
                AisleFor(group.Key, group, catalogue, fallbackCategories)));
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
                // No date and no meal: a manual row serves no planned dish, so there is no
                // day to name and nothing for it to be the "first dish" of.
                [new ShoppingListPartResponse(item.Quantity, "Added by you", null, null)],
                [],
                mark?.IsPurchased ?? false,
                ShoppingListGroupOrigin.Manual,
                item.Id,
                // A manual row's quantity is free text ("a couple of bags"), deliberately —
                // it is a note to self, not a measurement. Nothing to sum.
                [],
                // Free text resolves to nothing, so there is no category to shelve it by.
                ShoppingAisles.Other));
        }

        // Dead hides: suppressed marks whose snapshot references nothing still planned
        // (or no snapshot at all — pre-rework rows). Deleted on read, so the table
        // self-cleans without a background job. Purchased marks are exempt: their hide
        // can never re-apply once dead, but the tick must survive for when the
        // ingredient returns. ExecuteDeleteAsync rather than RemoveRange+SaveChanges: a
        // second overlapping read can already have deleted the same row (untracked, so
        // there is no concurrency token to trip on), and that must be a silent no-op,
        // not a DbUpdateConcurrencyException that 500s the read.
        var deadMarks = marks
            .Where(m => m.WeekStartDate == weekStart && m.IsSuppressed && !m.IsPurchased)
            .Where(m => m.SuppressedEntryIds is null || !m.SuppressedEntryIds.Any(liveEntryIds.Contains))
            .ToList();
        if (deadMarks.Count > 0)
        {
            var deadIds = deadMarks.Select(m => m.Id).ToList();
            await _db.ShoppingListMarks
                .Where(m => deadIds.Contains(m.Id))
                .ExecuteDeleteAsync(ct);
            marks.RemoveAll(deadMarks.Contains);   // orphan banner must not see them either
        }

        // Aisle WALK order, not alphabetical (shop redesign): the list is read while walking
        // a shop, so produce leads and drinks trail. Origin no longer needs its own sort —
        // manual rows are all in "Other", which ranks last by construction.
        var ordered = groups
            .OrderBy(g => ShoppingAisles.RankOf(g.Aisle))
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ShoppingListWeekResponse(
            weekStart,
            ordered,
            ordered.Count(g => g.IsPurchased),
            ordered.Count,
            new ShoppingListWeekDiagnosticsResponse(hiddenItems, silentMeals, unavailableCount));
    }

    /// <summary>
    /// Trust rework: a hide applies only while the group's CURRENT contributors are a
    /// subset of the snapshot taken when it was written. A contributor outside the
    /// snapshot — a meal added or re-added since — renders the group again. A null
    /// snapshot (pre-rework mark) never holds. Stateless on purpose: if the newer meal
    /// leaves again, the plan is back inside the snapshot and the hide re-arms.
    /// </summary>
    private static bool HideApplies(ShoppingListMark? mark, IEnumerable<ProjectedPart> group) =>
        mark is { IsSuppressed: true, SuppressedEntryIds: not null }
        && group.All(p => mark.SuppressedEntryIds.Contains(p.EntryId));

    /// <summary>
    /// One recipe line on its way into a group — the working shape of the projection, before
    /// parts are grouped by key and rendered.
    ///
    /// A record rather than the tuple it replaced: it grew a Date and a Meal with the shop
    /// redesign, and an eight-field tuple spelled out in three signatures is a change nobody
    /// can make safely.
    /// </summary>
    private sealed record ProjectedPart(
        Guid EntryId,
        string Key,
        string RawName,
        decimal Quantity,
        UnitOfMeasure Unit,
        Guid? IngredientId,
        string Dish,
        DateTime Date,
        MealType Meal);

    /// <summary>
    /// Days from the week's Monday. System.DayOfWeek numbers Sunday 0, and every week here
    /// starts on a Monday — so this is the only correct way to turn an entry's day into a
    /// position in the week, or into a date.
    /// </summary>
    private static int DayOffset(DayOfWeek day) => ((int)day + 6) % 7;

    /// <summary>
    /// The aisle a group is shelved in: its catalogue category, mapped by ShoppingAisles.
    ///
    /// A group's parts all share one ingredient id when they resolved at all (the id IS the
    /// key), so the first resolved part answers for the group.
    ///
    /// An unresolved group has no id and no category, and used to stop there — in "Other",
    /// beside the manual rows, which on a real week made "Other" the biggest heading on the
    /// page. It now gets a second, deliberately WEAKER attempt: its key is the keyed form of
    /// its own name, and ShoppingAisles walks that name's tail for a head noun the catalogue
    /// does know ("plum tomato" → "tomato"). That walk may answer with an AISLE and can reach
    /// nothing else — the group keeps its name-derived key, its null id, and its absence from
    /// every nutrition and dietary figure. A wrong heading is cheap; a wrong identity is not.
    /// </summary>
    private static string AisleFor(
        string key,
        IEnumerable<ProjectedPart> parts,
        IReadOnlyDictionary<Guid, (string Category, double? GramsPerMillilitre)> catalogue,
        IReadOnlyDictionary<string, string> fallbackCategories)
    {
        var id = parts.Select(p => p.IngredientId).FirstOrDefault(i => i is not null);
        return id is Guid resolved && catalogue.TryGetValue(resolved, out var row)
            ? ShoppingAisles.ForCategory(row.Category)
            : ShoppingAisles.FallbackAisleFor(key, fallbackCategories);
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
        IEnumerable<ProjectedPart> parts,
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
        IEnumerable<ProjectedPart> parts,
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
