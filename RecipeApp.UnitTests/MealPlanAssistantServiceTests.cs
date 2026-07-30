using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.MealPlanning;

namespace RecipeApp.UnitTests;

// Unit tests for MealPlanAssistantService's parsing + slot/id filtering (Stream C),
// exercised through a faked IChatMessageCaller — the same mould as ChatAssistantServiceTests.
// The fake returns whatever raw JSON the test wants and captures the assembled system prompt
// and schema so the grounding block can be asserted.
public class MealPlanAssistantServiceTests
{
    private static readonly Guid RecipeA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RecipeB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeCaller : IChatMessageCaller
    {
        private readonly string _json;

        public FakeCaller(string json) => _json = json;

        public string? CapturedSystemPrompt { get; private set; }
        public object? CapturedSchema { get; private set; }

        public Task<string> CreateJsonMessageAsync(
            string systemPrompt,
            IReadOnlyList<ChatHistoryItem> history,
            string userMessage,
            object? responseSchema = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSystemPrompt = systemPrompt;
            CapturedSchema = responseSchema;
            return Task.FromResult(_json);
        }
    }

    private static ChatCandidateRecipe Candidate(Guid id, string title, params string[] tags) =>
        new(id, title, $"{title} description", "Italian", DifficultyLevel.Easy, 30, 400, tags);

    private static IReadOnlyList<ChatCandidateRecipe> TwoCandidates() =>
        new[] { Candidate(RecipeA, "Tomato Soup", "vegetarian"), Candidate(RecipeB, "Pesto Pasta") };

    private static IReadOnlyList<PlanSlot> TwoOpenSlots() =>
        new[]
        {
            new PlanSlot(DayOfWeek.Monday, MealType.Dinner),
            new PlanSlot(DayOfWeek.Tuesday, MealType.Lunch),
        };

    private static Task<IReadOnlyList<ProposedSlotAssignment>> InvokeAsync(
        string json,
        IReadOnlyList<PlanSlot>? openSlots = null,
        IReadOnlyList<ChatCandidateRecipe>? candidates = null,
        IReadOnlyList<string>? dietary = null)
    {
        var service = new MealPlanAssistantService(new FakeCaller(json));
        return service.ProposeWeekAsync(
            openSlots ?? TwoOpenSlots(),
            candidates ?? TwoCandidates(),
            dietary ?? Array.Empty<string>());
    }

    private static string Slot(string day, string mealType, string recipeId) =>
        $$"""{"day":"{{day}}","mealType":"{{mealType}}","recipeId":"{{recipeId}}"}""";

    [Fact]
    public async Task ValidResponse_FillsBothOpenSlots()
    {
        var json = $$"""{"slots":[{{Slot("Monday", "Dinner", RecipeA.ToString())}},{{Slot("Tuesday", "Lunch", RecipeB.ToString())}}]}""";

        var result = await InvokeAsync(json);

        Assert.Equal(2, result.Count);
        Assert.Equal(new ProposedSlotAssignment(DayOfWeek.Monday, MealType.Dinner, RecipeA), result[0]);
        Assert.Equal(new ProposedSlotAssignment(DayOfWeek.Tuesday, MealType.Lunch, RecipeB), result[1]);
    }

    [Fact]
    public async Task HallucinatedRecipeId_IsDropped_WhileValidSlotSurvives()
    {
        var hallucinated = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var json = $$"""{"slots":[{{Slot("Monday", "Dinner", hallucinated.ToString())}},{{Slot("Tuesday", "Lunch", RecipeB.ToString())}}]}""";

        var result = await InvokeAsync(json);

        var assignment = Assert.Single(result);
        Assert.Equal(RecipeB, assignment.RecipeId);
    }

