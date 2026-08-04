using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// GET /recipes/{id}/insights (stream G, slice G4).
///
/// The assertions that matter most here are the honest ones: coverage is reported,
/// unresolved lines are counted rather than ignored, and a clean result is
/// "nothing found", never "safe".
/// </summary>
public class RecipeInsightsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Nutrition_is_computed_from_the_catalogue()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // 200 g of flour at ~364 kcal/100 g over 2 servings ≈ 364 kcal each.
        var recipe = await CreateAsync(client, servings: 2, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        Assert.NotNull(insights!.Nutrition.KcalPerServing);
        Assert.InRange(insights.Nutrition.KcalPerServing!.Value, 250, 500);
        Assert.Equal(1, insights.Nutrition.CoveredLines);
        Assert.Equal(1, insights.Nutrition.TotalLines);
    }

    [Fact]
    public async Task Coverage_reports_the_lines_it_could_not_use()
    {
        // The number that keeps the figure honest. A total computed from 1 of 3
        // ingredients is nearly meaningless, and a client rendering it without this
        // would be making a confident claim the data does not support.
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = await CreateAsync(client, servings: 1, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 100m, Unit = UnitOfMeasure.Gram },
            // Unresolvable — no catalogue entry, so no nutrition.
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 50m, Unit = UnitOfMeasure.Gram },
            // Resolvable but imprecise — a pinch has no gram weight.
            new RecipeIngredient { Name = "salt", Quantity = 1m, Unit = UnitOfMeasure.Pinch },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        Assert.Equal(1, insights!.Nutrition.CoveredLines);
        Assert.Equal(3, insights.Nutrition.TotalLines);
    }

    [Fact]
    public async Task A_recipe_with_nothing_resolvable_reports_null_rather_than_zero()
    {
        // Zero would read as "this recipe has no calories". Null reads as "we do not
        // know", which is true. The distinction is the difference between a useless
        // figure and a misleading one.
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = await CreateAsync(client, servings: 2, ingredients: [
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 100m, Unit = UnitOfMeasure.Gram },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        Assert.Null(insights!.Nutrition.KcalPerServing);
        Assert.Equal(0, insights.Nutrition.CoveredLines);
    }

    [Fact]
    public async Task Volume_contributes_when_the_ingredient_has_a_density()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var withDensity = await CreateAsync(client, servings: 1, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 2m, Unit = UnitOfMeasure.Cup },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{withDensity.Id}/insights", TestJson.Options);

        Assert.Equal(1, insights!.Nutrition.CoveredLines);
        Assert.NotNull(insights.Nutrition.KcalPerServing);
    }

    // ── dietary checks ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_system_verifies_the_callers_own_restrictions()
    {
        // The sentence the horizon document says this buys: the model is told the
        // restriction, and the system can check it.
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await SetRestrictionsAsync(client, auth.Username, [DietaryRestriction.Vegan]);

        var recipe = await CreateAsync(client, servings: 2, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "cheddar", Quantity = 100m, Unit = UnitOfMeasure.Gram },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        var check = Assert.Single(insights!.DietaryChecks);
        Assert.Equal(DietaryRestriction.Vegan, check.Restriction);
        var conflict = Assert.Single(check.Conflicts);
        Assert.Contains("cheese", conflict.IngredientName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unresolved_lines_are_reported_as_uncheckable_rather_than_ignored()
    {
        // "No conflicts" alongside "2 lines could not be checked" is an honest answer.
        // "No conflicts" alone would be a safety claim this cannot support — D8
        // guarantees unresolved ingredients will always exist.
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await SetRestrictionsAsync(client, auth.Username, [DietaryRestriction.Vegan]);

        var recipe = await CreateAsync(client, servings: 2, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "zzzz mystery paste", Quantity = 1m, Unit = UnitOfMeasure.Tablespoon },
            new RecipeIngredient { Name = "zzzz other thing", Quantity = 1m, Unit = UnitOfMeasure.Tablespoon },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        var check = Assert.Single(insights!.DietaryChecks);
        Assert.Empty(check.Conflicts);
        Assert.Equal(2, check.UncheckableLines);
    }

    [Fact]
    public async Task A_caller_with_no_restrictions_gets_no_checks()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var recipe = await CreateAsync(client, servings: 2, ingredients: [
            new RecipeIngredient { Name = "cheddar", Quantity = 100m, Unit = UnitOfMeasure.Gram },
        ]);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        Assert.Empty(insights!.DietaryChecks);
    }

    [Fact]
    public async Task Insights_are_anonymous_capable_but_carry_no_checks_for_a_guest()
    {
        var owner = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(owner);
        var recipe = await CreateAsync(owner, servings: 2, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ]);

        var guest = factory.CreateClient();
        var insights = await guest.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);

        Assert.NotNull(insights!.Nutrition.KcalPerServing);
        Assert.Empty(insights.DietaryChecks);
    }

    [Fact]
    public async Task A_private_recipes_insights_are_404_for_everyone_else()
    {
        // 404-never-403: the existence of insights must not confirm the existence of
        // a private recipe, exactly as GET /recipes/{id} behaves.
        var owner = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(owner);
        var recipe = await CreateAsync(owner, servings: 2, visibility: RecipeVisibility.Private, ingredients: [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ]);

        var stranger = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(stranger);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/recipes/{recipe.Id}/insights")).StatusCode);
        // ...and the owner still sees them.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/recipes/{recipe.Id}/insights")).StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private static async Task SetRestrictionsAsync(
        HttpClient client, string username, List<DietaryRestriction> restrictions)
    {
        var response = await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            username, null, null, RecipeVisibility.Public, restrictions), TestJson.Options);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<RecipeResponse> CreateAsync(
        HttpClient client,
        int servings,
        List<RecipeIngredient> ingredients,
        RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: $"Insights probe {Guid.NewGuid():N}",
            Description: "Seeded to exercise computed nutrition and dietary checks.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 5,
            Servings: servings,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: null,
            CaloriesPerServing: null,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: ingredients,
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            Tags: []);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
    }
}
