using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Import;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// The AI seam for stream L, mirroring <see cref="IRecipeGenerationAssistant"/>: the
/// implementation owns the prompt, the structured-output schema, the parse and the defensive
/// re-validation; the network call sits one level lower behind the provider callers, so this
/// is unit-testable without a provider.
///
/// HOW ITS TRUST BOUNDARY DIFFERS FROM THE GENERATOR'S, which is the only thing worth knowing
/// before editing it. The generator is FREE — the model invents, nothing can be checked
/// against a known set, so range-testing every field is all the safety there is. Extraction is
/// neither free nor grounded: there IS a right answer, sitting on the page or in the
/// photograph, and the model can be wrong about it in a way the generator cannot. A generator
/// that returns "300 g flour" for a request about soup is odd; an extractor that returns
/// "300 g flour" for a page that said 200 g has corrupted somebody's recipe while looking
/// entirely plausible.
///
/// Nothing in this codebase can detect that, and pretending otherwise would be worse than
/// admitting it — so the answer is not a cleverer check, it is D15: the source URL travels
/// with the recipe permanently and is shown to every reader, so the original is one click
/// away and the import is always auditable against it. THAT is why provenance is a column
/// rather than a nice-to-have, and why the extraction prompt's overriding instruction is to
/// transcribe rather than improve.
///
/// Both methods throw <see cref="InvalidOperationException"/> when the output cannot be
/// salvaged into a recipe. The orchestrator funnels that to AssistantUnavailable, exactly as
/// the chat, meal-plan and generation lanes do.
/// </summary>
public interface IRecipeExtractionAssistant
{
    /// <summary>
    /// Tier 1's FALLBACK ONLY — reached when a page carried no usable JSON-LD. Never called
    /// for a page with structured data, which is what keeps the common import free.
    /// </summary>
    /// <param name="pageText">The page's visible text, already stripped of markup.</param>
    /// <param name="sourceUrl">Where it came from, for the model's context only. It is not
    /// what gets stored — the orchestrator owns provenance.</param>
    Task<ExtractedRecipe> ExtractFromPageAsync(
        string pageText,
        string? sourceUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tier 2. Written recipes only — a cookbook page, a screenshot, a handwritten card.
    /// </summary>
    Task<ExtractedRecipe> ExtractFromImagesAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default);
}

/// <summary>One extraction, already normalised into domain shapes, plus what the call cost.</summary>
public record ExtractedRecipe(ImportedRecipeDraft Draft, ChatTokenUsage? Usage);

/// <summary>
/// One image handed to a vision model. Bytes rather than a URL: the provider must not be asked
/// to fetch a user-supplied address on our behalf, which would move the SSRF problem to
/// somebody else's network and out of reach of the guard that solves it here.
/// </summary>
public sealed record RecipeImageContent(byte[] Content, string ContentType);
