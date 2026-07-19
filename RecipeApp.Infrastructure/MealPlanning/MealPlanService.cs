using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

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
                Recipe = new MealPlanEntryRecipeSummary(r.Id, r.Title, r.ImageUrl),
            })
            .OrderBy(e => e.DayOfWeek)
            .ThenBy(e => e.MealType)
            .ToListAsync(cancellationToken);

        var entries = rows
            .Select(r => new MealPlanEntryResponse(r.Id, r.DayOfWeek, r.MealType, r.Recipe))
            .ToList();

        return MealPlanResult<MealPlanResponse>.Success(new MealPlanResponse(plan.Id, plan.WeekStartDate, plan.CreatedAt, entries));
    }

    public async Task<MealPlanResult<MealPlanEntryResponse>> AddEntryAsync(Guid mealPlanId, AddMealPlanEntryRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var ownsPlan = await _db.MealPlans.AnyAsync(mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (!ownsPlan)
        {
            return MealPlanResult<MealPlanEntryResponse>.NotFound();
        }

        // Visibility rule 1 (recipe-management plan), reused verbatim from GET /recipes/{id} /
        // RecipeService.GetRecipeByIdAsync: a non-public recipe is addable only by its own
        // author; anything else (including soft-deleted, via the global filter) is NotFound.
        var recipe = await _db.Recipes
            .Where(r => r.Id == request.RecipeId)
            .Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == userId)
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
            new MealPlanEntryRecipeSummary(recipe.Id, recipe.Title, recipe.ImageUrl)));
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

    // --- cp03: shopping list -------------------------------------------------------------

    public async Task<ShoppingListItemListResponse> GetShoppingListAsync(KeysetCursor? cursor, int limit, Guid userId, CancellationToken cancellationToken = default)
    {
        // meal-planning-v1-semantics #3: single per-user list, ALL of the caller's items
        // regardless of MealPlanId — the nullable FK is traceability, not a list partition.
        var items = _db.ShoppingListItems.Where(s => s.UserId == userId);

        if (cursor is not null)
        {
            var cursorCreatedAt = cursor.Timestamp;
            var cursorId = cursor.Id;
            items = items.Where(s =>
                s.CreatedAt < cursorCreatedAt
                || (s.CreatedAt == cursorCreatedAt && s.Id.CompareTo(cursorId) < 0));
        }

        var rows = await items
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(limit + 1)
            .Select(s => new ShoppingListItemResponse(s.Id, s.Ingredient, s.Quantity, s.IsPurchased, s.CreatedAt, s.MealPlanId))
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            var last = rows[^1];
            nextCursor = new KeysetCursor(last.CreatedAt, last.Id).Encode();
        }

        return new ShoppingListItemListResponse(rows, nextCursor);
    }

    public async Task<ShoppingListItemResponse> AddShoppingListItemAsync(AddShoppingListItemRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var item = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            Ingredient = request.Ingredient,
            Quantity = request.Quantity,
            IsPurchased = false,
            UserId = userId,
            MealPlanId = null,
        };
        _db.ShoppingListItems.Add(item);
        await _db.SaveChangesAsync(cancellationToken);

        return new ShoppingListItemResponse(item.Id, item.Ingredient, item.Quantity, item.IsPurchased, item.CreatedAt, item.MealPlanId);
    }

    public async Task<MealPlanResult<bool>> UpdateShoppingListItemAsync(Guid id, UpdateShoppingListItemRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // Explicit set, not a toggle (meal-planning-v1-semantics #4 deviation) — an
        // ExecuteUpdate targeting (id, userId) is naturally idempotent and also gives the
        // 404-never-403 behavior for free: an unknown id or another user's item matches zero
        // rows either way.
        var updated = await _db.ShoppingListItems
            .Where(s => s.Id == id && s.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsPurchased, request.IsPurchased), cancellationToken);

        return updated == 0
            ? MealPlanResult<bool>.NotFound()
            : MealPlanResult<bool>.Success(true);
    }

    public async Task<MealPlanResult<bool>> DeleteShoppingListItemAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        var deleted = await _db.ShoppingListItems
            .Where(s => s.Id == id && s.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0
            ? MealPlanResult<bool>.NotFound()
            : MealPlanResult<bool>.Success(true);
    }

    // --- cp04: generate shopping list -----------------------------------------------------

    public async Task<MealPlanResult<IReadOnlyList<ShoppingListItemResponse>>> GenerateShoppingListAsync(Guid mealPlanId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Caller-scoped, never 403 — same rule as GetMealPlanByIdAsync
        // (meal-planning-v1-semantics / 404-never-403).
        var plan = await _db.MealPlans.SingleOrDefaultAsync(
            mp => mp.Id == mealPlanId && mp.UserId == userId, cancellationToken);
        if (plan is null)
        {
            return MealPlanResult<IReadOnlyList<ShoppingListItemResponse>>.NotFound();
        }

        // Same hydrate idiom as GetMealPlanByIdAsync: join against _db.Recipes (carries the
        // global !IsDeleted filter) rather than an Include/navigation, so an entry whose
        // recipe is soft-deleted silently drops out instead of erroring. Dedupe micro-decision
        // (cp04, recorded in the session note): one row per DISTINCT recipe per ingredient —
        // if the same recipe appears in multiple entries (e.g. breakfast Mon + Tue), buying
        // for it twice is unambiguous noise, so distinct recipe ids are resolved first.
        var recipeIds = await _db.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .Join(_db.Recipes, e => e.RecipeId, r => r.Id, (e, r) => r.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Separate load for the full recipe rows (incl. the jsonb Ingredients column) — mirrors
        // how the rest of the codebase loads a Recipe entity (e.g. RecipeService), rather than
        // trying to select a jsonb column out of the join projection above.
        var recipes = await _db.Recipes
            .Where(r => recipeIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        // meal-planning-v1-semantics #1: no aggregation, no unit conversion. Quantity is the
        // display string "{Quantity} {Unit}" trimmed, decimal rendered invariant-culture so the
        // string is deterministic regardless of server locale.
        var newItems = recipes
            .OrderBy(r => r.Id)
            .SelectMany(r => r.Ingredients.Select(i => new ShoppingListItem
            {
                Id = Guid.NewGuid(),
                Ingredient = i.Name,
                Quantity = FormatQuantity(i.Quantity, i.Unit),
                IsPurchased = false,
                UserId = userId,
                MealPlanId = mealPlanId,
            }))
            .ToList();

        // meal-planning-v1-semantics #5: replace-per-plan in one explicit transaction.
        // ExecuteDeleteAsync executes immediately (it doesn't go through the change tracker),
        // so without the transaction wrapper a failed insert would leave a half-state. Manual
        // items (MealPlanId null) and other plans' rows are excluded by the WHERE clause and
        // are never touched.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        await _db.ShoppingListItems
            .Where(s => s.MealPlanId == mealPlanId && s.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        if (newItems.Count > 0)
        {
            _db.ShoppingListItems.AddRange(newItems);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var response = newItems
            .Select(i => new ShoppingListItemResponse(i.Id, i.Ingredient, i.Quantity, i.IsPurchased, i.CreatedAt, i.MealPlanId))
            .ToList();

        return MealPlanResult<IReadOnlyList<ShoppingListItemResponse>>.Success(response);
    }

    private static string FormatQuantity(decimal quantity, string unit) =>
        $"{quantity.ToString(CultureInfo.InvariantCulture)} {unit}".Trim();
}
