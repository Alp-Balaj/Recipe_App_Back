using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// POST /recipes/import/url and /recipes/import/photo (stream L). The real RecipeImportService
// runs against the container DB; the two outside-world seams are faked (FakeRecipePageFetcher,
// FakeRecipeExtractionAssistant).
//
// Shared-DB class: every test registers its own user and scopes assertions to that user's ids.
//
// The load-bearing assertions are stream L's three promises:
//   1. a page with JSON-LD costs NO model call (asserted two independent ways);
//   2. an import lands PRIVATE, whatever the author's own default says (D15);
//   3. SourceUrl is set once and cannot be edited afterwards (D15).
public class RecipeImportEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private const string Host = "https://recipes.example.test";

    // Mirrors RankingService.PointsFor(RecipeCreated) — the award this lane must NOT make,
    // for the same anti-farming reason D1 gives the generator.
    private const int RecipeCreated = 20;

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string path) =>
        client.PostAsJsonAsync(
            "/recipes/import/url", new ImportRecipeFromUrlRequest(Host + path), TestJson.Options);

    private static async Task<ImportRecipeResponse> ImportOkAsync(HttpClient client, string path)
    {
        var response = await ImportAsync(client, path);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImportRecipeResponse>(TestJson.Options))!;
    }

    private static MultipartFormDataContent Photo(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", "recipe.png" } };
    }

    private async Task<int> GetRankAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.CookingRank).SingleAsync();
    }

    // ── Tier 1, the deterministic path ──────────────────────────────────────────────────

    [Fact]
    public async Task StructuredPage_ImportsWithoutCallingTheModel()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath);

        // Two independent proofs that no model ran — see FakeRecipeExtractionAssistant's note.
        // The branch the orchestrator says it took...
        Assert.Equal(RecipeImportSource.StructuredData, result.Source);
        // ...and the content itself, which the model could not have produced.
        Assert.Equal(FakeRecipePageFetcher.StructuredTitle, result.Recipe.Title);
        Assert.DoesNotContain(FakeRecipeExtractionAssistant.ExtractedPrefix, result.Recipe.Title);

        // Null budget is the third: the orchestrator only reports one when it spent something.
        Assert.Null(result.Budget);
    }

    [Fact]
    public async Task StructuredPage_ReadsTheWholeRecipe()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = (await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath)).Recipe;

        Assert.Equal(10, recipe.PrepTimeMinutes);
        Assert.Equal(35, recipe.CookTimeMinutes);
        Assert.Equal(4, recipe.Servings);
        Assert.Equal(Cuisine.British, recipe.CuisineType);
        Assert.Equal(510, recipe.CaloriesPerServing);
        Assert.Equal(4, recipe.Ingredients.Count);
        Assert.Equal(4, recipe.Steps.Count);
    }

    // Stream J's typed step shape, recovered from prose the source wrote for a human.
    [Fact]
    public async Task StructuredPage_ProducesTypedSteps()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = (await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath)).Recipe;

        // "Soften the onion for 8 minutes."
        Assert.Equal(480, recipe.Steps[1].DurationSeconds);

        // "Add the butter beans and bake at 180 C for 25 minutes."
        var bake = recipe.Steps[2];
        Assert.Equal(1500, bake.DurationSeconds);
        Assert.NotNull(bake.Temperature);
        Assert.Equal(180, bake.Temperature.Value);
        Assert.Equal(TemperatureUnit.Celsius, bake.Temperature.Unit);

        // D15: the duration and the temperature were READ from the prose, never removed from it.
        Assert.Contains("180 C", bake.Description);
        Assert.Contains("25 minutes", bake.Description);
    }

    // D16, end to end. Step 0 names the oil, step 1 the onion, step 2 the butter beans.
    [Fact]
    public async Task StructuredPage_LinksStepsToIngredientsByIndex()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = (await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath)).Recipe;

        Assert.Equal([1], recipe.Steps[0].IngredientIndexes);
        Assert.Equal([2], recipe.Steps[1].IngredientIndexes);
        Assert.Equal([0], recipe.Steps[2].IngredientIndexes);

        // Every stored index addresses a real line — D16's invariant, checked at rest.
        Assert.All(recipe.Steps, step =>
            Assert.All(step.IngredientIndexes, i =>
            {
                Assert.InRange(i, 0, recipe.Ingredients.Count - 1);
            }));
    }

    // Re-hosting: Recipe.ImageUrl must never hold the source's foreign address.
    [Fact]
    public async Task StructuredPage_RehostsTheSourceImage()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = (await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath)).Recipe;

        Assert.NotNull(recipe.ImageUrl);
        Assert.NotEqual(FakeRecipePageFetcher.StructuredImageUrl, recipe.ImageUrl);
        Assert.DoesNotContain("images.example.test", recipe.ImageUrl);
    }

    // ── D15, the provenance promises ────────────────────────────────────────────────────

    // The single most important assertion in this file. The author's DefaultRecipeVisibility is
    // Public out of the box, so an import that deferred to it would republish a stranger's
    // writing into a social feed as the consequence of pasting a link.
    [Fact]
    public async Task Import_LandsPrivate_EvenWhenTheAuthorsDefaultIsPublic()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == auth.UserId);
            user.DefaultRecipeVisibility = RecipeVisibility.Public;
            await db.SaveChangesAsync();
        }

        var result = await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath);

        Assert.Equal(RecipeVisibility.Private, result.Recipe.Visibility);
    }

    [Fact]
    public async Task Import_RecordsWhereItCameFrom()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath);

        Assert.Equal(Host + FakeRecipePageFetcher.StructuredPath, result.Recipe.SourceUrl);
    }

    // Immutability, and note what makes it true: UpdateRecipeRequest has no SourceUrl field to
    // send, and UpdateRecipeAsync assigns named fields. This test guards that absence — the
    // property would regress silently the day somebody adds the field for symmetry.
    [Fact]
    public async Task SourceUrl_SurvivesAFullReplaceUpdate()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var imported = (await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath)).Recipe;

        var update = new UpdateRecipeRequest(
            "Renamed entirely", "A completely different description.",
            5, 5, 2, DifficultyLevel.Easy, null, null, null, RecipeVisibility.Private,
            [new Domain.ValueObjects.RecipeIngredient { Name = "flour", Quantity = 1m, Unit = UnitOfMeasure.Cup }],
            [new Domain.ValueObjects.RecipeStep { StepNumber = 1, Description = "Mix it." }],
            []);

        var response = await client.PutAsJsonAsync($"/recipes/{imported.Id}", update, TestJson.Options);
        response.EnsureSuccessStatusCode();

        var updated = (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
        Assert.Equal("Renamed entirely", updated.Title);
        Assert.Equal(imported.SourceUrl, updated.SourceUrl);
    }

    // An import is NOT an AI-generated recipe, even when the model transcribed it. The flag
    // would tell every reader a real author's work was machine-written.
    [Theory]
    [InlineData(FakeRecipePageFetcher.StructuredPath)]
    [InlineData(FakeRecipePageFetcher.UnstructuredPath)]
    public async Task Import_IsNeverFlaggedAsAiGenerated(string path)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ImportOkAsync(client, path);

        Assert.False(result.Recipe.IsAiGenerated);
        Assert.Null(result.Recipe.SourceConversationId);
    }

    // The anti-farming rule. Pasting URLs is a cheaper way to hold down a button than
    // generating, so the +20 D1 already refused the generator is refused here too.
    [Fact]
    public async Task Import_AwardsNoRank()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var before = await GetRankAsync(auth.UserId);
        await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath);
        var after = await GetRankAsync(auth.UserId);

        Assert.Equal(before, after);
        Assert.NotEqual(before + RecipeCreated, after);
    }

    // ── Tier 1's fallback ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnstructuredPage_FallsBackToTheModel()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ImportOkAsync(client, FakeRecipePageFetcher.UnstructuredPath);

        Assert.Equal(RecipeImportSource.PageExtraction, result.Source);
        Assert.StartsWith(FakeRecipeExtractionAssistant.ExtractedPrefix, result.Recipe.Title);
        // The budget rides back only on a path that spent something.
        Assert.NotNull(result.Budget);
    }

    // A Recipe node with a name and nothing else is a listing card, not a recipe — the parser
    // declines it and the model gets the page instead.
    [Fact]
    public async Task StubRecipeNode_FallsBackToTheModel()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ImportOkAsync(client, FakeRecipePageFetcher.StubPath);

        Assert.Equal(RecipeImportSource.PageExtraction, result.Source);
    }

    // The normaliser's defaults are on the fallback path, since the fake draft omits both.
    [Fact]
    public async Task ExtractedRecipe_GetsTheAbsentValueDefaults()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = (await ImportOkAsync(client, FakeRecipePageFetcher.UnstructuredPath)).Recipe;

        // "The quantities as published are one batch."
        Assert.Equal(1, recipe.Servings);
        // A missing description falls back to the title rather than being invented.
        Assert.Equal(recipe.Title, recipe.Description);
        Assert.Equal(DifficultyLevel.Medium, recipe.Difficulty);
    }

    // ── Tier 2 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Photo_ImportsAPrivateRecipeWithNoSourceUrl()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/recipes/import/photo", Photo(OnePixelPng));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<ImportRecipeResponse>(TestJson.Options))!;

        Assert.Equal(RecipeImportSource.PhotoExtraction, result.Source);
        Assert.Equal(FakeRecipeExtractionAssistant.PhotoTitle, result.Recipe.Title);
        Assert.Equal(RecipeVisibility.Private, result.Recipe.Visibility);
        // A photograph has no address, so there is nothing a reader could go and check.
        Assert.Null(result.Recipe.SourceUrl);
        Assert.NotNull(result.Budget);
    }

    // Validated by the SAME magic-byte sniff an upload gets, and BEFORE the provider call —
    // so a mislabelled file is a free 400 here rather than a paid vision call that fails at
    // Gemini's allow-list.
    [Fact]
    public async Task Photo_RejectsAFileThatIsNotAnImage()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00];
        var response = await client.PostAsync("/recipes/import/photo", Photo(gif, "image/gif"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Photo_RejectsARequestWithNoFile()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var content = new MultipartFormDataContent { { new StringContent("nope"), "note" } };
        var response = await client.PostAsync("/recipes/import/photo", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── The failure surface ─────────────────────────────────────────────────────────────

    // A policy rejection is the caller's to fix, so 400 with the field named.
    [Fact]
    public async Task RejectedAddress_Is400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await ImportAsync(client, FakeRecipePageFetcher.RejectedPath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Somebody else's server being down is a 502, not a 400 — re-typing the URL will not help.
    [Fact]
    public async Task UnreachableAddress_Is502()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await ImportAsync(client, FakeRecipePageFetcher.UnreachablePath);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // A model that read the page and honestly found no recipe. 422, not 502: nothing went
    // wrong, and a 400/502 would invite the user to retry something that cannot work.
    [Fact]
    public async Task PageWithNoRecipeInIt_Is422()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await ImportAsync(
            client, $"{FakeRecipePageFetcher.UnstructuredPath}?q={FakeRecipeExtractionAssistant.EmptySentinel}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ExtractorFailure_Is502_AndPersistsNothing()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await ImportAsync(
            client, $"{FakeRecipePageFetcher.UnstructuredPath}?q={FakeRecipeExtractionAssistant.FailSentinel}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Recipes.AnyAsync(r => r.CreatedByUserId == auth.UserId));
        // Nothing billed either: RecordCall is staged on the same unit of work as the insert,
        // and the failure returned before either.
        Assert.False(await db.AiUsageRecords.AnyAsync(u => u.UserId == auth.UserId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyUrl_Is400(string url)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/recipes/import/url", new ImportRecipeFromUrlRequest(url), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonAbsoluteUrl_Is400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/recipes/import/url", new ImportRecipeFromUrlRequest("not-a-url"), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_RequiresAuthentication()
    {
        var client = factory.CreateClient();

        var response = await ImportAsync(client, FakeRecipePageFetcher.StructuredPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Metering ────────────────────────────────────────────────────────────────────────

    // The lane bills the model-backed path and ONLY it. A zero row on the free path would bury
    // the calls that cost money among the ones that did not.
    [Fact]
    public async Task StructuredImport_RecordsNoUsage_ButExtractionDoes()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.AiUsageRecords.AnyAsync(u => u.UserId == auth.UserId));
        }

        await ImportOkAsync(client, FakeRecipePageFetcher.UnstructuredPath);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.AiUsageRecords.Where(u => u.UserId == auth.UserId).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal("recipe-import", row.Lane);
        }
    }

    // The recipe and its cost commit together — the unit-of-work discipline every lane uses.
    [Fact]
    public async Task ExtractedImport_CommitsTheRecipeAndItsCostTogether()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ImportOkAsync(client, FakeRecipePageFetcher.UnstructuredPath);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.True(await db.Recipes.AnyAsync(r => r.Id == result.Recipe.Id));
        Assert.True(await db.AiUsageRecords.AnyAsync(u => u.UserId == auth.UserId));
    }

    // ── Ingredient resolution (D8) ──────────────────────────────────────────────────────

    // Import produces free-text names off a stranger's page, so misses are the common case and
    // stay null. This pins that an unresolvable line SAVES rather than failing the write —
    // the property D8 exists to protect.
    [Fact]
    public async Task UnresolvableIngredientsStillSave()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = (await ImportOkAsync(client, FakeRecipePageFetcher.StructuredPath)).Recipe;

        // "Salt and black pepper" is not a catalogue entry and must not have become one.
        var unmeasured = recipe.Ingredients.Single(i => i.Name.Contains("black pepper"));
        Assert.Null(unmeasured.IngredientId);
        // Unmeasured lines encode as ToTaste, which the shopping list never sums.
        Assert.Equal(UnitOfMeasure.ToTaste, unmeasured.Unit);
    }
}
