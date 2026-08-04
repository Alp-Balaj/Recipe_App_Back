using RecipeApp.Domain.Enums;
using System.Net.Http.Json;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// fix round 1, F1c (task-10, meal-planning-week-shopping-rework): the blank-q "most common
// wins" selection in RecipeService.GetIngredientNamesAsync only does anything observable once
// there are MORE than 20 distinct ingredient names to choose among — otherwise every name
// fits and the ordering never has to drop one. IngredientNamesEndpointTests shares one DB
// across all its [Fact]s (IClassFixture<IntegrationTestFactory> = one Testcontainers Postgres
// per CLASS, not per test), so stacking a 21st-distinct-name scenario there would make the
// pass/fail boundary depend on exactly what earlier tests in that file happened to insert —
// fragile coupling nobody asked for. This gets its own factory (its own empty DB) instead, so
// the 21-name corpus below is the WHOLE corpus and the cap boundary is fully deterministic.
public class IngredientNamesFrequencyCapTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Blank_q_prefers_the_most_common_name_when_the_cap_bites()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        // One name used across 3 recipes (count 3) plus 20 single-use ("rare", count 1)
        // names — 21 distinct names total, one more than the cap of 20, so the selection
        // is forced to drop exactly one. The 20 rares ride in ONE recipe's ingredient list
        // (the scan cap is on RECIPE rows, not ingredient rows — RecipeService.cs), so this
        // needs only 4 POSTs, not 20+.
        for (var i = 0; i < 3; i++)
        {
            await MealPlanTestHelper.CreateRecipeAsync(client, $"Common-{i}",
            [
                new RecipeIngredient { Name = "Zzz Common Grain", Quantity = 1m, Unit = UnitOfMeasure.Piece },
            ]);
        }

        var rareIngredients = Enumerable.Range(1, 20)
            .Select(i => new RecipeIngredient { Name = $"Rare Item {i:D2}", Quantity = 1m, Unit = UnitOfMeasure.Piece })
            .ToList();
        await MealPlanTestHelper.CreateRecipeAsync(client, "Rares", rareIngredients);

        var names = await client.GetFromJsonAsync<string[]>("/ingredients/names?q=");

        Assert.NotNull(names);
        Assert.True(names!.Length <= 20);

        // The count-3 name always outranks any count-1 rare, so it always keeps its slot.
        Assert.Contains(names!, n => n.Equals("Zzz Common Grain", StringComparison.OrdinalIgnoreCase));

        // 21 distinct names, 20 slots: with the common name's slot guaranteed, only 19 of
        // the 20 count-1 rares fit. Same-count ties break alphabetically (RecipeService.cs),
        // so "Rare Item 20" — alphabetically LAST among the rares — is the one dropped, and
        // "Rare Item 01" — alphabetically first — survives.
        Assert.DoesNotContain(names!, n => n.Equals("Rare Item 20", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names!, n => n.Equals("Rare Item 01", StringComparison.OrdinalIgnoreCase));
    }
}
