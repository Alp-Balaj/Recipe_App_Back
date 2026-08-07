using RecipeApp.Application.Chat.Dtos;

namespace RecipeApp.Application.Scanning.Dtos;

/// <summary>
/// One thing the model saw in a pantry photo. <see cref="IngredientId"/> is the catalogue
/// entry it resolved to through the EXISTING exact-match rule (IngredientKey → alias), and
/// null is an honest answer, not a failure: "we don't know this one" is surfaced to the
/// user rather than dropped, because a detection the catalogue has never heard of is still
/// something they have.
/// </summary>
public record DetectedIngredientResponse(string Name, Guid? IngredientId, string? CatalogueName);

/// <summary>
/// One recipe the caller could (mostly) cook. The counts are the whole point of the
/// deterministic matcher: "you have 7 of these 9" is checkable in a way a model's ranked
/// list is not, and the missing names say exactly what a shop would have to fill in.
/// </summary>
public record PantryMatchResponse(
    Guid RecipeId,
    string Title,
    string? ImageUrl,
    int TotalTimeMinutes,
    int? CaloriesPerServing,
    int MatchedIngredientCount,
    int TotalIngredientCount,
    List<string> MatchedIngredientNames,
    List<string> MissingIngredientNames);

/// <summary>
/// The pantry scan's answer. Nothing here was persisted (D19): the photo was read and
/// discarded, and this response is the entire product of the call. The budget envelope is
/// non-nullable because every scan spends — there is no free path whose envelope would be
/// a lie.
/// </summary>
public record PantryScanResponse(
    List<DetectedIngredientResponse> Detected,
    List<PantryMatchResponse> Matches,
    AiBudgetResponse Budget);

/// <summary>One draft shopping-list line read off a receipt, as printed.</summary>
public record ReceiptItemResponse(string Name, string? Quantity);

/// <summary>
/// A reviewable DRAFT, deliberately not a write. Receipts are full of things nobody wants
/// on a shopping list, so the user confirms line by line and the confirmed rows go through
/// the existing POST /shopping-list — which stays the only writer of ShoppingListItem rows,
/// the promise D13 made when it called this the cheapest landing zone in the app.
/// </summary>
public record ReceiptScanResponse(
    List<ReceiptItemResponse> Items,
    AiBudgetResponse Budget);
