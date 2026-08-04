using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// open-loops slice 2: ?search= over the stored generated tsvector (title + description +
// ingredient names) with a GIN index.
//
// The shared Testcontainers database is never reset between tests, so every case searches
// for a NONSENSE TOKEN unique to that test. That is not a workaround — it is also the only
// way to assert "exactly one result" against a database other tests are filling.
public class RecipeSearchEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    /// A token no other test or stemmer will collide with. Letters only, so the English
    /// dictionary leaves it alone rather than stemming a trailing digit run.
    private static string Token() =>
        "zq" + new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(8).ToArray());

    [Fact]
    public async Task Search_MatchesOnTitle()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        var created = await CreateRecipeAsync(client, title: $"A {token} bake");

        var page = await SearchAsync(client, token);

        var only = Assert.Single(page.Items);
        Assert.Equal(created.Id, only.Id);
    }

    [Fact]
    public async Task Search_MatchesOnDescription()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        var created = await CreateRecipeAsync(client, description: $"Best served {token} and warm.");

        var page = await SearchAsync(client, token);

        Assert.Equal(created.Id, Assert.Single(page.Items).Id);
    }

    // The one that needed a new capability: Ingredients is an opaque jsonb scalar with no
    // LINQ translation, so this only works because the generated column reaches into it
    // with jsonb_path_query_array. If the jsonpath or its casing is wrong, this is the
    // test that fails.
    [Fact]
    public async Task Search_MatchesOnAnIngredientNameAlone()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        var created = await CreateRecipeAsync(
            client,
            title: "Nothing distinctive here",
            description: "Nor here.",
            ingredients: [new RecipeIngredient { Name = token, Quantity = 1m, Unit = UnitOfMeasure.Teaspoon }]);

        var page = await SearchAsync(client, token);

        Assert.Equal(created.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_IsCaseInsensitiveAndStems()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        var created = await CreateRecipeAsync(client, description: $"{token} Caramelised onions");

        // Different case, and "caramelising" stems to the same lexeme as "caramelised".
        var page = await SearchAsync(client, $"{token} CARAMELISING");

        Assert.Equal(created.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_AnotherUsersPrivateRecipe_IsNeverFindable()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var token = Token();
        var secret = await CreateRecipeAsync(ownerClient, title: $"Secret {token} loaf", visibility: RecipeVisibility.Private);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        // The visibility predicate composes FIRST, so search can only ever narrow it.
        Assert.Empty((await SearchAsync(otherClient, token)).Items);
        // …but the author finds their own private draft.
        Assert.Equal(secret.Id, Assert.Single((await SearchAsync(ownerClient, token)).Items).Id);
    }

    [Fact]
    public async Task Search_AnonymousCaller_SeesOnlyPublicMatches()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var token = Token();
        var open = await CreateRecipeAsync(ownerClient, title: $"Open {token} tart");
        await CreateRecipeAsync(ownerClient, title: $"Closed {token} tart", visibility: RecipeVisibility.Private);

        var guestClient = factory.CreateClient();
        var page = await SearchAsync(guestClient, token);

        Assert.Equal(open.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_PageWalk_ReturnsEveryMatchWithoutDuplicates()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        for (var i = 0; i < 3; i++)
        {
            await CreateRecipeAsync(client, title: $"{token} number {i}");
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/recipes?search={token}&limit=1" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await client.GetFromJsonAsync<RecipeListResponse>(url, TestJson.Options))!;
            seen.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task Search_ComposesWithTheOtherFilters()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        var easy = await CreateRecipeAsync(client, title: $"{token} quick", difficulty: DifficultyLevel.Easy);
        await CreateRecipeAsync(client, title: $"{token} slow", difficulty: DifficultyLevel.Hard);

        var page = (await client.GetFromJsonAsync<RecipeListResponse>(
            $"/recipes?search={token}&difficulty=Easy", TestJson.Options))!;

        Assert.Equal(easy.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_OnMine_NarrowsToTheCallersOwnRecipes()
    {
        var token = Token();

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await CreateRecipeAsync(otherClient, title: $"{token} by someone else");

        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var mine = await CreateRecipeAsync(client, title: $"{token} by me");

        var page = (await client.GetFromJsonAsync<RecipeListResponse>(
            $"/recipes/mine?search={token}", TestJson.Options))!;

        Assert.Equal(mine.Id, Assert.Single(page.Items).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_BlankTerm_IsNotAFilter(string term)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await CreateRecipeAsync(client, title: "Blank search still lists");

        var page = (await client.GetFromJsonAsync<RecipeListResponse>(
            $"/recipes?search={Uri.EscapeDataString(term)}", TestJson.Options))!;

        Assert.NotEmpty(page.Items);
    }

    // websearch_to_tsquery never throws on user input — unlike to_tsquery, which is why it
    // is the parser here and why there is nothing to escape. These would be 500s otherwise.
    [Theory]
    [InlineData("\"unclosed quote")]
    [InlineData("and or not")]
    [InlineData("!@#$%^&*()")]
    [InlineData("50%")]
    [InlineData("a & b | c")]
    public async Task Search_HostileInput_Returns200NotAnError(string term)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/recipes?search={Uri.EscapeDataString(term)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Search_QuotedPhrase_MatchesOnlyTheAdjacentWords()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var token = Token();
        var adjacent = await CreateRecipeAsync(client, description: $"{token} slow roasted lamb");
        await CreateRecipeAsync(client, description: $"{token} roasted then slow cooled");

        var page = (await client.GetFromJsonAsync<RecipeListResponse>(
            $"/recipes?search={Uri.EscapeDataString($"{token} \"slow roasted\"")}", TestJson.Options))!;

        Assert.Equal(adjacent.Id, Assert.Single(page.Items).Id);
    }

    // --- helpers ------------------------------------------------------------------------

    private static Task<RecipeListResponse> SearchAsync(HttpClient client, string term) =>
        client.GetFromJsonAsync<RecipeListResponse>($"/recipes?search={Uri.EscapeDataString(term)}", TestJson.Options)!;

    private static async Task<RecipeResponse> CreateRecipeAsync(
        HttpClient client,
        string title = "Search Test Dish",
        string description = "A recipe used to exercise full-text search.",
        RecipeVisibility visibility = RecipeVisibility.Public,
        DifficultyLevel difficulty = DifficultyLevel.Easy,
        List<RecipeIngredient>? ingredients = null)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: description,
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 4,
            Difficulty: difficulty,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: 210,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: ingredients ?? [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = UnitOfMeasure.Cup }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
            Tags: [RecipeTag.Bread]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
