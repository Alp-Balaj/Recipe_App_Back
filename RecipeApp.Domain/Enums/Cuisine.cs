namespace RecipeApp.Domain.Enums;

/// <summary>
/// The closed vocabulary for <c>Recipe.CuisineType</c>, replacing the free-text string
/// (decision D10).
///
/// A cuisine list is a judgement call with no correct answer, so the rule applied here is
/// narrow: a member earns its place by being something a user would FILTER by. That is what
/// the field is for — GET /recipes takes a cuisine filter, and a value nobody filters on is
/// a tag, not a cuisine.
///
/// Regions appear where the individual national cuisines would be too fine to fill
/// (MiddleEastern, NorthAfrican, EasternEuropean); the countries with their own entries are
/// the ones with enough distinct dishes to stand alone. <see cref="Other"/> is the honest
/// escape for a dish that belongs to a real cuisine not listed, and the field stays NULLABLE
/// for the commoner case of a dish that belongs to no cuisine at all — those are different
/// answers and collapsing them would lose the difference.
///
/// Persisted by name via HasConversion&lt;string&gt;(). Append freely; do not reorder.
/// </summary>
public enum Cuisine
{
    American,
    British,
    Caribbean,
    Chinese,
    EasternEuropean,
    French,
    German,
    Greek,
    Indian,
    Italian,
    Japanese,
    Korean,
    Mediterranean,
    Mexican,
    MiddleEastern,
    NorthAfrican,
    Nordic,
    Portuguese,
    Spanish,
    Thai,
    Turkish,
    Vietnamese,
    Other,
}
