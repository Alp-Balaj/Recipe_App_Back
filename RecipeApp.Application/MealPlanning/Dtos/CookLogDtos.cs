namespace RecipeApp.Application.MealPlanning.Dtos;

// Wire contracts for the cook log (plan-page redesign / roadmap spec 2, 2026-08-10).
// Positional records, appended-last on any future change, per the house rule.

/// <summary>Log a cook. <paramref name="MealPlanEntryId"/> is null for an ad-hoc cook.</summary>
public record LogCookRequest(Guid RecipeId, Guid? MealPlanEntryId);

/// <summary>Set (or clear, with null) the note on one logged cook.</summary>
public record UpdateCookNoteRequest(string? Note);

// RecipeTitle is the SNAPSHOT taken at write time, not a join — it is what the row still has
// once a recipe is soft-deleted. RecipeImageUrl IS a join, so it goes null in that case, which
// is why the two are not carried as one nested recipe object: they have different lifetimes,
// and a nested object would have to be nullable as a whole and take the title down with it.
// RecipeAvailable is false once the recipe is gone: the row still renders (title, date, note)
// but the client must not offer to open or re-cook a dish that no longer exists.
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
