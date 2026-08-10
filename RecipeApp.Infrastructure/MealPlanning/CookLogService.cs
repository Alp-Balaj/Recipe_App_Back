using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.Infrastructure.MealPlanning;

// The cook log (plan-page redesign / roadmap spec 2, 2026-08-10).
//
// Lives beside MealPlanService rather than in Infrastructure/Social because the surfaces it
// serves are the planning ones, and because it reaches for MealPlanEntry ownership on every
// write. It does touch CookedRecipe, which is Social's table — see LogAsync for why that
// coupling is deliberate and where it stops.
public class CookLogService : ICookLogService
{
    private readonly ApplicationDbContext _db;

    public CookLogService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<MealPlanResult<CookLogResponse>> LogAsync(
        Guid recipeId, Guid? mealPlanEntryId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Same visibility expression as adding a dish to a plan, for the same reason: you can
        // log a cook of exactly the recipes you can open. Soft-deleted falls out via the
        // global query filter.
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .Where(r => r.Id == recipeId)
            .Select(r => new { r.Id, r.Title })
            .SingleOrDefaultAsync(cancellationToken);
        if (recipe is null)
        {
            return MealPlanResult<CookLogResponse>.NotFound();
        }

        // An entry id that is not on one of the caller's own plans is NotFound, not a silently
        // dropped field. Accepting it and storing null would let a client believe it had linked
        // a cook to a plan slot when it had not — and roadmap spec 3 reads that link.
        if (mealPlanEntryId is not null)
        {
            var ownsEntry = await _db.MealPlanEntries
                .AnyAsync(e => e.Id == mealPlanEntryId && e.MealPlan.UserId == currentUserId, cancellationToken);
            if (!ownsEntry)
            {
                return MealPlanResult<CookLogResponse>.NotFound();
            }
        }

        var now = DateTime.UtcNow;

        var row = new CookLog
        {
            Id = Guid.NewGuid(),
            UserId = currentUserId,
            RecipeId = recipe.Id,
            RecipeTitle = recipe.Title,
            MealPlanEntryId = mealPlanEntryId,
            CookedAt = now,
        };
        _db.CookLogs.Add(row);

        // The aggregate bump, in the SAME SaveChanges as the insert above — one transaction,
        // both rows or neither. This is the drift the design exists to prevent: a cook logged
        // on /plan that never appears in "you've cooked this 3 times" on the recipe page.
        //
        // Duplicated from SocialService.MarkCookedAsync rather than called into it, because
        // that method owns its own SaveChanges and calling it would commit the bump
        // independently of the insert — the exact atomicity this is trying to buy. The two
        // are pinned together by Logging_a_cook_writes_both_rows.
        //
        // No rank award here either, for SocialService's stated reason: cooking is private and
        // awarding it would let anyone farm rank with a button. The award still hangs off the
        // null -> rated transition on the rating path, which this does not touch.
        var aggregate = await _db.CookedRecipes
            .SingleOrDefaultAsync(cr => cr.UserId == currentUserId && cr.RecipeId == recipe.Id, cancellationToken);
        if (aggregate is null)
        {
            _db.CookedRecipes.Add(new CookedRecipe
            {
                UserId = currentUserId,
                RecipeId = recipe.Id,
                TimesCooked = 1,
                FirstCookedAt = now,
                LastCookedAt = now,
            });
        }
        else
        {
            aggregate.TimesCooked += 1;
            aggregate.LastCookedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Freshly written, so the recipe is by definition still there.
        var imageUrl = await _db.Recipes
            .Where(r => r.Id == recipe.Id)
            .Select(r => r.ImageUrl)
            .SingleOrDefaultAsync(cancellationToken);

        return MealPlanResult<CookLogResponse>.Success(ToResponse(row, imageUrl, recipeAvailable: true));
    }

    public async Task<MealPlanResult<CookLogListResponse>> ListAsync(
        Guid currentUserId, KeysetCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var query = _db.CookLogs.Where(cl => cl.UserId == currentUserId);

        if (cursor is not null)
        {
            // Hoisted to locals before the expression tree, same as ChatService/AppEventService:
            // the translator handles captured locals cleanly and member access on the record
            // less so.
            var cursorCookedAt = cursor.Timestamp;
            var cursorId = cursor.Id;

            // Strictly after the cursor in (CookedAt desc, Id desc) order. The Id half is not
            // decoration: two cooks logged in the same tick would otherwise page against an
            // ambiguous boundary and either repeat or skip a row.
            query = query.Where(cl =>
                cl.CookedAt < cursorCookedAt ||
                (cl.CookedAt == cursorCookedAt && cl.Id.CompareTo(cursorId) < 0));
        }

        // One extra row is the "is there a next page" probe — never returned.
        var page = await Project(query
                .OrderByDescending(cl => cl.CookedAt)
                .ThenByDescending(cl => cl.Id)
                .Take(limit + 1))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        var items = page.Take(limit).ToList();

        var nextCursor = hasMore && items.Count > 0
            ? new KeysetCursor(items[^1].Row.CookedAt, items[^1].Row.Id).Encode()
            : null;

        return MealPlanResult<CookLogListResponse>.Success(new CookLogListResponse(
            items.Select(x => ToResponse(x.Row, x.ImageUrl, x.RecipeAvailable)).ToList(),
            nextCursor));
    }

    public async Task<MealPlanResult<CookLogLatestResponse>> GetLatestAsync(
        Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var latest = await Project(_db.CookLogs
                .Where(cl => cl.UserId == currentUserId)
                .OrderByDescending(cl => cl.CookedAt)
                .ThenByDescending(cl => cl.Id))
            .FirstOrDefaultAsync(cancellationToken);

        var total = await _db.CookLogs.CountAsync(cl => cl.UserId == currentUserId, cancellationToken);

        // An empty log is Success with a null Latest, never NotFound: "you have not cooked
        // anything yet" is an answer, and the card needs to tell it apart from "still loading".
        return MealPlanResult<CookLogLatestResponse>.Success(new CookLogLatestResponse(
            latest is null ? null : ToResponse(latest.Row, latest.ImageUrl, latest.RecipeAvailable),
            total));
    }

    public async Task<MealPlanResult<CookLogResponse>> UpdateNoteAsync(
        Guid id, string? note, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var row = await _db.CookLogs
            .SingleOrDefaultAsync(cl => cl.Id == id && cl.UserId == currentUserId, cancellationToken);
        if (row is null)
        {
            return MealPlanResult<CookLogResponse>.NotFound();
        }

        // Empty and whitespace normalise to null so "cleared" has one representation — the
        // client renders a placeholder on null and would otherwise show an empty note as
        // written-then-blank.
        row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        // Deliberately NOTHING else. Annotating a cook you already did is not cooking again:
        // no TimesCooked bump, no LastCookedAt touch, no new log row. Pinned by
        // Editing_a_note_leaves_the_cooked_count_alone.
        await _db.SaveChangesAsync(cancellationToken);

        var recipe = await _db.Recipes
            .Where(r => r.Id == row.RecipeId)
            .Select(r => new { r.ImageUrl })
            .SingleOrDefaultAsync(cancellationToken);

        return MealPlanResult<CookLogResponse>.Success(
            ToResponse(row, recipe?.ImageUrl, recipeAvailable: recipe is not null));
    }

    public async Task<MealPlanResult<bool>> UncookEntryAsync(
        Guid mealPlanEntryId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // The same ownership question LogAsync asks, and for the same reason: an entry id
        // that is not on one of the caller's own plans is NotFound, never Forbidden.
        var ownsEntry = await _db.MealPlanEntries
            .AnyAsync(e => e.Id == mealPlanEntryId && e.MealPlan.UserId == currentUserId, cancellationToken);
        if (!ownsEntry)
        {
            return MealPlanResult<bool>.NotFound();
        }

        var rows = await _db.CookLogs
            .Where(cl => cl.UserId == currentUserId && cl.MealPlanEntryId == mealPlanEntryId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            // Already the desired end state. Success, not NotFound — see the interface note.
            return MealPlanResult<bool>.Success(true);
        }

        _db.CookLogs.RemoveRange(rows);

        // Symmetric with LogAsync's bump, in the SAME SaveChanges, and by the number of rows
        // actually removed — not by one. Floored at 0 because TimesCooked and the CookLog row
        // count for a recipe are NOT guaranteed to move together, and this is reachable over
        // plain HTTP, not just via legacy data:
        //   1. A user who cooked something before CookLog existed has a count with no rows
        //      behind it.
        //   2. SocialService.ClearCookedAsync deletes the CookedRecipe row outright but never
        //      touches CookLog — cook via entry A, DELETE /recipes/{id}/cooked (orphans A's
        //      row), cook again via entry B (recreates the aggregate at TimesCooked = 1),
        //      un-cook A (drops to 0), un-cook B: unfloored that is -1, over a real HTTP
        //      sequence with no direct-db access anywhere.
        // Do not read this list as closed — it is not a fixed, enumerable set of causes, it is
        // the consequence of TimesCooked and CookLog living as two separately-writable records
        // of the same fact. (Path 2 above is closed by Task 3 making ClearCookedAsync delete
        // the caller's CookLog rows too, but the floor stays: nothing enforces that every
        // future writer of either table keeps the pair in lockstep, and a floor that would
        // matter again after the next path opens is not a floor to remove now.)
        var recipeIds = rows.Select(r => r.RecipeId).Distinct().ToList();
        var aggregates = await _db.CookedRecipes
            .Where(cr => cr.UserId == currentUserId && recipeIds.Contains(cr.RecipeId))
            .ToListAsync(cancellationToken);
        foreach (var aggregate in aggregates)
        {
            var removed = rows.Count(r => r.RecipeId == aggregate.RecipeId);
            aggregate.TimesCooked = Math.Max(0, aggregate.TimesCooked - removed);
        }

        // Rating, RatedAt and the rank award are deliberately untouched: cooking awards no
        // rank (see SocialService.MarkCookedAsync), so there is nothing to revert.
        await _db.SaveChangesAsync(cancellationToken);

        return MealPlanResult<bool>.Success(true);
    }

    // The shared read shape, applied LAST — after Where/OrderBy/Take. Projecting first and
    // then filtering the projection is what EF cannot translate here (it fails at runtime, not
    // at compile time, so the shape matters); every read below therefore narrows the entity
    // query and only then selects.
    //
    // Both extras reach for the recipe through its global soft-delete filter, which is the
    // point: a row whose recipe is gone comes back with a null image and RecipeAvailable
    // false rather than dropping out of the list. The plan and shopping surfaces correctly
    // drop soft-deleted dishes — you cannot cook one — but a record of what you ALREADY did
    // must not delete itself, which is why RecipeTitle is snapshotted on the row.
    // Correlated SUBQUERIES, not the cl.Recipe navigation. The navigation is a required
    // relationship, so projecting through it compiles to an INNER join, and a soft-deleted
    // recipe then drops the whole cook out of the caller's own history — silently, and only
    // for the rows that most need to survive. A_soft_deleted_recipe_leaves_the_cook_readable
    // caught exactly that; do not "simplify" this back to cl.Recipe.ImageUrl.
    private IQueryable<ReadRow> Project(IQueryable<CookLog> rows) =>
        rows.Select(cl => new ReadRow(
            cl,
            _db.Recipes.Where(r => r.Id == cl.RecipeId).Select(r => r.ImageUrl).FirstOrDefault(),
            _db.Recipes.Any(r => r.Id == cl.RecipeId)));

    private sealed record ReadRow(CookLog Row, string? ImageUrl, bool RecipeAvailable);

    private static CookLogResponse ToResponse(CookLog row, string? imageUrl, bool recipeAvailable) =>
        new(row.Id,
            row.RecipeId,
            row.RecipeTitle,
            imageUrl,
            row.MealPlanEntryId,
            row.CookedAt,
            row.Note,
            recipeAvailable);
}
