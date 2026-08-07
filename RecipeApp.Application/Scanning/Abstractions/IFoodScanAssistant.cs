using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;

namespace RecipeApp.Application.Scanning.Abstractions;

/// <summary>
/// The scanner's construction seam (stream N, D13) — the sixth assistant seam, in the same
/// mould as <see cref="IRecipeExtractionAssistant"/>: the prompt, the schema, the parse and
/// the defensive re-validation live in the implementation, and only the network sits behind
/// the vision caller beneath it.
///
/// One interface for both scan modes rather than one each, exactly as the extraction
/// assistant holds both the page and the photo path: the two methods share a discipline
/// (transcribe what is visible, never invent), a provider seam and a fake, and splitting
/// them would double all three to express a distinction the type system already carries in
/// the return types.
///
/// The discipline, stated once here because both prompts are built on it: this seam READS
/// photographs, it does not COMPOSE from them. A pantry photo with no food in it returns an
/// empty list — it does not invent the pantry a kitchen "must surely" have. That is D13's
/// load-bearing split: detection is the only part of the scanner that needs a model, and
/// everything the user can check (matching, coverage, the shopping-list rows) is computed
/// deterministically from what this seam returns.
/// </summary>
public interface IFoodScanAssistant
{
    /// <summary>
    /// Names the food items VISIBLE in the photographs — a fridge shelf, a counter, a
    /// cupboard. An empty list is a correct reading of a photo with no food in it.
    /// </summary>
    Task<PantryDetection> DetectPantryAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcribes the purchased grocery lines of a shop receipt, as printed. Totals, tax,
    /// payment and loyalty lines are not purchases and do not appear.
    /// </summary>
    Task<ReceiptRead> ReadReceiptAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default);
}

/// <summary>What a pantry photo contained, plus what the call cost.</summary>
public record PantryDetection(List<string> Names, ChatTokenUsage? Usage);

/// <summary>
/// One purchased line off a receipt. <paramref name="Quantity"/> is FREE TEXT as printed
/// ("2 x 500g") — a manual shopping-list row's quantity is a note to self, not a
/// measurement, and typing it here would invent structure the receipt never carried.
/// </summary>
public record ReceiptLine(string Name, string? Quantity);

/// <summary>What a receipt photo contained, plus what the call cost.</summary>
public record ReceiptRead(List<ReceiptLine> Items, ChatTokenUsage? Usage);
