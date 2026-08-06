using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// The endpoint-facing orchestrator for stream L's two import tiers.
///
/// SYNCHRONOUS, DELIBERATELY, and this is the stream's one architectural refusal worth
/// recording at the seam rather than in a commit message. Stream X built this backend's first
/// background execution seam and its own comment invites L to reuse it. L declines.
///
/// The reason is not that the work is fast — a fetch plus a vision call is the slowest thing
/// this API does. It is that moderation and import are opposite shapes. NOBODY WAITS ON
/// MODERATION: it produces no response, and dropping an item costs a missed catch. EVERYBODY
/// WAITS ON THEIR OWN IMPORT: the created recipe IS the response, so a queued import would
/// have to invent a job entity, a status row, a polling route and a second migration — to
/// deliver, eventually, the thing an awaited call already returns. POST /recipes/generate
/// already awaits a provider round-trip and returns a recipe; import is that shape exactly.
///
/// What the queue actually buys — not blocking a create path that has already committed — is
/// a property import does not need, because import has nothing committed to protect.
/// </summary>
public interface IRecipeImportService
{
    /// <summary>
    /// Tier 1. Fetches the page, prefers schema.org JSON-LD, and only falls back to the model
    /// when there is none. A page with structured data never consults the caller's AI budget,
    /// so an exhausted quota cannot block an import that needs no model.
    /// </summary>
    Task<RecipeImportResult<ImportRecipeResponse>> ImportFromUrlAsync(
        ImportRecipeFromUrlRequest request,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tier 2. Reads a photograph of a WRITTEN recipe — a cookbook page, a screenshot, a
    /// handwritten card. Not a photograph of food: recognising a cooked dish and inventing a
    /// recipe for it is a different feature that was cut, not deferred.
    /// </summary>
    Task<RecipeImportResult<ImportRecipeResponse>> ImportFromPhotoAsync(
        RecipeImageContent image,
        Guid userId,
        CancellationToken cancellationToken = default);
}
