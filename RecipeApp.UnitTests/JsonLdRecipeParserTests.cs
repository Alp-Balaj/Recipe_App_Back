using RecipeApp.Application.Recipes.Import;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Stream L, Tier 1's primary path. Almost every case here is a SHAPE a real publisher emits
// rather than a rule: schema.org lets a string field be a string, a number, an array or an
// object, and recipe sites use all of it. A shape this parser cannot read is a page that falls
// through to the paid extraction lane, so the tests are really a list of the ways import stays
// free.
public class JsonLdRecipeParserTests
{
    private static string Page(string jsonLd) =>
        $$"""
        <!doctype html><html><head>
        <script type="application/ld+json">{{jsonLd}}</script>
        </head><body><p>Some blog preamble.</p></body></html>
        """;

    private const string MinimalRecipe = """
        {
          "@context": "https://schema.org",
          "@type": "Recipe",
          "name": "Butter Beans on Toast",
          "description": "A fast supper.",
          "prepTime": "PT10M",
          "cookTime": "PT15M",
          "recipeYield": "2 servings",
          "recipeCuisine": "British",
          "image": "https://example.test/beans.jpg",
          "nutrition": { "@type": "NutritionInformation", "calories": "420 kcal" },
          "recipeIngredient": ["400 g butter beans", "2 slices sourdough", "1 tbsp olive oil", "Salt"],
          "recipeInstructions": [
            { "@type": "HowToStep", "text": "Warm the butter beans in a pan for 5 minutes." },
            { "@type": "HowToStep", "text": "Toast the sourdough." },
            { "@type": "HowToStep", "text": "Drizzle with the olive oil and season." }
          ]
        }
        """;

    [Fact]
    public void Reads_a_conventional_recipe()
    {
        var draft = JsonLdRecipeParser.TryParse(Page(MinimalRecipe));

        Assert.NotNull(draft);
        Assert.Equal("Butter Beans on Toast", draft.Title);
        Assert.Equal("A fast supper.", draft.Description);
        Assert.Equal(10, draft.PrepTimeMinutes);
        Assert.Equal(15, draft.CookTimeMinutes);
        Assert.Equal(2, draft.Servings);
        Assert.Equal(Cuisine.British, draft.CuisineType);
        Assert.Equal(420, draft.CaloriesPerServing);
        Assert.Equal("https://example.test/beans.jpg", draft.ImageUrl);
        Assert.Equal(4, draft.Ingredients.Count);
        Assert.Equal(3, draft.Steps.Count);
    }

    // Steps arrive typed (stream J) rather than as bare prose, which is the requirement the
    // brief singles out. The duration comes out of the sentence and the sentence keeps it.
    [Fact]
    public void Steps_come_back_typed_and_numbered()
    {
        var draft = JsonLdRecipeParser.TryParse(Page(MinimalRecipe));

        Assert.NotNull(draft);
        Assert.Equal([1, 2, 3], draft.Steps.Select(s => s.StepNumber));
        Assert.Equal(300, draft.Steps[0].DurationSeconds);
        // D15: stored as parsed. The duration was READ from the prose, never removed from it.
        Assert.Contains("5 minutes", draft.Steps[0].Description);
    }

    // D16, end to end through the parser: step 1 names the butter beans, step 3 names the oil.
    [Fact]
    public void Steps_reference_ingredients_by_index()
    {
        var draft = JsonLdRecipeParser.TryParse(Page(MinimalRecipe));

        Assert.NotNull(draft);
        Assert.Equal([0], draft.Steps[0].IngredientIndexes);
        Assert.Equal([1], draft.Steps[1].IngredientIndexes);
        Assert.Equal([2], draft.Steps[2].IngredientIndexes);
    }

