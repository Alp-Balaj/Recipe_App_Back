using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// Deterministic stand-in for the Gemini-backed IRecipeGenerationAssistant (stream E) so CI
// never calls the real API. Same design rules as FakeChatAssistantService /
// FakeMealPlanAssistantService: behaviour is a PURE function of the inputs — no mutable
// state — so it is safe under the shared TestServer.
//
//   * The draft echoes the prompt into the title, so a test can assert its own generation
//     came back on a shared database.
//   * The dietary restrictions and the conversation history length are echoed into the
//     DESCRIPTION, which is how the tests prove the orchestrator actually loaded and
//     forwarded them. Stream G moved that echo off Tags: a curated vocabulary has no member
//     meaning "history-2", and inventing one to serve a test fixture would put a value in
//     the product's enum that no recipe should ever carry. Description stays free text, so
//     it is the right place for a fixture to smuggle a signal through.
//   * A prompt containing "__FAIL__" throws, exercising the 502 path with nothing persisted.
//
// Note what it does NOT do: it returns an already-clean draft. The salvaging of a messy
// model response is RecipeGenerationAssistant's job and is covered exhaustively by
// RecipeGenerationAssistantTests — faking it here would only re-test the fake.
public sealed class FakeRecipeGenerationAssistant : IRecipeGenerationAssistant
{
    public const string FailSentinel = "__FAIL__";

    public Task<GeneratedRecipe> GenerateAsync(
        string request,
        IReadOnlyList<ChatHistoryItem> history,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default)
    {
        if (request.Contains(FailSentinel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated recipe generator failure.");
        }

        // Still a pure function of the inputs — no mutable state, safe under the shared
        // TestServer (see the class note).
        var restrictions = dietaryRestrictions.Count == 0 ? "none" : string.Join('/', dietaryRestrictions);

        var draft = new GeneratedRecipeDraft(
            Title: $"Generated: {request}",
            Description: $"A recipe invented for the integration suite. history-{history.Count} restrictions-{restrictions}",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 10,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Other,
            CaloriesPerServing: 320,
            Ingredients: [new RecipeIngredient { Name = "generated ingredient", Quantity = 1m, Unit = UnitOfMeasure.Piece }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Cook the generated ingredient." }],
            Tags: [RecipeTag.Dinner]);

        return Task.FromResult(new GeneratedRecipe(draft, new ChatTokenUsage(120, 240, 360)));
    }
}
