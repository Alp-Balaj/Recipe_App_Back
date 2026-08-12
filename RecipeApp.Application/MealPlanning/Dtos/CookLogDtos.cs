using RecipeApp.Application.Social.Dtos;

namespace RecipeApp.Application.MealPlanning.Dtos;

// Wire contracts for the cook log (plan-page redesign / roadmap spec 2, 2026-08-10).
// Positional records, appended-last on any future change, per the house rule.

/// <summary>Log a cook. <paramref name="MealPlanEntryId"/> is null for an ad-hoc cook.</summary>
/// <remarks>
/// <para>
/// The two trailing fields are KAN-6 — "record a cook that happened in the past" — and are
/// appended-last with defaults per the house rule, so every existing two-argument caller keeps
/// compiling and every existing client keeps meaning what it meant.
/// </para>
/// <para>
/// <paramref name="CookedAt"/> null means "now", which is what every caller before KAN-6 meant
/// implicitly. When supplied it must be a UTC timestamp (the validator's only rule) and the
/// service reads its DATE and nothing else — see CookLogService.LogAsync for why a day the user
/// typed is not a moment, and where the two bounds on it live.
/// </para>
/// <para>
/// <paramref name="Note"/> exists so a backdated cook can be annotated in the ONE gesture that
/// created it. PATCH /cook-log/{id} still works on the row afterwards and remains the only way
/// to change or clear a note; this is not a second note field, it is the initial value of the
/// same one.
/// </para>
/// </remarks>
public record LogCookRequest(
    Guid RecipeId,
    Guid? MealPlanEntryId,
    DateTime? CookedAt = null,
    string? Note = null);

/// <summary>Set (or clear, with null) the note on one logged cook.</summary>
public record UpdateCookNoteRequest(string? Note);

// RecipeTitle is the SNAPSHOT taken at write time, not a join — it is what the row still has
// once the recipe becomes unavailable. RecipeImageUrl IS a join, so it goes null in that case,
// which is why the two are not carried as one nested recipe object: they have different
// lifetimes, and a nested object would have to be nullable as a whole and take the title down
// with it.
//
// RecipeAvailable answers "can this caller OPEN the recipe", not "does the row exist" — it is
// false both when the author removed it and when the author stopped sharing it with this
// caller (KAN-2). The row still renders (title, date, note) but the client must not offer to
// open or re-cook it.
//
// Those two causes are deliberately ONE flag with no second field separating them, and no
// client should try to reconstruct the difference: telling a reader that an author turned a
// recipe Private reports that author's private decision to a stranger, which is itself the
// leak. See docs/adr/0001 and design decision D14.
//
// That is a property of THIS contract, not yet of the whole API. GET /meal-plans/{id} joins
// Recipes with no visibility filter, so a plan-linked cook can still be told apart by holding
// both responses: the plan drops a removed recipe out of its join but keeps serving a
// withdrawn one's title and photo. Closing that is KAN-1, and until it lands the guarantee
// above holds only for clients reading the cook log alone.
public record CookLogResponse(
    Guid Id,
    Guid RecipeId,
    string RecipeTitle,
    string? RecipeImageUrl,
    Guid? MealPlanEntryId,
    DateTime CookedAt,
    string? Note,
    bool RecipeAvailable);

public record CookLogListResponse(IReadOnlyList<CookLogResponse> Items, string? NextCursor);

// One request serves the whole "How did it go?" card: the row it renders, and the total the
// "All N cooks ›" link needs. Splitting them would make the card's two halves able to disagree
// while one request was still in flight.
//
// Latest is null (with TotalCount 0) on an empty log rather than the endpoint 204ing: the card
// distinguishes "you have never cooked" from "still loading", and a 204 gives the client an
// empty body to tell those apart from, which is exactly the cold-start trap MealPlanWeekPage
// documents.
public record CookLogLatestResponse(CookLogResponse? Latest, int TotalCount);

// KAN-18 — what BOTH un-log deletes answer with, in place of the 204 they used to send.
//
// Un-logging changes a fact the client is holding: since KAN-13 "you cooked this" is
// TimesCooked > 0, so removing the last cook flips it. Nothing in the reply meant the client
// could learn that, and the SPA's per-recipe social envelope is seeded once and never
// re-fetched (useSocialEnvelope's recorded no-resync limitation), so the recipe page went on
// saying "you cooked this" for the rest of the session. See docs/adr/0006.
//
// Reusing CookedRecipeResponse rather than declaring a slimmer pair of fields is the point:
// this is the SAME row POST/DELETE /recipes/{id}/cooked and DELETE /recipes/{id}/rating answer
// with, and the client patches one cache from all four. A second shape carrying the same facts
// is how the two copies start disagreeing — the drift KAN-13 and ADR-0005 exist to end.
//
// A LIST because the count is genuinely 0, 1 or more. The entry-scoped delete removes every
// cook logged against one plan slot, and a slot's rows carry the recipe they named at the time,
// so two are possible if the slot's recipe was swapped between cooks; the same delete is
// idempotent, and a repeat that changed nothing answers with an EMPTY list rather than
// restating a state no write moved. One recipe per entry, never duplicated — the client patches
// per recipe.
public record UncookResponse(IReadOnlyList<CookedRecipeResponse> Recipes);
