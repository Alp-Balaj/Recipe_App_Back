using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.UnitTests;

// Unit tests for the recipe-generation seam (stream E) through a faked IChatMessageCaller —
// the same mould as ChatAssistantServiceTests / MealPlanAssistantServiceTests.
//
// These carry more weight than their two siblings do. The other AI lanes are grounded: an
// id either is in the candidate set or it is dropped, and that is the whole safety story.
// This lane is free — the model invents every value — so the ONLY thing standing between a
// hallucinated number and a row in the recipes table is the normalisation asserted below.
// Each test names the corruption it prevents.
public class RecipeGenerationAssistantTests
{
    private sealed class FakeCaller : IChatMessageCaller
    {
        private readonly string _json;

        public FakeCaller(string json) => _json = json;

        public string? CapturedSystemPrompt { get; private set; }
        public object? CapturedSchema { get; private set; }
        public IReadOnlyList<ChatHistoryItem>? CapturedHistory { get; private set; }
        public string? CapturedUserMessage { get; private set; }

        public Task<ChatMessageCall> CreateJsonMessageAsync(
            string systemPrompt,
            IReadOnlyList<ChatHistoryItem> history,
            string userMessage,
            object? responseSchema = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSystemPrompt = systemPrompt;
            CapturedSchema = responseSchema;
            CapturedHistory = history;
            CapturedUserMessage = userMessage;
            return Task.FromResult(new ChatMessageCall(_json, new ChatTokenUsage(100, 200, 300)));
        }
    }

    private const string ValidJson = """
        {
          "title": "Lemon Butter Cod",
          "description": "A fast weeknight fish supper.",
          "prepTimeMinutes": 10,
          "cookTimeMinutes": 15,
          "servings": 2,
          "difficulty": "Easy",
          "cuisineType": "British",
          "caloriesPerServing": 420,
          "ingredients": [
            { "name": "cod fillet", "quantity": 2, "unit": "pcs" },
            { "name": "butter", "quantity": 30, "unit": "g" }
          ],
          "steps": [
            { "description": "Season the cod." },
            { "description": "Fry in butter.", "durationSeconds": 300 }
          ],
          "tags": ["fish", "quick"]
        }
        """;

    private static Task<GeneratedRecipe> InvokeAsync(string json, IReadOnlyList<string>? dietary = null) =>
        new RecipeGenerationAssistant(new FakeCaller(json))
            .GenerateAsync("something with cod", [], new AiPreferenceContext(dietary ?? [], []));

    private static async Task<GeneratedRecipeDraft> DraftAsync(string json) => (await InvokeAsync(json)).Draft;

    [Fact]
    public async Task ValidResponse_MapsEveryField()
    {
        var draft = await DraftAsync(ValidJson);

        Assert.Equal("Lemon Butter Cod", draft.Title);
        Assert.Equal("A fast weeknight fish supper.", draft.Description);
        Assert.Equal(10, draft.PrepTimeMinutes);
        Assert.Equal(15, draft.CookTimeMinutes);
        Assert.Equal(2, draft.Servings);
        Assert.Equal(DifficultyLevel.Easy, draft.Difficulty);
        Assert.Equal(Cuisine.British, draft.CuisineType);
        Assert.Equal(420, draft.CaloriesPerServing);
        Assert.Equal(2, draft.Ingredients.Count);
        Assert.Equal("cod fillet", draft.Ingredients[0].Name);
        Assert.Equal(2m, draft.Ingredients[0].Quantity);
        Assert.Equal(UnitOfMeasure.Piece, draft.Ingredients[0].Unit);
        Assert.Equal([RecipeTag.Quick], draft.Tags);
        Assert.Equal(300, draft.Steps[1].DurationSeconds);
    }

    [Fact]
    public async Task TokenUsage_RidesAlongUntouched()
    {
        // The class validates CONTENT; what the call cost is the provider's report and is
        // accounted upstream (ai-quotas). Losing it here would mean an unbilled generation.
        var generated = await InvokeAsync(ValidJson);

        Assert.Equal(300, generated.Usage!.TotalTokens);
    }