    // The @graph shape, which is what most WordPress recipe plugins emit: the Recipe sits
    // among the site's Organization, WebPage and BreadcrumbList nodes.
    [Fact]
    public void Finds_a_recipe_inside_an_at_graph()
    {
        var page = Page($$"""
            {
              "@context": "https://schema.org",
              "@graph": [
                { "@type": "Organization", "name": "A Food Blog" },
                { "@type": "BreadcrumbList", "itemListElement": [] },
                {{MinimalRecipe}}
              ]
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal("Butter Beans on Toast", draft.Title);
    }

    [Fact]
    public void Finds_a_recipe_in_a_root_array()
    {
        var draft = JsonLdRecipeParser.TryParse(Page($"[{{\"@type\":\"WebSite\",\"name\":\"x\"}},{MinimalRecipe}]"));

        Assert.NotNull(draft);
        Assert.Equal("Butter Beans on Toast", draft.Title);
    }

    // "@type": ["Recipe", "NewsArticle"] is legal and not rare.
    [Fact]
    public void Accepts_an_array_valued_type()
    {
        var page = Page("""
            {
              "@type": ["NewsArticle", "Recipe"],
              "name": "Dual typed",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry the egg."
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal("Dual typed", draft.Title);
    }

    // A page may carry several blocks; only one of them is the recipe, and an earlier
    // malformed block must not stop the search.
    [Fact]
    public void Skips_malformed_blocks_and_keeps_looking()
    {
        var html = $$"""
            <html><head>
            <script type="application/ld+json">{ this is not json ,, }</script>
            <script type="application/ld+json">{"@type":"Organization","name":"Blog"}</script>
            <script type="application/ld+json">{{MinimalRecipe}}</script>
            </head><body></body></html>
            """;

        var draft = JsonLdRecipeParser.TryParse(html);

        Assert.NotNull(draft);
        Assert.Equal("Butter Beans on Toast", draft.Title);
    }

    // The oldest instructions shape: one prose blob with markup inside it.
    [Fact]
    public void Splits_a_single_instructions_blob_on_markup()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Blob",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "<ol><li>Crack the egg.</li><li>Fry it.</li><li>Eat it.</li></ol>"
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal(3, draft.Steps.Count);
        Assert.Equal("Crack the egg.", draft.Steps[0].Description);
    }

    // HowToSection wraps its own steps under a heading. The heading is not an instruction —
    // nobody "does" the words "For the sauce".
    [Fact]
    public void Descends_into_HowToSection_without_taking_its_heading_as_a_step()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Sectioned",
              "recipeIngredient": ["1 egg", "100 ml cream"],
              "recipeInstructions": [
                {
                  "@type": "HowToSection",
                  "name": "For the sauce",
                  "itemListElement": [
                    { "@type": "HowToStep", "text": "Warm the cream." },
                    { "@type": "HowToStep", "text": "Whisk in the egg." }
                  ]
                }
              ]
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal(2, draft.Steps.Count);
        Assert.DoesNotContain(draft.Steps, s => s.Description.Contains("For the sauce"));
    }

