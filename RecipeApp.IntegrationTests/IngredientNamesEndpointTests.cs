using System.Net.Http.Json;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// task-10 (meal-planning-week-shopping-rework): GET /ingredients/names backs the recipe-form
// autocomplete. It does not repair existing IngredientKey groupings — it slows the corpus
// from diverging further, so shopping-list grouping only gets better over time.
public class IngredientNamesEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Ingredient_names_are_distinct_prefix_matched_and_capped()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        await MealPlanTestHelper.CreateRecipeAsync(client, "A",
        [
            new RecipeIngredient { Name = "Flour", Quantity = 1m, Unit = "cup" },
            new RecipeIngredient { Name = "Flaked almonds", Quantity = 1m, Unit = "cup" },
        ]);
        await MealPlanTestHelper.CreateRecipeAsync(client, "B",
        [
            new RecipeIngredient { Name = "flour", Quantity = 2m, Unit = "cups" },
        ]);

        var names = await client.GetFromJsonAsync<string[]>("/ingredients/names?q=fl");

        Assert.Contains(names!, n => n.Equals("Flour", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names!, n => n.StartsWith("Flaked", StringComparison.OrdinalIgnoreCase));
        Assert.True(names!.Length <= 20);
        // Distinct case-insensitively: "Flour" and "flour" are one suggestion.
        Assert.Single(names!, n => n.Equals("flour", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ingredient_names_are_alphabetical_when_a_prefix_is_given()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        await MealPlanTestHelper.CreateRecipeAsync(client, "C",
        [
            new RecipeIngredient { Name = "Zucchini", Quantity = 1m, Unit = "unit" },
            new RecipeIngredient { Name = "Aubergine", Quantity = 1m, Unit = "unit" },
        ]);

        var names = await client.GetFromJsonAsync<string[]>("/ingredients/names?q=");

        Assert.NotNull(names);
        var sorted = names!.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        // Blank q returns "most common" names, not necessarily alphabetical by frequency
        // selection, but the final list is still sorted for stable UI presentation.
        Assert.Equal(sorted, names);
    }

    [Fact]
    public async Task Ingredient_names_exclude_deleted_recipes()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var created = await MealPlanTestHelper.CreateRecipeAsync(client, "D",
        [
            new RecipeIngredient { Name = "Unobtainium root", Quantity = 1m, Unit = "unit" },
        ]);

        var beforeDelete = await client.GetFromJsonAsync<string[]>("/ingredients/names?q=Unobtainium");
        Assert.Contains(beforeDelete!, n => n.Equals("Unobtainium root", StringComparison.OrdinalIgnoreCase));

        var deleteResponse = await client.DeleteAsync($"/recipes/{created.Id}");
        deleteResponse.EnsureSuccessStatusCode();

        var afterDelete = await client.GetFromJsonAsync<string[]>("/ingredients/names?q=Unobtainium");
        Assert.DoesNotContain(afterDelete!, n => n.Equals("Unobtainium root", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ingredient_names_endpoint_requires_authentication()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/ingredients/names?q=fl");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
