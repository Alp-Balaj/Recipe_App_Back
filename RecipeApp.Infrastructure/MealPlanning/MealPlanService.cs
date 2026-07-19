using Microsoft.EntityFrameworkCore;
using Npgsql;
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
}
