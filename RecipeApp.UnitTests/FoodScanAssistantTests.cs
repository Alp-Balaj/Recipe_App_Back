using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.Scanning;

namespace RecipeApp.UnitTests;

// Unit tests for FoodScanAssistant's parsing + drop-based re-validation (stream N),
// exercised through a faked IVisionMessageCaller — the same mould as
// MealPlanAssistantServiceTests fakes IChatMessageCaller. The fake returns whatever raw
// JSON the test wants and captures the prompt so the transcription discipline can be
// asserted where it lives.
public class FoodScanAssistantTests
{
    private sealed class FakeVisionCaller : IVisionMessageCaller
    {
        private readonly string _json;
        private readonly ChatTokenUsage? _usage;

        public FakeVisionCaller(string json, ChatTokenUsage? usage = null)
        {
            _json = json;
            _usage = usage;
        }

        public string? CapturedSystemPrompt { get; private set; }
        public object? CapturedSchema { get; private set; }
        public IReadOnlyList<VisionImagePart>? CapturedImages { get; private set; }

        public Task<ChatMessageCall> CreateJsonMessageFromImagesAsync(
            string systemPrompt,
            IReadOnlyList<VisionImagePart> images,
            string userMessage,
            object? responseSchema = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSystemPrompt = systemPrompt;
            CapturedSchema = responseSchema;
            CapturedImages = images;
            return Task.FromResult(new ChatMessageCall(_json, _usage));
        }
    }

    private static readonly RecipeImageContent Photo = new([1, 2, 3], "image/jpeg");

    private static async Task<List<string>> DetectAsync(string json)
    {
        var assistant = new FoodScanAssistant(new FakeVisionCaller(json));
        return (await assistant.DetectPantryAsync([Photo])).Names;
    }

    // --- pantry: the empty answer is an answer ---------------------------------------------

    [Fact]
    public async Task EmptyDetection_IsAnEmptyList_NotAFailure()
    {
        var names = await DetectAsync("""{"ingredients":[]}""");
        Assert.Empty(names);
    }

    [Fact]
    public async Task MissingList_IsAnEmptyList()
    {
        var names = await DetectAsync("{}");
        Assert.Empty(names);
    }

    // --- pantry: dropping, never repairing -------------------------------------------------

    [Fact]
    public async Task BlankAndNullNames_AreDropped()
    {
        var names = await DetectAsync("""{"ingredients":["flour", "", "   ", null, "butter"]}""");
        Assert.Equal(["flour", "butter"], names);
    }

    // The dedupe key is IngredientKey — the SAME key the matcher uses downstream — so two
    // spellings that would match identically collapse into one detection. First wins.
    [Fact]
    public async Task DuplicateSpellings_CollapseOnIngredientKey()
    {
        var names = await DetectAsync("""{"ingredients":["Eggs", "egg", "EGG", "flour"]}""");
        Assert.Equal(["Eggs", "flour"], names);
    }

    [Fact]
    public async Task LongNames_AreClippedToTheLineCap()
    {
        var longName = new string('x', 300);
        var names = await DetectAsync($$"""{"ingredients":["{{longName}}"]}""");
        Assert.Equal(100, Assert.Single(names).Length);
    }

    [Fact]
    public async Task DetectionCount_IsCapped()
    {
        var many = string.Join(",", Enumerable.Range(0, 80).Select(i => $"\"item number {i}\""));
        var names = await DetectAsync($$"""{"ingredients":[{{many}}]}""");
        Assert.Equal(50, names.Count);
    }

    [Fact]
    public async Task MalformedJson_Throws_SoTheOrchestratorCanAnswer502()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => DetectAsync("not json at all"));
    }

    [Fact]
    public async Task ProviderUsage_RidesBackWithTheDetection()
    {
        var usage = new ChatTokenUsage(100, 40, 140);
        var assistant = new FoodScanAssistant(new FakeVisionCaller("""{"ingredients":["flour"]}""", usage));

        var detection = await assistant.DetectPantryAsync([Photo]);

        Assert.Equal(usage, detection.Usage);
    }

    // The prompt's one job is stopping the model helping — the two sentences that carry the
    // discipline must actually be in it.
    [Fact]
    public async Task PantryPrompt_ForbidsInventingTheStapleCupboard()
    {
        var caller = new FakeVisionCaller("""{"ingredients":[]}""");
        await new FoodScanAssistant(caller).DetectPantryAsync([Photo]);

        Assert.Contains("Never add a staple", caller.CapturedSystemPrompt);
        Assert.Contains("empty list is a correct answer", caller.CapturedSystemPrompt);
        Assert.NotNull(caller.CapturedSchema);
    }

    // --- receipt ---------------------------------------------------------------------------

    private static async Task<List<Application.Scanning.Abstractions.ReceiptLine>> ReadAsync(string json)
    {
        var assistant = new FoodScanAssistant(new FakeVisionCaller(json));
        return (await assistant.ReadReceiptAsync([Photo])).Items;
    }

    [Fact]
    public async Task ReceiptLines_KeepNameAndFreeTextQuantity()
    {
        var items = await ReadAsync(
            """{"items":[{"name":"Whole milk","quantity":"2 x 1L"},{"name":"Eggs"}]}""");

        Assert.Equal(2, items.Count);
        Assert.Equal("Whole milk", items[0].Name);
        Assert.Equal("2 x 1L", items[0].Quantity);
        Assert.Null(items[1].Quantity);
    }

    // A receipt printing milk twice sold milk twice — the draft shows the receipt.
    [Fact]
    public async Task RepeatedLines_AreKept_NotDeduped()
    {
        var items = await ReadAsync(
            """{"items":[{"name":"Milk","quantity":"1"},{"name":"Milk","quantity":"1"}]}""");

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task NamelessLines_AreDropped()
    {
        var items = await ReadAsync(
            """{"items":[{"quantity":"2"},{"name":"  "},null,{"name":"Bread"}]}""");

        Assert.Equal("Bread", Assert.Single(items).Name);
    }

    // Clipped to the landing zone's caps: ShoppingListItem.Ingredient is 200,
    // .Quantity is 50 — a draft line that could not be confirmed would be a trap.
    [Fact]
    public async Task ReceiptFields_AreClippedToTheManualRowCaps()
    {
        var longName = new string('n', 250);
        var longQuantity = new string('q', 80);
        var items = await ReadAsync(
            $$"""{"items":[{"name":"{{longName}}","quantity":"{{longQuantity}}"}]}""");

        var item = Assert.Single(items);
        Assert.Equal(200, item.Name.Length);
        Assert.Equal(50, item.Quantity!.Length);
    }

    [Fact]
    public async Task EmptyReceipt_IsAnEmptyDraft()
    {
        Assert.Empty(await ReadAsync("""{"items":[]}"""));
    }
}
