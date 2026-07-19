using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.IntegrationTests;

// meal-planning plan, cp03: GET/POST /shopping-list, PATCH/DELETE /shopping-list/{id}.
// Fresh users per test (shared Testcontainers DB), mirroring SocialInteractionEndpointsTests'
// / MealPlanEndpointsTests' style.
public class ShoppingListEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- add --------------------------------------------------------------------------------

    [Fact]
    public async Task AddItem_Returns201WithShapeAndNullMealPlanId()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/shopping-list", new AddShoppingListItemRequest("Flour", "2.5 cups"), TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal("Flour", body.Ingredient);
        Assert.Equal("2.5 cups", body.Quantity);
        Assert.False(body.IsPurchased);
        Assert.Null(body.MealPlanId);
        Assert.Equal($"/shopping-list/{body.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task AddItem_BlankIngredient_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/shopping-list", new AddShoppingListItemRequest("", "2 cups"), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_BlankQuantity_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/shopping-list", new AddShoppingListItemRequest("Flour", ""), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- list -------------------------------------------------------------------------------

    [Fact]
    public async Task GetShoppingList_ReturnsOnlyCallersItems()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var mine = await AddItemAsync(ownerClient, "Flour", "1 kg");

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await AddItemAsync(otherClient, "Sugar", "500 g");

        var listed = await ownerClient.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);

        var ids = listed!.Items.Select(i => i.Id).ToList();
        Assert.Contains(mine.Id, ids);
        Assert.Single(listed.Items);
    }

    [Fact]
    public async Task GetShoppingList_PageWalk_ReturnsAllWithoutDuplicatesStableOrder()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var item = await AddItemAsync(client, $"Ingredient {i}", "1 unit");
            created.Add(item.Id);
        }

        var seen = new List<ShoppingListItemResponse>();
        string? cursor = null;
        do
        {
            var url = $"/shopping-list?limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await client.GetFromJsonAsync<ShoppingListItemListResponse>(url, TestJson.Options);
            Assert.NotNull(page);
            Assert.True(page!.Items.Count <= 2);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Select(i => i.Id).Distinct().Count(), seen.Count);
        Assert.Equal(created.OrderBy(id => id), seen.Select(i => i.Id).OrderBy(id => id));

        // CreatedAt DESC / Id DESC: the walk must yield a strictly non-increasing key across
        // pages, tie-broken by Id (Postgres timestamp precision can legitimately collide).
        for (var i = 1; i < seen.Count; i++)
        {
            var newer = seen[i - 1];
            var older = seen[i];
            Assert.True(
                older.CreatedAt < newer.CreatedAt
                || (older.CreatedAt == newer.CreatedAt && older.Id.CompareTo(newer.Id) < 0));
        }
    }

    [Theory]
    [InlineData("!!!not-base64url!!!")] // invalid base64url characters
    [InlineData("aGVsbG8")]             // base64url("hello") — not JSON
    [InlineData("e30")]                 // base64url("{}") — missing t and i
    public async Task GetShoppingList_MalformedCursor_Returns400(string cursor)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/shopping-list?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task GetShoppingList_NonPositiveLimit_Returns400(int limit)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/shopping-list?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- patch ------------------------------------------------------------------------------

    [Fact]
    public async Task PatchItem_SetsIsPurchased_Returns204AndPersists()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var item = await AddItemAsync(client, "Eggs", "12");

        var response = await client.PatchAsJsonAsync($"/shopping-list/{item.Id}", new UpdateShoppingListItemRequest(true), TestJson.Options);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var listed = await client.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        var persisted = Assert.Single(listed!.Items, i => i.Id == item.Id);
        Assert.True(persisted.IsPurchased);
    }

    [Fact]
    public async Task PatchItem_Idempotent_RepeatedCallsSameState()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var item = await AddItemAsync(client, "Milk", "1 L");

        var first = await client.PatchAsJsonAsync($"/shopping-list/{item.Id}", new UpdateShoppingListItemRequest(true), TestJson.Options);
        var second = await client.PatchAsJsonAsync($"/shopping-list/{item.Id}", new UpdateShoppingListItemRequest(true), TestJson.Options);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var listed = await client.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        var persisted = Assert.Single(listed!.Items, i => i.Id == item.Id);
        Assert.True(persisted.IsPurchased);
    }

    [Fact]
    public async Task PatchItem_CrossUser_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var item = await AddItemAsync(ownerClient, "Butter", "250 g");

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PatchAsJsonAsync($"/shopping-list/{item.Id}", new UpdateShoppingListItemRequest(true), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchItem_Unknown_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PatchAsJsonAsync($"/shopping-list/{Guid.NewGuid()}", new UpdateShoppingListItemRequest(true), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- delete -----------------------------------------------------------------------------

    [Fact]
    public async Task DeleteItem_Returns204ThenReDeleteReturns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var item = await AddItemAsync(client, "Yeast", "1 packet");

        var first = await client.DeleteAsync($"/shopping-list/{item.Id}");
        var second = await client.DeleteAsync($"/shopping-list/{item.Id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var listed = await client.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        Assert.DoesNotContain(listed!.Items, i => i.Id == item.Id);
    }

    [Fact]
    public async Task DeleteItem_CrossUser_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var item = await AddItemAsync(ownerClient, "Salt", "1 box");

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.DeleteAsync($"/shopping-list/{item.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Still there for the real owner — the cross-user 404 must not have deleted it.
        var listed = await ownerClient.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        Assert.Contains(listed!.Items, i => i.Id == item.Id);
    }

    // --- helpers ------------------------------------------------------------------------

    private static async Task<ShoppingListItemResponse> AddItemAsync(HttpClient client, string ingredient, string quantity)
    {
        var response = await client.PostAsJsonAsync("/shopping-list", new AddShoppingListItemRequest(ingredient, quantity), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options))!;
    }
}
