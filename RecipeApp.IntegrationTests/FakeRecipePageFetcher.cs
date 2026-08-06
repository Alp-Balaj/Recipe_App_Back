using RecipeApp.Application.Recipes.Abstractions;

namespace RecipeApp.IntegrationTests;

// Stream L. Stands in for the one seam in the import path that talks to the internet, so the
// suite never makes an outbound request — and, just as importantly, so the tests can pin
// exactly which page shape produced which outcome.
//
// A PURE FUNCTION OF THE URL, like every other fake here: the path selects a canned page, and
// there is no mutable state, so it is safe under the shared TestServer. That constraint is why
// there is no call counter on this class even though "was the model called?" is a thing the
// suite must prove — see FakeRecipeExtractionAssistant, which proves it through the response
// body instead.
//
// NOTE WHAT THIS FAKE DOES NOT COVER: the SSRF guard. Every rejection in SafeRecipePageFetcher
// — the scheme check, the credentials check, the address policy, the per-hop redirect
// re-validation — lives in the real implementation this class replaces, so no integration test
// here can exercise it. That is deliberate rather than a gap: those rules are pure and are
// tested directly in PrivateAddressPolicyTests, and an integration test that tried to reach a
// private address would either be testing this fake or making a real connection to 169.254.
public sealed class FakeRecipePageFetcher : IRecipePageFetcher
{
    /// <summary>Path of a page carrying well-formed schema.org JSON-LD. No model call should follow.</summary>
    public const string StructuredPath = "/structured";

    /// <summary>Path of a page with prose only — the fallback lane's trigger.</summary>
    public const string UnstructuredPath = "/unstructured";

    /// <summary>Path of a page whose JSON-LD Recipe node is a stub, so the parser declines it.</summary>
    public const string StubPath = "/stub";

    /// <summary>Path that fails the fetch outright, as an unreachable host would.</summary>
    public const string UnreachablePath = "/unreachable";

    /// <summary>Path this server refuses to fetch at all, standing in for a policy rejection.</summary>
    public const string RejectedPath = "/rejected";

    /// <summary>Title carried by the structured page's JSON-LD. Its presence in a response PROVES the deterministic path ran.</summary>
    public const string StructuredTitle = "Structured Data Stew";

    /// <summary>The image the structured page advertises. Re-hosting turns this into a local URL.</summary>
    public const string StructuredImageUrl = "https://images.example.test/stew.png";

    // A one-pixel PNG, so the re-host path gets bytes that survive ImageUploadRules' magic-byte
    // sniff. Same fixture ImageUploadEndpointsTests uses.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public Task<FetchedPage> FetchPageAsync(Uri url, CancellationToken cancellationToken = default) =>
        url.AbsolutePath switch
        {
            RejectedPath => throw RecipeFetchException.Rejected("That address is not publicly reachable."),
            UnreachablePath => throw RecipeFetchException.Unreachable("That address returned HTTP 503."),
            StructuredPath => Task.FromResult(new FetchedPage(StructuredPage, url)),
            StubPath => Task.FromResult(new FetchedPage(StubPage, url)),
            _ => Task.FromResult(new FetchedPage(UnstructuredPage + QueryEcho(url), url)),
        };

    /// <summary>
    /// Echoes the query string into the page body, so a test can steer the EXTRACTOR through
    /// the URL it imports — FakeRecipeExtractionAssistant's sentinels are matched against page
    /// text, and without this they would never reach it.
    ///
    /// Still a pure function of the URL, which is the constraint every fake here works under.
    /// The alternative (a settable property on the fake) would be shared mutable state under
    /// the shared TestServer, and two tests running concurrently would steer each other.
    /// </summary>
    /// Echoed as visible PROSE rather than an HTML comment: HtmlText.VisibleText strips
    /// comments along with every other tag, so a sentinel hidden in one would never survive
    /// into the text the extractor sees.
    private static string QueryEcho(Uri url) =>
        string.IsNullOrEmpty(url.Query) ? string.Empty : $"<p>{Uri.UnescapeDataString(url.Query)}</p>";

    public Task<FetchedImage?> FetchImageAsync(Uri url, CancellationToken cancellationToken = default) =>
        Task.FromResult<FetchedImage?>(
            url.ToString() == StructuredImageUrl ? new FetchedImage(OnePixelPng, "image/png") : null);

    // A realistic page: the recipe node sits in an @graph beside the site's own nodes, the
    // instructions are HowToSteps, and one ingredient line has no measurement. Between them
    // these exercise the parser, the ingredient line parser, the step linker and the
    // duration/temperature extraction in one fixture.
    private static readonly string StructuredPage = $$"""
        <!doctype html>
        <html><head>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org",
          "@graph": [
            { "@type": "Organization", "name": "A Test Blog" },
            {
              "@type": "Recipe",
              "name": "{{StructuredTitle}}",
              "description": "A stew that exists to be parsed.",
              "prepTime": "PT10M",
              "cookTime": "PT35M",
              "recipeYield": "4 servings",
              "recipeCuisine": "British",
              "keywords": "Comfort, Hearty",
              "image": "{{StructuredImageUrl}}",
              "nutrition": { "@type": "NutritionInformation", "calories": "510 kcal" },
              "recipeIngredient": [
                "400 g butter beans",
                "2 tbsp olive oil",
                "1 large onion, finely chopped",
                "Salt and black pepper"
              ],
              "recipeInstructions": [
                { "@type": "HowToStep", "text": "Heat the olive oil in a heavy pan." },
                { "@type": "HowToStep", "text": "Soften the onion for 8 minutes." },
                { "@type": "HowToStep", "text": "Add the butter beans and bake at 180 C for 25 minutes." },
                { "@type": "HowToStep", "text": "Season to taste and serve." }
              ]
            }
          ]
        }
        </script>
        </head><body><p>Blog preamble nobody needs.</p></body></html>
        """;

    // No JSON-LD anywhere — the fallback lane's trigger.
    private const string UnstructuredPage = """
        <!doctype html>
        <html><head><title>Grandma's notes</title></head>
        <body>
          <h1>Prose Only Pie</h1>
          <p>Take some flour and some butter. Rub them together. Bake it.</p>
        </body></html>
        """;

    // A Recipe node with a name and nothing else — a listing-page card. The parser declines it
    // and the import falls through to the model, which is the behaviour under test.
    private const string StubPage = """
        <!doctype html>
        <html><head>
        <script type="application/ld+json">
        { "@context": "https://schema.org", "@type": "Recipe", "name": "Just a card" }
        </script>
        </head><body><p>Prose Only Pie: take some flour and some butter.</p></body></html>
        """;
}
