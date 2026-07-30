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

    // Renamed from "...are_alphabetical_when_a_prefix_is_given" (fix round 1, F1a): this
    // test queries q= (blank), so it was never exercising the prefix branch at all — its
    // name claimed something its body didn't test. It genuinely does check that the
    // BLANK-q result (frequency-selected) still comes back sorted for stable display; see
    // Ingredient_names_prefix_filter_excludes_non_matching_and_orders_alphabetically below
    // for the actual prefix-branch test, and IngredientNamesFrequencyCapTests for the
    // frequency-selection logic itself.
    [Fact]
    public async Task Blank_q_results_are_returned_alphabetically_sorted()
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
        // Blank q selects by "most common" (see the frequency-cap tests), but the final
        // list handed to the client is always re-sorted alphabetically for stable display.
        Assert.Equal(sorted, names);
    }

    // fix round 1, F1b: genuinely exercises the PREFIX branch — the previous "alphabetical"
    // test above queried q= (blank) and never touched this code path at all.
    [Fact]
    public async Task Ingredient_names_prefix_filter_excludes_non_matching_and_orders_alphabetically()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        await MealPlanTestHelper.CreateRecipeAsync(client, "E",
        [
            new RecipeIngredient { Name = "Basil", Quantity = 1m, Unit = "unit" },
            new RecipeIngredient { Name = "Bay leaf", Quantity = 1m, Unit = "unit" },
            new RecipeIngredient { Name = "Banana", Quantity = 1m, Unit = "unit" },
            // Deliberately NOT matching the "Ba" prefix below — proves the filter excludes it.
            new RecipeIngredient { Name = "Carrot", Quantity = 1m, Unit = "unit" },
        ]);

        var names = await client.GetFromJsonAsync<string[]>("/ingredients/names?q=Ba");

        Assert.NotNull(names);
        // Exactly the "Ba*" matches, in alphabetical order — nothing else in the corpus
        // (this class's earlier tests included) starts with "Ba", and "Carrot" never appears.
        Assert.Equal(["Banana", "Basil", "Bay leaf"], names);
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
