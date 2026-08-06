using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Import;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// Stream L. Deterministic stand-in for the Gemini-backed extraction assistant, so CI never
// makes a paid call on either model-backed path (the page fallback or the photo tier).
//
// ── HOW THE SUITE PROVES "NO MODEL CALL" WITHOUT A COUNTER ──────────────────────────────
// The stream's central claim is that a page carrying JSON-LD costs nothing, and a test has to
// be able to check it. A call counter would be the obvious instrument and is the wrong one
// here: every fake in this project is a pure function of its inputs precisely so it stays safe
// under the SHARED TestServer, and a mutable counter would make two tests running against the
// same host able to read each other's numbers.
//
// So the proof runs through the response body instead, twice over:
//
//   1. ImportRecipeResponse.Source distinguishes StructuredData from PageExtraction, and the
//      orchestrator sets it on the branch it actually took.
//   2. This fake stamps a SENTINEL TITLE. A recipe that came back titled "Structured Data
//      Stew" cannot have been through the model, because the model only ever returns
//      "Extracted: ...". The two assertions are independent — one would have to lie about the
//      branch and the other about the content — which is what makes the claim solid rather
//      than merely asserted.
//
// That also makes the check meaningful in the live pass, where there is no fake at all.
public sealed class FakeRecipeExtractionAssistant : IRecipeExtractionAssistant
{
    /// <summary>Prefix on every title this fake produces. Its presence means a model path ran.</summary>
    public const string ExtractedPrefix = "Extracted: ";

    /// <summary>Title of a recipe read out of a photograph.</summary>
    public const string PhotoTitle = ExtractedPrefix + "Photographed Recipe";

    /// <summary>Page text containing this makes the extractor throw, exercising the 502 path.</summary>
    public const string FailSentinel = "__FAIL__";

    /// <summary>
    /// Page text containing this makes the extractor return an EMPTY draft — a model that read
    /// the page and found no recipe in it. Distinct from a failure: nothing went wrong, there
    /// was simply nothing there, and the orchestrator must answer 422 rather than 502.
    /// </summary>
    public const string EmptySentinel = "__EMPTY__";

    // An image this small cannot contain a recipe, which is how the photo path signals failure
    // without needing mutable state: the test posts a truncated PNG.
    private const int FailingImageByteCount = 8;

    public Task<ExtractedRecipe> ExtractFromPageAsync(
        string pageText,
        string? sourceUrl,
        CancellationToken cancellationToken = default)
    {
        if (pageText.Contains(FailSentinel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated recipe extractor failure.");
        }

        if (pageText.Contains(EmptySentinel, StringComparison.Ordinal))
        {
            return Task.FromResult(new ExtractedRecipe(new ImportedRecipeDraft(), Usage));
        }

        // The title echoes nothing from the page on purpose: a test asserting on this string is
        // asserting that the MODEL path ran, and echoing page content would let a structured
        // import accidentally produce a matching title.
        return Task.FromResult(new ExtractedRecipe(
            Draft(ExtractedPrefix + "Prose Only Pie"),
            Usage));
    }

    public Task<ExtractedRecipe> ExtractFromImagesAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0 || images[0].Content.Length <= FailingImageByteCount)
        {
            throw new InvalidOperationException("Simulated vision extractor failure.");
        }

        return Task.FromResult(new ExtractedRecipe(Draft(PhotoTitle), Usage));
    }

    // Non-zero, so the usage row the orchestrator stages is visibly a real one and the budget
    // actually moves — which is what lets a test drive the quota gate to exhaustion.
    private static ChatTokenUsage Usage => new(120, 340, 460);

    // An already-clean draft. Salvaging a messy model response is RecipeExtractionAssistant's
    // job and belongs in its own unit tests; faking it here would only re-test the fake.
    // Servings and description are deliberately ABSENT so the normaliser's defaults are on the
    // path every model-backed integration test exercises.
    private static ImportedRecipeDraft Draft(string title) => new()
    {
        Title = title,
        PrepTimeMinutes = 5,
        CookTimeMinutes = 20,
        Ingredients =
        [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "butter", Quantity = 100m, Unit = UnitOfMeasure.Gram },
        ],
        Steps =
        [
            new RecipeStep
            {
                StepNumber = 1,
                Description = "Rub the butter into the flour.",
                IngredientIndexes = [0, 1],
            },
            new RecipeStep
            {
                StepNumber = 2,
                Description = "Bake for 20 minutes.",
                DurationSeconds = 1200,
                Temperature = new StepTemperature { Value = 180, Unit = TemperatureUnit.Celsius },
            },
        ],
    };
}
