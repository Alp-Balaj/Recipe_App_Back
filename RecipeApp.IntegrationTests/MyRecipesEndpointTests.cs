using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// GET /recipes/mine — the caller's own recipes, private drafts included, keyset-paged.
// Replaces the SPA's "page the global list and filter by author in the browser" approach,
// which lost any recipe that fell past the page cap.
//
// Tests share one database (class fixture), so — same discipline as RecipeListEndpointsTests
// — assertions scope by a unique marker tag or assert id presence/absence, never "the list
// contains only X". The one exception is the fresh-user tests, where the caller provably
// owns nothing else.
public class MyRecipesEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Mine_ReturnsOnlyCallersRecipes_IncludingPrivateOnes()
    {
        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        var otherPublic = await SeedAsync(otherClient, "Someone Else's Public Dish");

        var client = factory.CreateClient();
        var me = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var mine = await SeedAsync(client, "My Public Dish");
        var myPrivate = await SeedAsync(client, "My Private Draft", visibility: RecipeVisibility.Private);

        var body = await GetMineAsync(client);

        var ids = body.Items.Select(r => r.Id).ToList();
        Assert.Contains(mine.Id, ids);
        // The whole point of the endpoint: a private draft is invisible in GET /recipes for
        // everyone (it isn't Public), but its author must still see it in "My recipes".
        Assert.Contains(myPrivate.Id, ids);
        Assert.DoesNotContain(otherPublic.Id, ids);
        Assert.All(body.Items, r => Assert.Equal(me.UserId, r.CreatedByUserId));
    }

    // The bug this endpoint exists to fix. The caller writes ONE recipe, then a second user
    // writes enough newer ones to push it off the first page of the global list. Filtering
    // /recipes?limit=N client-side would miss it; /recipes/mine must return it on page one.
    [Fact]
    public async Task Mine_FindsRecipesThatFallPastTheGlobalListsFirstPage()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var buried = await SeedAsync(client, "Buried Under Newer Recipes");

        var noisyClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(noisyClient);
        for (var i = 0; i < 6; i++)
        {
            await SeedAsync(noisyClient, $"Newer Noise {i}");
        }

        // Proof the setup actually buries it: the caller's recipe is NOT on the global list's
        // first page, so the old client-side filter over that page would have found nothing.
        var globalFirstPage = await client.GetFromJsonAsync<RecipeListResponse>("/recipes?limit=5", TestJson.Options);
        Assert.DoesNotContain(buried.Id, globalFirstPage!.Items.Select(r => r.Id));

        var minePage = await client.GetFromJsonAsync<RecipeListResponse>("/recipes/mine?limit=5", TestJson.Options);

        Assert.Contains(buried.Id, minePage!.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Mine_FreshUser_ReturnsEmpty()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var body = await GetMineAsync(client);

        Assert.Empty(body.Items);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task Mine_PagesByKeyset_NewestFirstNoDuplicatesOrSkips()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var seeded = new List<RecipeResponse>();
        for (var i = 1; i <= 5; i++)
        {
            seeded.Add(await SeedAsync(client, $"Paged {i}"));
        }

        var first = await client.GetFromJsonAsync<RecipeListResponse>("/recipes/mine?limit=2", TestJson.Options);
        Assert.Equal(2, first!.Items.Count);
        Assert.NotNull(first.NextCursor);

        var walked = new List<Guid>(first.Items.Select(r => r.Id));
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await client.GetFromJsonAsync<RecipeListResponse>(
                $"/recipes/mine?limit=2&cursor={Uri.EscapeDataString(cursor)}", TestJson.Options);
            walked.AddRange(page!.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        }

        // CreatedAt DESC / Id DESC, exactly the seeds in reverse creation order — the pages
        // are dense (no other user's rows consuming slots) and nothing is skipped or repeated.
        Assert.Equal(seeded.AsEnumerable().Reverse().Select(r => r.Id).ToList(), walked);
    }

    [Fact]
    public async Task Mine_CarriesFullRecipeResponse_IncludingIngredients()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await SeedAsync(client, "Full Payload Check");

        var body = await GetMineAsync(client);

        // The recipe picker reads ingredients straight off the list response to show a
        // dish's shopping consequence without a follow-up GET per recipe.
        var item = Assert.Single(body.Items);
        Assert.NotEmpty(item.Ingredients);
        Assert.NotEmpty(item.Steps);
    }

    [Fact]
    public async Task Mine_AppliesTheSameFiltersAsTheGlobalList()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var marker = $"mine-{Guid.NewGuid():N}";
        var italian = await SeedAsync(client, "Filterable Italian", cuisine: "Italian", tags: [marker]);
        await SeedAsync(client, "Filterable Thai", cuisine: "Thai", tags: [marker]);

        var byCuisine = await client.GetFromJsonAsync<RecipeListResponse>(
            $"/recipes/mine?tags={marker}&cuisine=italian", TestJson.Options);

        var item = Assert.Single(byCuisine!.Items);
        Assert.Equal(italian.Id, item.Id);
    }

    [Theory]
    [InlineData("/recipes/mine?limit=0")]
    [InlineData("/recipes/mine?cursor=not-a-cursor")]
    [InlineData("/recipes/mine?difficulty=99")]
    public async Task Mine_InvalidQuery_Returns400(string path)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Unlike GET /recipes, this route is NOT anonymous-capable — "mine" has no meaning
    // without a caller, so the RequireAuthenticatedUser fallback policy answers 401.
    [Fact]
    public async Task Mine_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/recipes/mine");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // "mine" is not a guid, so it can never be captured by GET /recipes/{id:guid} — this
    // pins that, since the two routes share a path segment.
    [Fact]
    public async Task Mine_DoesNotShadowTheDetailRoute()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await SeedAsync(client, "Detail Route Still Works");

        var detail = await client.GetFromJsonAsync<RecipeResponse>($"/recipes/{recipe.Id}", TestJson.Options);

        Assert.Equal(recipe.Id, detail!.Id);
    }

    // --- helpers ------------------------------------------------------------------------

    private static async Task<RecipeListResponse> GetMineAsync(HttpClient client)
    {
        var response = await client.GetAsync("/recipes/mine");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RecipeListResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> SeedAsync(
        HttpClient client,
        string title,
        RecipeVisibility visibility = RecipeVisibility.Public,
        string? cuisine = "Test",
        List<string>? tags = null)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "A recipe used to exercise the /recipes/mine endpoint.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: cuisine,
            CaloriesPerServing: 300,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "Salt", Quantity = 1m, Unit = "pinch" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Season and serve." }],
            Tags: tags ?? ["mine-test"]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