    [Fact]
    public async Task UnrequestedSlot_IsDropped_EvenWithValidRecipe()
    {
        // Sunday Breakfast was never offered (occupied, by construction) — the model must not
        // be able to overwrite it.
        var json = $$"""{"slots":[{{Slot("Sunday", "Breakfast", RecipeA.ToString())}},{{Slot("Monday", "Dinner", RecipeA.ToString())}}]}""";

        var result = await InvokeAsync(json);

        var assignment = Assert.Single(result);
        Assert.Equal(new PlanSlot(DayOfWeek.Monday, MealType.Dinner), new PlanSlot(assignment.DayOfWeek, assignment.MealType));
    }

    [Fact]
    public async Task DuplicateSlot_KeepsFirstAssignment()
    {
        var json = $$"""{"slots":[{{Slot("Monday", "Dinner", RecipeA.ToString())}},{{Slot("Monday", "Dinner", RecipeB.ToString())}}]}""";

        var result = await InvokeAsync(json);

        var assignment = Assert.Single(result);
        Assert.Equal(RecipeA, assignment.RecipeId);
    }

    [Fact]
    public async Task DuplicateSlot_WithHallucinatedFirst_KeepsValidSecond()
    {
        // The hallucinated first attempt must not burn the slot: after it is dropped, the
        // valid second assignment for the same slot still lands.
        var hallucinated = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var json = $$"""{"slots":[{{Slot("Monday", "Dinner", hallucinated.ToString())}},{{Slot("Monday", "Dinner", RecipeB.ToString())}}]}""";

        var result = await InvokeAsync(json);

        var assignment = Assert.Single(result);
        Assert.Equal(RecipeB, assignment.RecipeId);
    }

    [Theory]
    [InlineData("Funday", "Dinner")]     // unknown day
    [InlineData("Monday", "Brunch")]     // unknown meal type
    [InlineData("17", "Dinner")]         // numeric-string day parses via Enum.TryParse but is undefined
    [InlineData("Monday", "17")]         // same for meal type
    public async Task UnparseableOrUndefinedEnums_AreDropped(string day, string mealType)
    {
        var json = $$"""{"slots":[{{Slot(day, mealType, RecipeA.ToString())}}]}""";

        var result = await InvokeAsync(json);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CaseInsensitiveEnums_AreAccepted()
    {
        var json = $$"""{"slots":[{{Slot("monday", "DINNER", RecipeA.ToString())}}]}""";

        var result = await InvokeAsync(json);

        var assignment = Assert.Single(result);
        Assert.Equal(DayOfWeek.Monday, assignment.DayOfWeek);
        Assert.Equal(MealType.Dinner, assignment.MealType);
    }

    [Fact]
    public async Task EmptySlots_AreValid()
    {
        var result = await InvokeAsync("""{"slots":[]}""");

        Assert.Empty(result);
    }

    [Fact]
    public async Task MalformedJson_ThrowsClearFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync("not json at all"));
    }

    [Fact]
    public async Task MissingSlotsKey_ThrowsClearFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync("""{"reply":"wrong shape"}"""));
    }

    [Fact]
    public async Task SystemPrompt_GroundsOnSlotsCandidatesAndDietaryRestrictions()
    {
        var caller = new FakeCaller("""{"slots":[]}""");
        var service = new MealPlanAssistantService(caller);

        await service.ProposeWeekAsync(
            TwoOpenSlots(),
            TwoCandidates(),
            new[] { "vegetarian", "no nuts" });

        Assert.NotNull(caller.CapturedSystemPrompt);
        var prompt = caller.CapturedSystemPrompt!;
        Assert.Contains("Monday Dinner", prompt);
        Assert.Contains("Tuesday Lunch", prompt);
        Assert.Contains($"id={RecipeA}", prompt);
        Assert.Contains("Tomato Soup", prompt);
        Assert.Contains("vegetarian, no nuts", prompt);
        // The proposal lane must not ride the chat lane's default { reply, suggestedRecipeIds }
        // schema — its own slots schema has to travel through the seam.
        Assert.NotNull(caller.CapturedSchema);
    }
}
