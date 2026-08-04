namespace RecipeApp.Domain.Enums;

/// <summary>
/// The closed vocabulary for <c>User.DietaryRestrictions</c>, replacing the free-text list
/// (decision D10).
///
/// This is the vocabulary with the highest stakes in stream G. The value is injected into
/// every AI system prompt as an absolute constraint ("every ingredient must comply"), so
/// before this change a typo was not a cosmetic problem — "vegitarian" reached the model as
/// a word it had to honour and might quietly not, and nothing downstream could tell the
/// difference between a restriction that was respected and one that was misspelt.
///
/// Typing it also buys the thing slice G4 is for: a restriction the SYSTEM can check a
/// recipe against, rather than one only the model was asked to respect. That check needs a
/// fixed set of names to hang exclusion rules off — this is that set.
///
/// The members are the ones a kitchen actually plans around: three diet shapes, the common
/// allergen exclusions, two religious observances, and two macro targets. Persisted by name
/// inside jsonb — see RecipeAppDataSource.
/// </summary>
public enum DietaryRestriction
{
    Vegetarian,
    Vegan,
    Pescatarian,
    GlutenFree,
    DairyFree,
    NutFree,
    PeanutFree,
    EggFree,
    SoyFree,
    ShellfishFree,
    Halal,
    Kosher,
    LowCarb,
    LowSodium,
}
