namespace RecipeApp.Application.MealPlanning.Dtos;

// Wire contracts for the cook log (plan-page redesign / roadmap spec 2, 2026-08-10).
// Positional records, appended-last on any future change, per the house rule.

/// <summary>Log a cook. <paramref name="MealPlanEntryId"/> is null for an ad-hoc cook.</summary>
public record LogCookRequest(Guid RecipeId, Guid? MealPlanEntryId);

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
