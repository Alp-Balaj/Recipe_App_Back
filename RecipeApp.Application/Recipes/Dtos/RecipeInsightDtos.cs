using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Recipes.Dtos;

/// <summary>
/// Nutrition COMPUTED from the catalogue (stream G, slice G4) — the thing the
/// horizon document says is "a different and far more defensible thing to put in a
/// thesis than asking a model for a calorie count".
///
/// Every figure is per serving and every one is nullable. <see cref="CoveredLines"/>
/// and <see cref="TotalLines"/> are not decoration: a total computed from 3 of 11
/// ingredients is nearly meaningless, and a client that renders the number without
/// the coverage would be making exactly the confident claim this design refuses.
/// The recipe's own hand-typed CaloriesPerServing is untouched and still returned
/// by /recipes/{id} — this sits beside it rather than overwriting it, because they
/// answer different questions ("what the author said" and "what the ingredients
/// add up to").
/// </summary>
public record ComputedNutritionResponse(
    int? KcalPerServing,
    double? ProteinGPerServing,
    double? FatGPerServing,
    double? CarbsGPerServing,
    double? FibreGPerServing,
    int CoveredLines,
    int TotalLines);

/// <summary>
/// One ingredient line that conflicts with a restriction, and why.
/// </summary>
public record DietaryConflictResponse(string IngredientName, string Reason);

/// <summary>
/// One restriction's verdict. <c>Conflicts</c> empty means nothing was FOUND — see
/// <c>UncheckableLines</c>, and see DietaryRules for why that is not compliance.
/// </summary>
public record DietaryCheckResponse(
    DietaryRestriction Restriction,
    IReadOnlyList<DietaryConflictResponse> Conflicts,
    int UncheckableLines);

/// <summary>
/// GET /recipes/{id}/insights — the two things the catalogue makes possible.
///
/// A separate endpoint rather than fields on RecipeResponse, deliberately: it costs
/// a catalogue join that every existing recipe read would otherwise pay, and
/// RecipeResponse is consumed by the feed, the planner and the picker, none of
/// which want it. Restrictions are the CALLER's own.
/// </summary>
public record RecipeInsightsResponse(
    ComputedNutritionResponse Nutrition,
    IReadOnlyList<DietaryCheckResponse> DietaryChecks);
