using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Abstractions;

// The cook log (plan-page redesign / roadmap spec 2, 2026-08-10) — one row per cook event,
// behind /plan's "How did it go?" card and /plan/cooks.
//
// Returns MealPlanResult<T> like its neighbours: NotFound covers "doesn't exist" and "not the
// caller's" alike, because cook-log rows have no visibility tier and 404-never-403 applies
// uniformly (Decisions/meal-planning-v1-semantics.md).
public interface ICookLogService
{
    /// <summary>
    /// Records a cook: writes a log row AND bumps the caller's CookedRecipe aggregate, in one
    /// transaction. NotFound when the recipe is not visible to the caller, or when
    /// <paramref name="mealPlanEntryId"/> is given but is not an entry on one of their plans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The aggregate bump is what keeps /plan and the recipe page telling the same story: a
    /// meal marked cooked here must show up in "you've cooked this 3 times" there. Both writes
    /// or neither — a log row without the bump is the drift this design exists to prevent.
    /// </para>
    /// <para>
    /// <paramref name="cookedAt"/> null means now. Supplied, it records a cook that already
    /// happened (KAN-6): only its DATE is kept, and it is Invalid outside the window between
    /// the caller's account creation and today. A backdated cook must not reorder Cooked, so
    /// the aggregate takes the LATER last-cooked and the EARLIER first-cooked rather than
    /// stamping both with the write.
    /// </para>
    /// </remarks>
    Task<MealPlanResult<CookLogResponse>> LogAsync(
        Guid recipeId,
        Guid? mealPlanEntryId,
        DateTime? cookedAt,
        string? note,
        Guid currentUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's cooks, newest first, keyset-paged. <paramref name="recipeId"/> narrows the
    /// list to one dish (KAN-5, design D9) and is null for the whole log.
    /// </summary>
    /// <remarks>
    /// Deliberately a PARAMETER on the existing list rather than a second endpoint: the dish
    /// page reads the same rows in the same order through the same (CookedAt, Id) cursor, and
    /// the only thing that differs is how many of the caller's cooks are in scope. The filter
    /// narrows the CALLER's log; it never widens it to the recipe's, so another user's cooks of
    /// the same recipe — and their private notes — stay out.
    /// </remarks>
    Task<MealPlanResult<CookLogListResponse>> ListAsync(
        Guid currentUserId, KeysetCursor? cursor, int limit, Guid? recipeId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest row plus the caller's lifetime cook count — everything the "How did it go?"
    /// card renders, in one request. Always Success; an empty log is a null Latest, not a 404.
    /// </summary>
    Task<MealPlanResult<CookLogLatestResponse>> GetLatestAsync(
        Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears the note on one logged cook. Touches NO counter — annotating a cook you
    /// already did is not cooking again.
    /// </summary>
    Task<MealPlanResult<CookLogResponse>> UpdateNoteAsync(
        Guid id, string? note, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every cook the caller logged against one plan entry, decrements the per-recipe
    /// aggregate by exactly that many (floored at 0), and reports each affected recipe's
    /// resulting state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent: un-cooking an entry that was never cooked is Success, not NotFound —
    /// NotFound is reserved for an entry that is not on one of the caller's own plans, so
    /// the two answers stay distinguishable. Deleting ALL matching rows rather than one is
    /// what makes a double-tapped cook fully reversible: CookLog has no unique key, so a
    /// single-row delete could leave the entry looking cooked after the user un-cooked it.
    /// Rating is untouched — it lives on CookedRecipe and is a separate claim.
    /// </para>
    /// <para>
    /// The reply is KAN-18 and is EMPTY for the idempotent no-op: no rows went, so no recipe's
    /// state changed and there is nothing for a client to patch. See <see cref="UncookAsync"/>
    /// for why the state is reported at all rather than derived by the caller.
    /// </para>
    /// </remarks>
    Task<MealPlanResult<UncookResponse>> UncookEntryAsync(Guid mealPlanEntryId, Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes ONE logged cook of the caller's own, decrements the per-recipe aggregate by
    /// one (floored at 0), and reports the dish's resulting state. NotFound when the row is not
    /// the caller's, or is already gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KAN-14 — the cook-id-scoped sibling of <see cref="UncookEntryAsync"/>. That one can
    /// only reach a cook that satisfied a plan slot, so a cook logged from cook mode on an
    /// unplanned recipe had no undo at all: the only gesture wide enough to reach it was
    /// <c>DELETE /recipes/{id}/cooked</c>, which means "I have never cooked this" and takes
    /// every cook of the dish and every note with it. That gesture keeps its width and its
    /// meaning (ADR-0002); this is the narrow one its own comment asked for.
    /// </para>
    /// <para>
    /// NOT idempotent, unlike its sibling, and the difference is the scope rather than an
    /// inconsistency. A plan entry outlives its cooks, so "no cooks against this slot" is a
    /// state it can still be in and a repeat is Success; a log row does not outlive its own
    /// deletion, so a second call is asking about something that is not there. NotFound
    /// covers "not yours" and "already gone" alike, per this interface's 404-never-403 note.
    /// </para>
    /// <para>
    /// Rating is untouched, for <see cref="UncookEntryAsync"/>'s reason: un-cooking is not a
    /// retraction of an opinion. ADR-0004 says so explicitly — a rated row may sit at zero
    /// cooks; what its owner cannot do is re-rate until they record another cook.
    /// </para>
    /// <para>
    /// KAN-18 — the reply carries the dish's state AFTER the write, always naming the recipe
    /// even when no aggregate row was found (that is the strongest "you have not cooked this",
    /// not an absence of news). The client cannot work this out for itself: since KAN-13
    /// "cooked" is <c>TimesCooked &gt; 0</c> (ADR-0005), a rule the server owns and has already
    /// changed once, and the SPA prefers a cache patch over a later read — so a client deriving
    /// the flag locally would overwrite the correct answer and outlive the fetch that should
    /// have healed it. Same argument, and the same shape, as
    /// <c>ISocialService.ClearRatingAsync</c>. See docs/adr/0006.
    /// </para>
    /// </remarks>
    Task<MealPlanResult<UncookResponse>> UncookAsync(Guid cookLogId, Guid currentUserId, CancellationToken cancellationToken = default);
}
