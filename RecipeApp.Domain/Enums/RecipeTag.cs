namespace RecipeApp.Domain.Enums;

/// <summary>
/// The curated tag vocabulary for <c>Recipe.Tags</c> (decision D10).
///
/// Alp chose a closed vocabulary over open tags, against the advice recorded in the horizon
/// document, and it is built as chosen. What the closure buys is the one thing open tags
/// could never do: tag filtering on GET /recipes is match-ALL and case-SENSITIVE, so
/// "one-pot", "One Pot" and "onepot" were three unrelated facets of one idea and every
/// filter silently under-returned. A fixed set makes a tag filter mean something.
///
/// What it costs is the tag nobody anticipated, which is a real loss — the mitigation is
/// that this list is APPEND-ONLY and cheap to extend, so a missing tag is a one-line change
/// rather than a design argument.
///
/// The members group as: course, form, method, occasion, and claim. Deliberately excluded
/// are anything a typed field already answers — no cuisine tags (Recipe.CuisineType), no
/// difficulty tags (Recipe.Difficulty), and no diet tags that duplicate
/// <see cref="DietaryRestriction"/> beyond the two a recipe genuinely advertises about
/// itself. A tag that restates a column is a second source of truth for it.
///
/// Persisted by name inside jsonb — see RecipeAppDataSource.
/// </summary>
public enum RecipeTag
{
    // ── Course ──────────────────────────────────────────────────────────────────────
    Breakfast,
    Brunch,
    Lunch,
    Dinner,
    Appetizer,
    SideDish,
    Dessert,
    Snack,
    Drink,

    // ── Form ────────────────────────────────────────────────────────────────────────
    Salad,
    Soup,
    Stew,
    Sandwich,
    Pasta,
    Pizza,
    Curry,
    Bread,
    Cake,

    // ── Method ──────────────────────────────────────────────────────────────────────
    Baking,
    Grilling,
    Roasting,
    Frying,
    SlowCooker,
    OnePot,
    NoCook,
    MealPrep,

    // ── Occasion ────────────────────────────────────────────────────────────────────
    Quick,
    Budget,
    Comfort,
    KidFriendly,
    PartyFood,
    Holiday,
    Leftovers,

    // ── Claim ───────────────────────────────────────────────────────────────────────
    // Vegetarian and Vegan overlap DietaryRestriction on purpose and are the only two that
    // do: they are what a recipe ADVERTISES about itself to a browsing stranger, which is a
    // different act from a user recording what they cannot eat. The rest of the restriction
    // list stays off this enum precisely to avoid a recipe claiming "GlutenFree" as a tag
    // that no ingredient check backs — that claim is G4's to make, from the catalogue.
    Vegetarian,
    Vegan,
    HighProtein,
    LowCalorie,
    Spicy,
    Healthy,
}
