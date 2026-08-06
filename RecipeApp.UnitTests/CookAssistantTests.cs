using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.Recipes;

namespace RecipeApp.UnitTests;

// Stream M's assistant seam, through a faked IChatMessageCaller. Two things are under test:
// what the model is TOLD (the bounded grounding block, which is the feature) and what is done
// with what it says back (refusal as a structured field, not a tone).
public class CookAssistantTests
{
    private static readonly ChatTokenUsage FakeUsage = new(300, 60, 400);

    private sealed class FakeCaller : IChatMessageCaller
    {
        private readonly string _json;

        public FakeCaller(string json) => _json = json;

        public string? CapturedSystemPrompt { get; private set; }
        public IReadOnlyList<ChatHistoryItem>? CapturedHistory { get; private set; }
        public object? CapturedSchema { get; private set; }

        public Task<ChatMessageCall> CreateJsonMessageAsync(
            string systemPrompt,
            IReadOnlyList<ChatHistoryItem> history,
            string userMessage,
            object? responseSchema = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSystemPrompt = systemPrompt;
            CapturedHistory = history;
            CapturedSchema = responseSchema;
            return Task.FromResult(new ChatMessageCall(_json, FakeUsage));
        }
    }

    private static Recipe TestRecipe() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Tomato Soup",
        Description = "A warming bowl.",
        PrepTimeMinutes = 10,
        CookTimeMinutes = 25,
        Servings = 4,
        Difficulty = DifficultyLevel.Easy,
        Ingredients =
        [
            new RecipeIngredient { Name = "tomatoes", Quantity = 800m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "butter", Quantity = 2m, Unit = UnitOfMeasure.Tablespoon },
            new RecipeIngredient { Name = "black pepper", Quantity = 1m, Unit = UnitOfMeasure.ToTaste },
        ],
        Steps =
        [
            new RecipeStep
            {
                StepNumber = 1,
                Description = "Melt the butter in a heavy pan.",
                DurationSeconds = 120,
                IngredientIndexes = [1],
            },
            new RecipeStep
            {
                StepNumber = 2,
                Description = "Add the tomatoes and roast.",
                DurationSeconds = 1500,
                IngredientIndexes = [0],
                Temperature = new StepTemperature { Value = 180, Unit = TemperatureUnit.Celsius },
            },
        ],
    };

    private static async Task<(CookAssistantAnswer Answer, FakeCaller Caller)> AskAsync(
        string json,
        int targetServings = 4,
        IReadOnlyList<string>? restrictions = null,
        IReadOnlyList<ChatHistoryItem>? history = null,
        Recipe? recipe = null)
    {
        var subject = recipe ?? TestRecipe();
        var caller = new FakeCaller(json);
        var assistant = new CookAssistant(caller);
        var context = new CookContext(
            subject,
            ServingScale.ScaleIngredients(subject.Ingredients, subject.Servings, targetServings),
            targetServings,
            new AiPreferenceContext(restrictions ?? [], []));

        var answer = await assistant.AskAsync(
            context,
            history ?? [],
            "can I use oil instead of butter?");

        return (answer, caller);
    }

    // ── The grounding block ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prompt_carries_the_recipe_its_ingredients_and_its_method()
    {
        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""");
        var prompt = caller.CapturedSystemPrompt!;

        Assert.Contains("Tomato Soup", prompt);
        Assert.Contains("800 g tomatoes", prompt);
        Assert.Contains("2 tbsp butter", prompt);
        Assert.Contains("Melt the butter in a heavy pan.", prompt);
        Assert.Contains("Add the tomatoes and roast.", prompt);
    }

    [Fact]
    public async Task Prompt_carries_stream_Js_typed_step_facts()
    {
        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""");
        var prompt = caller.CapturedSystemPrompt!;

        Assert.Contains("2 min", prompt);       // DurationSeconds 120
        Assert.Contains("25 min", prompt);      // DurationSeconds 1500
        Assert.Contains("180 °C", prompt);      // Temperature
        // D16's indexes are rendered as NAMES: the index is a storage shape, and making the
        // model resolve it is asking it to do a lookup this class can just do.
        Assert.Contains("uses 2 tbsp butter", prompt);
    }

    [Fact]
    public async Task Prompt_states_scaled_quantities_as_fact_and_never_asks_the_model_to_scale()
    {
        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""", targetServings: 8);
        var prompt = caller.CapturedSystemPrompt!;

        // Doubled BEFORE the model saw them — this is the whole "arithmetic, never the model"
        // posture, and 1600 g appearing here is what proves it happened on our side.
        Assert.Contains("1600 g tomatoes", prompt);
        Assert.Contains("4 tbsp butter", prompt);
        Assert.DoesNotContain("800 g tomatoes", prompt);
        Assert.Contains("never recalculate", prompt);

        // The step prose is the AUTHOR'S wording and is not rewritten, so the prompt has to say
        // which number wins when an older recipe's prose disagrees with the list.
        Assert.Contains("the ingredient list is right", prompt);
    }

    [Fact]
    public async Task ToTaste_lines_keep_their_undecorated_form_when_scaled()
    {
        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""", targetServings: 8);
        Assert.Contains("to taste black pepper", caller.CapturedSystemPrompt!);
    }

    [Fact]
    public async Task Dietary_restrictions_are_stated_as_absolute()
    {
        var (_, caller) = await AskAsync(
            """{"answer":"Use oil.","refused":false}""",
            restrictions: ["dairy free"]);
        var prompt = caller.CapturedSystemPrompt!;

        Assert.Contains("dairy free", prompt);
        Assert.Contains("absolute", prompt);
        // The opposite of ChatAssistantService's preference wording, deliberately: a
        // substitution suggested here is one somebody is about to put in a pan.
        Assert.DoesNotContain("tends to prefer", prompt);
    }

    [Fact]
    public async Task The_prompt_instructs_refusal_of_anything_off_recipe()
    {
        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""");
        Assert.Contains("refused=true", caller.CapturedSystemPrompt!);
    }

    // ── The response ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_on_topic_answer_comes_back_unrefused_with_its_usage()
    {
        var (answer, _) = await AskAsync("""{"answer":"Yes — use the same volume of a neutral oil.","refused":false}""");

        Assert.False(answer.Refused);
        Assert.Contains("neutral oil", answer.Answer);
        Assert.Equal(FakeUsage, answer.Usage);
    }

    [Fact]
    public async Task A_refusal_surfaces_as_a_flag_not_just_as_prose()
    {
        // The point of the structured field: the caller renders and the suite asserts on a
        // boolean rather than pattern-matching an apology.
        var (answer, _) = await AskAsync(
            """{"answer":"I can only help with this recipe while you're cooking it.","refused":true}""");

        Assert.True(answer.Refused);
        Assert.Contains("only help with this recipe", answer.Answer);
    }

    [Fact]
    public async Task A_refused_turn_still_reports_its_usage()
    {
        // It cost a provider call. A lane where off-topic questions are free is a lane with a
        // free-calls trick in it.
        var (answer, _) = await AskAsync("""{"answer":"Sorry — recipe questions only.","refused":true}""");
        Assert.Equal(FakeUsage, answer.Usage);
    }

    [Fact]
    public async Task Malformed_json_throws_so_the_orchestrator_can_report_502()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AskAsync("""{"answer": """));
    }

    [Fact]
    public async Task An_empty_answer_throws_rather_than_rendering_a_blank_bubble()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AskAsync("""{"answer":"   ","refused":false}"""));
    }

    [Fact]
    public async Task History_is_trimmed_to_the_same_trailing_window_the_other_lanes_use()
    {
        var history = Enumerable.Range(0, 30)
            .Select(i => new ChatHistoryItem(i % 2 == 0 ? "user" : "assistant", $"turn {i}"))
            .ToList();

        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""", history: history);

        Assert.Equal(20, caller.CapturedHistory!.Count);
        Assert.Equal("turn 10", caller.CapturedHistory![0].Content);
    }

    [Fact]
    public async Task The_custom_schema_is_passed_positionally_and_the_token_by_name()
    {
        // Regression guard for the 2026-07-30 seam bug: a CancellationToken passed positionally
        // binds to responseSchema and is serialized as the schema. If that happened here the
        // captured schema would be a token, not the object.
        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""");

        Assert.NotNull(caller.CapturedSchema);
        Assert.IsNotType<CancellationToken>(caller.CapturedSchema);
    }

    [Fact]
    public async Task A_step_referencing_a_line_that_no_longer_exists_is_grounded_without_it()
    {
        // Impossible to store since J (both validators reject it) but readable in a row written
        // before that. A step is worth grounding on with one name missing.
        var recipe = TestRecipe();
        recipe.Steps[0].IngredientIndexes = [1, 99];

        var (_, caller) = await AskAsync("""{"answer":"Yes.","refused":false}""", recipe: recipe);

        Assert.Contains("uses 2 tbsp butter", caller.CapturedSystemPrompt!);
    }
}
