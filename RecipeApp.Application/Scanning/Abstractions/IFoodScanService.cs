using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Scanning.Dtos;

namespace RecipeApp.Application.Scanning.Abstractions;

/// <summary>
/// The food scanner's orchestrator (stream N, D13): budget gate, one vision call, then
/// deterministic work — resolution against the catalogue and coverage over the caller's
/// visible recipes for a pantry, a reviewable draft for a receipt.
///
/// D19, settled: the photo is NEVER persisted. Bytes go to the vision caller and are
/// discarded. The evidence is in IImageStorage's D21 comment — exactly two columns
/// reference a stored object and a scan photo has neither, so a persisted scan would be
/// unreachable the moment it was written: garbage by construction. It is also a photograph
/// of somebody's HOME, and stored objects are served by unguessable URL with no
/// authorization check; not storing it is the only version of "private" this app can
/// currently promise. The only row either scan writes is its AiUsageRecord.
/// </summary>
public interface IFoodScanService
{
    Task<FoodScanResult<PantryScanResponse>> ScanPantryAsync(
        RecipeImageContent image, Guid userId, CancellationToken cancellationToken = default);

    Task<FoodScanResult<ReceiptScanResponse>> ScanReceiptAsync(
        RecipeImageContent image, Guid userId, CancellationToken cancellationToken = default);
}