    // ── The three unsalvageable failures. Each becomes a 502 with nothing persisted. ──

    [Fact]
    public async Task MalformedJson_ThrowsClearFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync("not json at all"));
    }

    [Fact]
    public async Task MissingTitle_ThrowsClearFailure()
    {
        var json = """
            { "title": "   ", "description": "d", "ingredients": [{"name":"x","quantity":1,"unit":"g"}],
              "steps": [{"description":"do it"}] }
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(json));
    }

    [Fact]
    public async Task NoUsableIngredients_ThrowsClearFailure()
    {
        // A recipe whose only ingredients are nameless is not a recipe. The human write
        // path's validator rejects an empty Ingredients list with a 400; there is nobody
        // to hand a 400 to here, so this is where it stops.
        var json = """
            { "title": "Soup", "description": "d", "ingredients": [{"name":"","quantity":1,"unit":"g"}],
              "steps": [{"description":"do it"}] }
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(json));
    }

    [Fact]
    public async Task NoUsableSteps_ThrowsClearFailure()
    {
        var json = """
            { "title": "Soup", "description": "d", "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"  "}] }
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(json));
    }

    // ── Everything else is salvaged rather than rejected. ─────────────────────────────

    [Fact]
    public async Task MissingDescription_FallsBackToTitle()
    {
        // NotEmpty on the human path; an empty string would be a row the create form could
        // not have produced.
        var json = """
            { "title": "Soup", "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal("Soup", draft.Description);
    }

    [Theory]
    [InlineData("\"Hard\"", DifficultyLevel.Hard)]
    [InlineData("\"hard\"", DifficultyLevel.Hard)]      // case-insensitive
    [InlineData("\"Impossible\"", DifficultyLevel.Medium)] // unknown -> Medium
    [InlineData("\"17\"", DifficultyLevel.Medium)]      // Enum.TryParse accepts bare ints; IsDefined catches it
    [InlineData("null", DifficultyLevel.Medium)]
    public async Task Difficulty_ParsesOrFallsBackToMedium(string raw, DifficultyLevel expected)
    {
        var json = $$"""
            { "title": "Soup", "description": "d", "difficulty": {{raw}},
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(expected, draft.Difficulty);
    }

    [Fact]
    public async Task AbsurdNumbers_AreClampedIntoRange()
    {
        // 900 servings and a four-day prep are not a 400 to anyone — they are a row that
        // breaks the day page's totals and the week's cook-load bar.
        var json = """
            { "title": "Soup", "description": "d",
              "prepTimeMinutes": -5, "cookTimeMinutes": 99999, "servings": 900,
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(0, draft.PrepTimeMinutes);
        Assert.Equal(24 * 60, draft.CookTimeMinutes);
        Assert.Equal(100, draft.Servings);
    }

    [Fact]
    public async Task FractionalNumbers_AreRounded()
    {
        var json = """
            { "title": "Soup", "description": "d", "prepTimeMinutes": 10.4, "servings": 2.6,
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(10, draft.PrepTimeMinutes);
        Assert.Equal(3, draft.Servings);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("-40", null)]
    [InlineData("null", null)]
    [InlineData("420", 420)]
    public async Task Calories_DropRatherThanClampWhenNotAnEstimate(string raw, int? expected)
    {
        // Clamping 0 up to 1 would manufacture a "1 kcal" serving the model never claimed,
        // and calories feed the day page's totals. Absent is an honest answer; 1 is not.
        var json = $$"""
            { "title": "Soup", "description": "d", "caloriesPerServing": {{raw}},
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(expected, draft.CaloriesPerServing);
    }

    [Fact]
    public async Task ZeroDurationSeconds_BecomesNull()
    {
        var json = """
            { "title": "Soup", "description": "d",
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it","durationSeconds":0}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Null(draft.Steps[0].DurationSeconds);
    }

    [Fact]
    public async Task IngredientWithoutQuantityOrUnit_GetsNeutralDefaults()
    {
        // The human path requires Quantity > 0 and a non-empty Unit. Dropping the row would
        // silently delete an ingredient from the recipe, which is worse than approximating
        // it — so "salt, to taste" becomes 1 pcs rather than disappearing.
        var json = """
            { "title": "Soup", "description": "d",
              "ingredients": [{"name":"salt","quantity":0,"unit":"  "}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        var ingredient = Assert.Single(draft.Ingredients);
        Assert.Equal("salt", ingredient.Name);
        Assert.Equal(1m, ingredient.Quantity);
        Assert.Equal(UnitOfMeasure.Piece, ingredient.Unit);
    }

    [Fact]
    public async Task NamelessIngredient_IsDroppedWithoutKillingTheRecipe()
    {
        var json = """
            { "title": "Soup", "description": "d",
              "ingredients": [{"name":"","quantity":1,"unit":"g"},{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal("stock", Assert.Single(draft.Ingredients).Name);
    }

    [Fact]
    public async Task Steps_AreRenumberedFromOne_IgnoringWhatTheModelSent()
    {
        // StepNumber > 0 with no gaps or duplicates is what the validator and the detail
        // page assume. Renumbering from the SURVIVING steps also means a dropped blank in
        // the middle cannot leave a hole.
        var json = """
            { "title": "Soup", "description": "d",
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"first"},{"description":"   "},{"description":"third"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(2, draft.Steps.Count);
        Assert.Equal([1, 2], draft.Steps.Select(s => s.StepNumber));
        Assert.Equal(["first", "third"], draft.Steps.Select(s => s.Description));
    }

    [Fact]
    public async Task Tags_AreDedupedCaseInsensitivelyAndCapped()
    {
        // Tag FILTERING is case-sensitive on GET /recipes, so "Vegan" and "vegan" on one
        // recipe would split one idea across two facets.
        // 15 real members, so the cap (10) is what trims the list rather than the
        // vocabulary check. "vegan" repeats Vegan in another casing and "  " is blank —
        // neither may occupy a slot.
        var many = string.Join(",", Enum.GetNames<RecipeTag>().Skip(1).Take(15).Select(n => $"\"{n}\""));
        var json = $$"""
            { "title": "Soup", "description": "d",
              "tags": ["Vegan","vegan","  ", {{many}}],
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(10, draft.Tags.Count);
        Assert.Equal(RecipeTag.Vegan, draft.Tags[0]);
        // Parsed case-insensitively into ONE member, so the duplicate cost no slot — the
        // property the old string version had to spell out as a case-insensitive dedupe.
        Assert.Single(draft.Tags, t => t == RecipeTag.Vegan);
    }

    [Fact]
    public async Task OverlongTitle_IsClippedToTheHumanPathsLimit()
    {
        var json = $$"""
            { "title": "{{new string('a', 500)}}", "description": "d",
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(200, draft.Title.Length);
    }

    [Fact]
    public async Task BlankCuisine_BecomesNull()
    {
        var json = """
            { "title": "Soup", "description": "d", "cuisineType": "   ",
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"heat it"}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Null(draft.CuisineType);
    }

    // ── The call itself ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SystemPrompt_CarriesDietaryRestrictions_AndItsOwnSchema()
    {
        var caller = new FakeCaller(ValidJson);

        await new RecipeGenerationAssistant(caller)
            .GenerateAsync("something with cod", [], new AiPreferenceContext(["vegetarian", "no nuts"], []));

        Assert.Contains("vegetarian, no nuts", caller.CapturedSystemPrompt);
        Assert.Equal("something with cod", caller.CapturedUserMessage);
        // The generator must not ride the chat lane's default { reply, suggestedRecipeIds }
        // schema — its own recipe schema has to travel through the seam.
        Assert.NotNull(caller.CapturedSchema);
        Assert.IsNotType<CancellationToken>(caller.CapturedSchema);
    }

    // Onboarding (stream K). The generator's stakes are higher than chat's: a recommendation
    // bent toward a preferred cuisine is a bad suggestion the user scrolls past, but a
    // GENERATED recipe bent that way is a row saved to their account. Hence the conditional
    // wording, and hence this assertion on it.
    [Fact]
    public async Task SystemPrompt_CarriesCuisinePreferences_AsADefaultTheRequestOverrides()
    {
        var caller = new FakeCaller(ValidJson);

        await new RecipeGenerationAssistant(caller)
            .GenerateAsync("something with cod", [], new AiPreferenceContext(["vegan"], ["Korean", "Japanese"]));

        var prompt = caller.CapturedSystemPrompt!;
        Assert.Contains("Korean, Japanese", prompt);
        Assert.Contains("only when — the request does not imply a cuisine", prompt);
        Assert.Contains("overrides this completely", prompt);

        // The restriction line keeps "absolute"; the preference must never borrow that word.
        Assert.Contains("dietary restrictions are absolute", prompt);
    }

    [Fact]
    public async Task SystemPrompt_OmitsThePreferenceBlock_WhenNoCuisinesAreChosen()
    {
        var caller = new FakeCaller(ValidJson);

        await new RecipeGenerationAssistant(caller).GenerateAsync("something with cod", [], AiPreferenceContext.None);

        Assert.DoesNotContain("lean toward the cuisines", caller.CapturedSystemPrompt!);
    }

    [Fact]
    public async Task History_IsTrimmedToTheTrailingWindow()
    {
        var caller = new FakeCaller(ValidJson);
        var history = Enumerable.Range(1, 30)
            .Select(i => new ChatHistoryItem("user", $"message {i}"))
            .ToList();

        await new RecipeGenerationAssistant(caller).GenerateAsync("go", history, AiPreferenceContext.None);

        Assert.Equal(20, caller.CapturedHistory!.Count);
        Assert.Equal("message 11", caller.CapturedHistory[0].Content);
    }

    // ── The entity mapping (the other half of stream G's future edit) ─────────────────

    [Fact]
    public async Task ToRecipe_StampsProvenanceTheModelHasNoSayIn()
    {
        var owner = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var draft = await DraftAsync(ValidJson);

        var recipe = RecipeGenerationAssistant.ToRecipe(draft, owner, conversation, RecipeVisibility.Private);

        Assert.True(recipe.IsAiGenerated);
        Assert.Equal(conversation, recipe.SourceConversationId);
        Assert.Equal(owner, recipe.CreatedByUserId);
        Assert.Equal(RecipeVisibility.Private, recipe.Visibility);
        Assert.Null(recipe.ImageUrl);
        Assert.NotEqual(Guid.Empty, recipe.Id);
        Assert.Equal("Lemon Butter Cod", recipe.Title);
        Assert.Equal(2, recipe.Ingredients.Count);
    }

    [Fact]
    public async Task ToRecipe_WithoutAConversation_LeavesTheSourceNull()
    {
        var draft = await DraftAsync(ValidJson);

        var recipe = RecipeGenerationAssistant.ToRecipe(draft, Guid.NewGuid(), null, RecipeVisibility.Public);

        Assert.True(recipe.IsAiGenerated);
        Assert.Null(recipe.SourceConversationId);
    }

    // ── Stream J: the typed step ─────────────────────────────────────────────────────
    // The free lane's usual bargain applies — the model invents these too, so each test
    // below names the corruption normalisation prevents.

    [Fact]
    public async Task StepIngredientIndexes_AreRemappedAroundADroppedIngredient()
    {
        // THE decision-D16 hazard on this path, and the reason NormaliseIngredients returns
        // a map. The model wrote three ingredients and referenced positions 0 and 2; the
        // middle one has no name and is dropped, which slides "chilli" from 2 to 1. Applying
        // the raw index to the shortened list would attach the WRONG ingredient to the step —
        // silently, and in a way no round-trip test could see.
        var json = """
            { "title": "Noodles", "description": "d",
              "ingredients": [
                {"name":"noodles","quantity":200,"unit":"g"},
                {"name":"","quantity":1,"unit":"g"},
                {"name":"chilli","quantity":2,"unit":"pcs"}
              ],
              "steps": [{"description":"Toss together.","ingredientIndexes":[0,2]}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(["noodles", "chilli"], draft.Ingredients.Select(i => i.Name));
        Assert.Equal([0, 1], draft.Steps[0].IngredientIndexes);
    }

    [Fact]
    public async Task StepIngredientIndexes_DropReferencesToNothing()
    {
        // Out of range, negative, and a duplicate. None is clamped to a neighbour: attaching
        // an arbitrary nearby ingredient to a step reads as deliberate and is unfalsifiable.
        var json = """
            { "title": "Soup", "description": "d",
              "ingredients": [{"name":"stock","quantity":1,"unit":"l"}],
              "steps": [{"description":"Heat it.","ingredientIndexes":[0,0,7,-3]}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal([0], draft.Steps[0].IngredientIndexes);
    }

    [Fact]
    public async Task StepWithNoIngredientIndexes_GetsAnEmptyList()
    {
        // Never null — "preheat the oven" consumes nothing, and a null here would make every
        // reader on the frontend defend against it.
        var draft = await DraftAsync(ValidJson);

        Assert.Empty(draft.Steps[0].IngredientIndexes);
    }

    [Fact]
    public async Task StepTemperature_IsKeptInTheUnitTheModelWroteIt()
    {
        var json = """
            { "title": "Bread", "description": "d",
              "ingredients": [{"name":"flour","quantity":500,"unit":"g"}],
              "steps": [{"description":"Bake.","temperature":{"value":425,"unit":"Fahrenheit"}}] }
            """;

        var draft = await DraftAsync(json);

        Assert.Equal(425, draft.Steps[0].Temperature!.Value);
        Assert.Equal(TemperatureUnit.Fahrenheit, draft.Steps[0].Temperature!.Unit);
    }

    [Fact]
    public async Task ImplausibleOrUnscaledTemperature_BecomesNullRatherThanClamped()
    {
        // Same rule as calories: a wrong number is worse than no number. 900 °C is the model
        // being wrong, and 300 standing in for it is a claim nobody made; a temperature with
        // an unrecognised unit is not a temperature at all.
        var tooHot = """
            { "title": "Bread", "description": "d",
              "ingredients": [{"name":"flour","quantity":500,"unit":"g"}],
              "steps": [{"description":"Bake.","temperature":{"value":900,"unit":"Celsius"}}] }
            """;
        var noScale = """
            { "title": "Bread", "description": "d",
              "ingredients": [{"name":"flour","quantity":500,"unit":"g"}],
              "steps": [{"description":"Bake.","temperature":{"value":180,"unit":"Kelvin"}}] }
            """;

        Assert.Null((await DraftAsync(tooHot)).Steps[0].Temperature);
        Assert.Null((await DraftAsync(noScale)).Steps[0].Temperature);
    }

    [Fact]
    public async Task Prompt_TellsTheModelNotToRepeatQuantitiesInStepProse()
    {
        // The reference exists so the prose does not have to carry the amount (decision D17's
        // third answer). A model that writes "stir in 2 tbsp butter" anyway makes the recipe
        // go stale the moment servings change, which is the whole thing J is avoiding.
        var caller = new FakeCaller(ValidJson);

        await new RecipeGenerationAssistant(caller).GenerateAsync("go", [], []);

        Assert.Contains("ingredientIndexes", caller.CapturedSystemPrompt);
        Assert.Contains("WITHOUT repeating the quantity", caller.CapturedSystemPrompt);
    }
}
