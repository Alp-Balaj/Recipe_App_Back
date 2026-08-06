using RecipeApp.Application.Chat.Dtos;

namespace RecipeApp.Application.Recipes.Dtos;

// Wire contract for the two import routes (stream L).

/// <summary>
/// POST /recipes/import/url. One field, and no Visibility field beside it — unlike
/// <c>GenerateRecipeRequest</c>, which lets the caller choose.
///
/// That asymmetry is decision D15 and not an oversight. A generated recipe is the user's own
/// composition and defaults to their <c>DefaultRecipeVisibility</c>; an imported one is
/// SOMEBODY ELSE'S WRITING sitting in the user's account, and publishing it to a feed by
/// default — which is what deferring to that preference would do, since it itself defaults to
/// Public — republishes a stranger's work on their behalf without asking. So import lands
/// Private, always, and the owner promotes it deliberately through the ordinary edit path
/// once they have looked at it.
/// </summary>
public record ImportRecipeFromUrlRequest(string Url);

/// <summary>
/// Where an imported recipe's content actually came from. Returned to the client because the
/// difference is worth surfacing — a structured-data import is exact, an extracted one is a
/// model's reading of prose or handwriting and deserves a closer look before the owner makes
/// it public.
///
/// It also makes stream L's central claim OBSERVABLE rather than merely asserted: "a page with
/// JSON-LD costs no model call" is verifiable from the response body, by a test and by a human,
/// without instrumenting the provider.
/// </summary>
public enum RecipeImportSource
{
    /// <summary>schema.org/Recipe JSON-LD, parsed deterministically. No model was called.</summary>
    StructuredData,

    /// <summary>The page carried no usable JSON-LD, so its text went to the model.</summary>
    PageExtraction,

    /// <summary>Tier 2 — a photograph of a written recipe, read by the vision model.</summary>
    PhotoExtraction,
}

/// <summary>
/// 201 body for both import routes. The recipe is an ordinary persisted row in the same shape
/// every other recipe endpoint returns, so the SPA routes straight to /recipes/{id}.
/// </summary>
/// <param name="Budget">
/// NULL when no model was called — which is the common Tier 1 outcome, not an edge case.
/// A zeroed budget object would have been the tidier-looking choice and would have said
/// something false: "here is what this cost you" reads as a claim that it cost something.
/// Absent means absent.
/// </param>
public record ImportRecipeResponse(
    RecipeResponse Recipe,
    RecipeImportSource Source,
    AiBudgetResponse? Budget);
