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

    /// <summary>
    /// The time of day every backdated cook is stored at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A day is not a moment, and the client picked a day. Midday is the hour with the most
    /// clearance in both directions: rendered in local time it stays on the chosen day for every
    /// offset from UTC-12 to UTC+11. Midnight would be wrong for half the planet, in the
    /// direction that silently reports yesterday's dinner as the day before.
    /// </para>
    /// <para>
    /// It is NOT wrong-proof, and the limit is arithmetic rather than a choice worth revisiting:
    /// inhabited offsets span UTC-11 to UTC+14, which is 25 hours, so no single stored instant
    /// can render as the same calendar day for all of them. Midday leaves UTC+12 to UTC+14
    /// (New Zealand, Fiji, Kiribati) reading a backdated cook as the following day. Closing that
    /// needs a date-only column beside this one, not a different hour — every hour fails
    /// somewhere. The CEILING's day of slack is the separate, solvable half of the same problem:
    /// it is about which dates those users may SEND, not how the result reads back.
    /// </para>
    /// </remarks>
    private static readonly TimeOnly StoredTimeOfDay = new(12, 0);

    public async Task<MealPlanResult<CookLogResponse>> LogAsync(
        Guid recipeId,
        Guid? mealPlanEntryId,
        DateTime? cookedAt,
        string? note,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        // Same visibility expression as adding a dish to a plan, for the same reason: you can
        // log a cook of exactly the recipes you can open. Soft-deleted falls out via the
        // global query filter.
        //
        // ImageUrl comes back from THIS query, the gated one, rather than a second read after
        // the write. Two reads could straddle the author flipping the recipe to Private, and
        // the response would then carry an image the caller may no longer see — the same leak
        // Project and UpdateNoteAsync were changed to close.
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .Where(r => r.Id == recipeId)
            .Select(r => new { r.Id, r.Title, r.ImageUrl })
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

        // KAN-6. A supplied date is resolved LAST, after both ownership gates above, so a 400
        // about dates can never be the thing that tells a caller a recipe they cannot see exists.
        var resolved = await ResolveCookedAtAsync(cookedAt, currentUserId, cancellationToken);
        if (resolved.Outcome is not MealPlanOutcome.Success)
        {
            return MealPlanResult<CookLogResponse>.Invalid(resolved.Detail!);
        }

        var cookTime = resolved.Value;

        var row = new CookLog
        {
            Id = Guid.NewGuid(),
            UserId = currentUserId,
            RecipeId = recipe.Id,
            RecipeTitle = recipe.Title,
            MealPlanEntryId = mealPlanEntryId,
            CookedAt = cookTime,
            // Same normalisation as UpdateNoteAsync, and for the same reason: "no note" has one
            // representation, so a whitespace-only note does not render as an empty quotation.
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
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
                FirstCookedAt = cookTime,
                LastCookedAt = cookTime,
            });
        }
        else
        {
            aggregate.TimesCooked += 1;

            // KAN-6, and the reason a backdated cook does not disturb Cooked. This used to stamp
            // LastCookedAt with the moment of the write unconditionally, which was indistinguish-
            // able from the right answer while every cook was "now" — and catastrophic the moment
            // one is not. Cooked is ordered on (LastCookedAt, RecipeId), so an unconditional
            // stamp would shove a dish cooked two years ago to the top of a most-recently-cooked
            // list: the one thing that ordering exists to prevent, and the exact reason a user
            // filling in their history would stop trusting the page they were filling in.
            //
            // Widening rather than replacing: the LATER end moves forward, the EARLIER end moves
            // back, and a cook that falls between them moves neither. Applied to every cook, not
            // only backdated ones — for a now-cook the max is "now" and the min is unchanged, so
            // there is no second code path to keep in step with this one.
            aggregate.LastCookedAt = cookTime > aggregate.LastCookedAt ? cookTime : aggregate.LastCookedAt;
            aggregate.FirstCookedAt = cookTime < aggregate.FirstCookedAt ? cookTime : aggregate.FirstCookedAt;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Available by construction: the gate above already proved this caller can open this
        // recipe, and the image travelled with it.
        return MealPlanResult<CookLogResponse>.Success(
            ToResponse(row, recipe.ImageUrl, recipeAvailable: true));
    }

    /// <summary>
    /// Turns the client's optional date into the instant the cook is stored at, or a sentence
    /// explaining why it cannot be (KAN-6).
    /// </summary>
    /// <returns>
    /// The layer's own <see cref="MealPlanResult{T}"/> rather than a tuple, so the failure travels
    /// in the shape LogAsync already returns and there is no second sentinel — a bare
    /// <c>(DateTime, string?)</c> would carry <c>default(DateTime)</c> on the failing path, which
    /// is a legal timestamp waiting to be used by a caller that forgot to check.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Both bounds are INVENTED here, and that is worth saying out loud because it is the reason
    /// they are spelled out at this length. Nothing else in this API takes a client-supplied
    /// historical timestamp: every other client date is a bucket key (a week start) or a
    /// watermark (notifications read up to), neither of which has a floor or a ceiling. There
    /// was no convention to copy, so there is nowhere else to look these up.
    /// </para>
    /// <para>
    /// The comparisons are between DAYS, never between instants. The client picked a day, the
    /// row is stored at midday on that day, and an instant comparison against a midday stamp
    /// would refuse or accept a date for reasons that have nothing to do with the date — see
    /// each bound below.
    /// </para>
    /// </remarks>
    private async Task<MealPlanResult<DateTime>> ResolveCookedAtAsync(
        DateTime? cookedAt, Guid currentUserId, CancellationToken cancellationToken)
    {
        // No date is the pre-KAN-6 contract and stays exactly as it was: the moment of the write,
        // to the tick. NOT normalised to midday — cook mode's cooks would all jump by up to
        // twelve hours, and /cook-log is ordered on CookedAt, so a day's worth of them would
        // collapse into one indistinguishable tie.
        if (cookedAt is null)
        {
            return MealPlanResult<DateTime>.Success(DateTime.UtcNow);
        }

        // Kind is already proven Utc by LogCookRequestValidator; DateOnly.FromDateTime ignores it
        // either way, and the stamp below re-specifies Utc explicitly rather than inheriting.
        var day = DateOnly.FromDateTime(cookedAt.Value);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The ceiling, with one day of slack. Not sloppiness — the slack IS the timezone
        // correction. A user at UTC+13 or +14 spends part of every day on a calendar date this
        // server has not reached, so their perfectly ordinary "I cooked this tonight" arrives as
        // tomorrow. Refusing it would make the feature unusable across the date line for hours at
        // a time, and would do it as a validation error blaming the user for their own timezone.
        // Two days ahead is beyond every offset that exists, so that is where the line goes.
        if (day > today.AddDays(1))
        {
            return MealPlanResult<DateTime>.Invalid("You cannot record a cook in the future.");
        }

        // The floor: the caller's own account. Read as a DAY, so someone who registered at 09:00
        // this morning can still record last night's dinner — comparing instants would refuse it,
        // and would refuse it only for users who signed up after midday, which is precisely the
        // kind of bound that never gets reproduced from a report.
        var accountCreatedAt = await _db.Users
            .Where(u => u.Id == currentUserId)
            .Select(u => u.CreatedAt)
            .SingleAsync(cancellationToken);

        if (day < DateOnly.FromDateTime(accountCreatedAt))
        {
            return MealPlanResult<DateTime>.Invalid(
                "You cannot record a cook from before your account existed.");
        }

        return MealPlanResult<DateTime>.Success(day.ToDateTime(StoredTimeOfDay, DateTimeKind.Utc));
    }

    public async Task<MealPlanResult<CookLogListResponse>> ListAsync(
        Guid currentUserId, KeysetCursor? cursor, int limit, Guid? recipeId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.CookLogs.Where(cl => cl.UserId == currentUserId);

        // KAN-5 / design D9 — the dish page is this list with ONE predicate on it. It narrows
        // the caller's own log and is stacked UNDER the UserId scope above, never instead of
        // it: a note is private to its author (CONTEXT.md), so filtering by recipe alone would
        // turn a per-dish view into everyone's cooks of that recipe.
        //
        // It lands on the (UserId, RecipeId, CookedAt DESC, Id DESC) index KAN-4's migration
        // added for the per-dish note subquery, which is why this needs no migration of its own.
        if (recipeId is not null)
        {
            query = query.Where(cl => cl.RecipeId == recipeId);
        }

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
                    .Take(limit + 1),
                currentUserId)
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
                    .ThenByDescending(cl => cl.Id),
                currentUserId)
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

        // Note the asymmetry, and that it is deliberate. The WRITE above is ungated: annotating
        // your own cook is your own record, so it stays possible after the author withdraws the
        // recipe (ADR-0001 — destructive-or-own-row writes are ungated, creating writes are
        // not). What the write REPORTS back is gated, by the same visibility Project applies,
        // so a client that re-renders from this response cannot come away believing the dish
        // became openable again.
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
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
        // count for a recipe are NOT guaranteed to move together. One live cause used to be
        // reachable over plain HTTP with no direct-db access anywhere: SocialService
        // .ClearCookedAsync deleted the CookedRecipe row outright but never touched CookLog —
        // cook via entry A, DELETE /recipes/{id}/cooked (orphaned A's row), cook again via
        // entry B (recreated the aggregate at TimesCooked = 1), un-cook A (dropped to 0),
        // un-cook B: unfloored that is -1. Task 3 closed exactly that path by making
        // ClearCookedAsync delete the caller's CookLog rows too.
        //
        // The floor stays regardless, because the underlying set of causes is not closed —
        // it is not a fixed, enumerable list, it is the consequence of TimesCooked and CookLog
        // living as two separately-writable records of the same fact:
        //   - A user who cooked something before CookLog existed has a count with no rows
        //     behind it (legacy data, permanent).
        //   - The two tables remain separately writable; nothing enforces that every future
        //     writer of either one keeps the pair in lockstep.
        // A floor that would matter again the moment a new path opens is not a floor to remove
        // now.
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

    public async Task<MealPlanResult<bool>> UncookAsync(
        Guid cookLogId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Read untracked, and write with set-based statements below, for ClearRatingAsync's
        // reason: two deletes of the same cook can genuinely be in flight together (a
        // double-tapped undo), and a tracked Remove that loses that race throws
        // DbUpdateConcurrencyException — a 500 on a gesture whose whole job is to be the safe
        // way out of a mis-tap. The read is only for the RecipeId; the deletion itself is
        // decided by the statement.
        //
        // Scoped to the caller on the way IN, not checked afterwards: a row that is not
        // theirs must never be counted as found. No visibility check on the recipe, and that
        // is ADR-0001 rather than an omission — this destroys a row of the caller's own, so
        // an author withdrawing their recipe must not strand a cook its owner can no longer
        // take back. Project() already makes the same call from the read side.
        var row = await _db.CookLogs
            .AsNoTracking()
            .Where(cl => cl.Id == cookLogId && cl.UserId == currentUserId)
            .Select(cl => new { cl.RecipeId })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return MealPlanResult<bool>.NotFound();
        }

        // The two statements are one transaction because they are one fact. LogAsync writes
        // the row and the bump in a single SaveChanges for exactly this reason ("both rows or
        // neither"), and an undo that removed the cook but kept the count would leave the
        // drift this design exists to prevent — pointing the wrong way, and permanently,
        // since nothing recomputes TimesCooked from the log.
        //
        // The only explicit transaction in the codebase, because it is the only place two
        // set-based statements have to land together; SaveChanges wraps its own, and
        // ExecuteDelete/ExecuteUpdate do not enrol in one. Safe without an execution-strategy
        // wrapper: the Npgsql registration turns on no retrying strategy (Program.cs).
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var deleted = await _db.CookLogs
            .Where(cl => cl.Id == cookLogId && cl.UserId == currentUserId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            // The loser of the race above: the row was there when we read it and is gone now.
            // Same answer as never having existed, which is what the caller's next list read
            // will agree with.
            return MealPlanResult<bool>.NotFound();
        }

        // The floor lives in the PREDICATE rather than in a Math.Max over a value this
        // request read, so two concurrent un-logs of different cooks of the same dish cannot
        // both compute "2 - 1" from the same stale 2. Same floor as UncookEntryAsync and for
        // the same open-ended reason — see its comment: TimesCooked and the log are two
        // separately-writable records of one fact and are not guaranteed to move together
        // (a cook from before CookLog existed has a count with no row behind it).
        //
        // No aggregate row at all is a no-op, not an error. The drift runs in that direction
        // too, and a cook whose aggregate was already cleared is still the caller's to delete.
        await _db.CookedRecipes
            .Where(cr => cr.UserId == currentUserId && cr.RecipeId == row.RecipeId && cr.TimesCooked > 0)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(cr => cr.TimesCooked, cr => cr.TimesCooked - 1),
                cancellationToken);

        // LastCookedAt and FirstCookedAt are deliberately NOT recomputed, even when the cook
        // just removed was the one they name. They can only be recomputed from the log, and
        // the log is not the whole record: a dish cooked twice before CookLog existed and
        // once since has one row to recompute from, so "narrow the range to what the log
        // holds" would report the dish as first cooked this month. ADR-0003 has the range
        // WIDENING only, which is the same rule read forwards. The cost is a watermark that
        // can outlive the cook that set it; the alternative is one that invents a date.
        //
        // The CookedRecipe row itself stays, too, even when this takes the count to zero and
        // nothing was ever rated. UncookEntryAsync leaves it (pinned by
        // Un_cooking_a_double_tapped_entry_removes_every_row) and two sibling gestures
        // answering differently is worse than the row: a zero-cook row still reads as
        // CookedByMe (row existence, see SocialDtos), which is the phantom the KAN-13/KAN-16
        // family is about and is not this ticket's to close on one path only.
        // No SaveChanges: both writes above were statements, and the read was untracked.
        await transaction.CommitAsync(cancellationToken);

        return MealPlanResult<bool>.Success(true);
    }

    // The shared read shape, applied LAST — after Where/OrderBy/Take. Projecting first and
    // then filtering the projection is what EF cannot translate here (it fails at runtime, not
    // at compile time, so the shape matters); every read below therefore narrows the entity
    // query and only then selects.
    //
    // Both extras read the recipe through `readable` — the caller's OWN visibility, not the
    // bare row. That is the whole of KAN-2 and of ADR-0001's first consequence: "available"
    // means "you can open this", never "the row exists and is not soft-deleted". A recipe its
    // author flipped to Private is as unopenable as a removed one, so it must read the same
    // way — same RecipeAvailable false, same withheld image, and (crucially) the same on the
    // wire, because reporting an author's private visibility decision back to a stranger is
    // itself a leak. The two cases are ONE user-visible state (design D14). Before this, a
    // private recipe stayed available, kept serving its photo to people who could no longer
    // open it, and left "Cook it again" enabled to 404 on press.
    //
    // Composed as one visibility-filtered IQueryable rather than repeating the predicate,
    // per RecipeVisibilityPolicy's own instruction: a divergent hand-written copy is how a
    // private recipe leaks. Soft deletion still falls out underneath it, via the DbSet's
    // global query filter — the two rules stack, they do not replace each other.
    //
    // What must NOT follow visibility is the row itself. A cook whose recipe went private is
    // still a fact about the user, so it keeps rendering from CookLog.RecipeTitle, keeps its
    // note, and stays annotatable — ADR-0001 withdraws the author's content, never the
    // reader's record. The plan and shopping surfaces correctly drop dishes you cannot cook;
    // a record of what you ALREADY did must not delete itself.
    //
    // Correlated SUBQUERIES, not the cl.Recipe navigation. The navigation is a required
    // relationship, so projecting through it compiles to an INNER join, and an unreadable
    // recipe then drops the whole cook out of the caller's own history — silently, and only
    // for the rows that most need to survive. A_soft_deleted_recipe_leaves_the_cook_readable
    // caught exactly that; do not "simplify" this back to cl.Recipe.ImageUrl.
    private IQueryable<ReadRow> Project(IQueryable<CookLog> rows, Guid currentUserId)
    {
        var readable = _db.Recipes.Where(RecipeVisibilityPolicy.VisibleTo(currentUserId));

        return rows.Select(cl => new ReadRow(
            cl,
            readable.Where(r => r.Id == cl.RecipeId).Select(r => r.ImageUrl).FirstOrDefault(),
            readable.Any(r => r.Id == cl.RecipeId)));
    }

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