    [Fact]
    public void Reads_instructions_given_as_plain_strings()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Strings",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": ["Crack the egg.", "Fry it."]
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal(2, draft.Steps.Count);
    }

    [Theory]
    [InlineData("\"4\"", 4)]
    [InlineData("4", 4)]
    [InlineData("\"4 servings\"", 4)]
    [InlineData("\"Serves 6\"", 6)]
    [InlineData("\"4-6\"", 4)]
    [InlineData("[\"4\", \"4 servings\"]", 4)]
    public void Reads_every_recipeYield_shape(string yieldJson, int expected)
    {
        var page = Page($$"""
            {
              "@type": "Recipe",
              "name": "Yield",
              "recipeYield": {{yieldJson}},
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        Assert.Equal(expected, JsonLdRecipeParser.TryParse(page)?.Servings);
    }

    // An ImageObject rather than a bare URL, and an array of them.
    [Theory]
    [InlineData("\"https://example.test/a.jpg\"")]
    [InlineData("{\"@type\":\"ImageObject\",\"url\":\"https://example.test/a.jpg\"}")]
    [InlineData("[\"https://example.test/a.jpg\", \"https://example.test/b.jpg\"]")]
    [InlineData("[{\"@type\":\"ImageObject\",\"contentUrl\":\"https://example.test/a.jpg\"}]")]
    public void Reads_every_image_shape(string imageJson)
    {
        var page = Page($$"""
            {
              "@type": "Recipe",
              "name": "Image",
              "image": {{imageJson}},
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        Assert.Equal("https://example.test/a.jpg", JsonLdRecipeParser.TryParse(page)?.ImageUrl);
    }

    // Non-ISO durations are common enough to be worth reading.
    [Theory]
    [InlineData("\"PT1H30M\"", 90)]
    [InlineData("\"PT45M\"", 45)]
    [InlineData("\"30 minutes\"", 30)]
    [InlineData("\"1 hr 15 min\"", 75)]
    public void Reads_durations(string prepJson, int expected)
    {
        var page = Page($$"""
            {
              "@type": "Recipe",
              "name": "Times",
              "prepTime": {{prepJson}},
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        Assert.Equal(expected, JsonLdRecipeParser.TryParse(page)?.PrepTimeMinutes);
    }

    // A source stating only a total keeps that total correct — TotalTimeMinutes is computed as
    // prep + cook, so the published figure survives rather than being split into two invented
    // ones.
    [Fact]
    public void A_total_time_alone_lands_whole()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Total only",
              "totalTime": "PT40M",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal(40, (draft.PrepTimeMinutes ?? 0) + (draft.CookTimeMinutes ?? 0));
    }

    // Half plus total gives the other half from the source's own arithmetic.
    [Fact]
    public void Derives_the_missing_half_from_the_total()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Derived",
              "prepTime": "PT10M",
              "totalTime": "PT40M",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal(10, draft.PrepTimeMinutes);
        Assert.Equal(30, draft.CookTimeMinutes);
    }

    // Membership, not approximation — stream G's rule for the generator, applied here.
    [Fact]
    public void An_unknown_cuisine_becomes_null_rather_than_the_nearest_member()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Cuisine",
              "recipeCuisine": "Sicilian",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        Assert.Null(JsonLdRecipeParser.TryParse(page)?.CuisineType);
    }

    // keywords is very often one comma-separated string.
    [Fact]
    public void Reads_tags_from_a_comma_separated_keywords_string()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Tags",
              "keywords": "Vegan, Quick, NotATagWeKnow",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Contains(RecipeTag.Vegan, draft.Tags);
        Assert.Contains(RecipeTag.Quick, draft.Tags);
        Assert.Equal(2, draft.Tags.Count);
    }

    // ── The null cases, which are the signal to fall back to the model ───────────────────

    [Fact]
    public void Returns_null_for_a_page_with_no_json_ld()
    {
        Assert.Null(JsonLdRecipeParser.TryParse("<html><body><h1>A recipe, in prose only.</h1></body></html>"));
    }

    [Fact]
    public void Returns_null_when_no_block_contains_a_recipe()
    {
        Assert.Null(JsonLdRecipeParser.TryParse(Page("{\"@type\":\"Organization\",\"name\":\"Blog\"}")));
    }

    // A Recipe node carrying neither ingredients nor steps is a listing-page card, not a
    // recipe. Falling through to the model is right: it reads the page text and often does
    // better with exactly these.
    [Fact]
    public void Returns_null_for_a_stub_recipe_node()
    {
        Assert.Null(JsonLdRecipeParser.TryParse(Page("{\"@type\":\"Recipe\",\"name\":\"Just a card\"}")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_null_for_empty_input(string? html)
    {
        Assert.Null(JsonLdRecipeParser.TryParse(html));
    }

    // HTML entities and inline markup inside string fields are endemic.
    [Fact]
    public void Decodes_entities_and_strips_inline_markup()
    {
        var page = Page("""
            {
              "@type": "Recipe",
              "name": "Fish &amp; Chips",
              "description": "<p>Proper <b>chips</b>.</p>",
              "recipeIngredient": ["1 egg"],
              "recipeInstructions": "Fry it."
            }
            """);

        var draft = JsonLdRecipeParser.TryParse(page);

        Assert.NotNull(draft);
        Assert.Equal("Fish & Chips", draft.Title);
        Assert.Equal("Proper chips.", draft.Description);
    }
}
