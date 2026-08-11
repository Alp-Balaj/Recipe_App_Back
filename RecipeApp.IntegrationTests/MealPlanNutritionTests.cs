using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// GET /meal-plans/{id}/nutrition (stream I, D12's second surface).
///
/// The properties worth defending here are the ones that make a computed number
/// safe to show next to an author-typed one: a day is one serving per planned
/// meal (so the two figures answer the same question), coverage travels with
/// every total, and a thinly-covered day says so instead of quietly rendering.
/// </summary>
public class MealPlanNutritionTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task A_day_sums_one_serving_of_each_planned_meal()
    {
        // The rule that lets the ribbon sit beside the author-typed calorie strip:
        // a recipe serving four contributes ONE serving to the day, because you eat
        // a portion, not a pot. Both dishes are 400 g of flour over 2 servings, so a
        // serving is 200 g ≈ 728 kcal and the day is ~1,456 — NOT ~2,912, which is
        // what counting each recipe whole would give.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());

        var breakfast = await FlourRecipeAsync(client, servings: 2, grams: 400m);
        var dinner = await FlourRecipeAsync(client, servings: 2, grams: 400m);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, breakfast.Id);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, dinner.Id);

        var nutrition = await GetAsync(client, plan.Id);

        var monday = Assert.Single(nutrition.Days);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.Equal(2, monday.EntryCount);
        Assert.NotNull(monday.Kcal);
        Assert.InRange(monday.Kcal!.Value, 1200, 1700);
        Assert.NotNull(monday.ProteinG);
    }

    [Fact]
    public async Task Every_planned_day_comes_back_from_one_read()
    {
        // The whole reason this endpoint exists. Asking /recipes/{id}/insights per
        // entry would be up to 21 requests for a full week — the N-per-view mistake
        // the month view already refused twice.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var recipe = await FlourRecipeAsync(client, servings: 2, grams: 200m);

        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Saturday })
        {
            await MealPlanTestHelper.AddEntryAsync(client, plan.Id, day, MealType.Dinner, recipe.Id);
        }

        var nutrition = await GetAsync(client, plan.Id);

        Assert.Equal(3, nutrition.Days.Count);
        Assert.Equal(plan.Id, nutrition.MealPlanId);
        Assert.All(nutrition.Days, day => Assert.NotNull(day.Kcal));
    }

    [Fact]
    public async Task A_day_with_nothing_planned_is_omitted_rather_than_returned_as_zero()
    {
        // A day nobody planned has no question to answer, and a row of 0 kcal would
        // answer it wrongly — a client would happily chart it as a fasting day.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var recipe = await FlourRecipeAsync(client, servings: 2, grams: 200m);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Tuesday, MealType.Lunch, recipe.Id);

        var nutrition = await GetAsync(client, plan.Id);

        Assert.Equal(DayOfWeek.Tuesday, Assert.Single(nutrition.Days).DayOfWeek);
    }

    [Fact]
    public async Task An_empty_plan_reports_no_days_at_all()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());

        var nutrition = await GetAsync(client, plan.Id);

        Assert.Empty(nutrition.Days);
    }

    [Fact]
    public async Task The_same_dish_twice_in_a_day_is_counted_twice()
    {
        // The opposite of the grocery insight's rule, and deliberately so: an outlier
        // is a property of a dish, but eating the same dish at lunch and dinner really
        // is twice the calories.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var recipe = await FlourRecipeAsync(client, servings: 2, grams: 400m);

        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Lunch, recipe.Id);
        var onceOnly = (await GetAsync(client, plan.Id)).Days.Single().Kcal;

        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);
        var twice = (await GetAsync(client, plan.Id)).Days.Single();

        Assert.NotNull(onceOnly);
        Assert.NotNull(twice.Kcal);
        Assert.Equal(onceOnly!.Value * 2, twice.Kcal!.Value);
        Assert.Equal(2, twice.EntryCount);
        // Coverage counts per entry too, for the same reason.
        Assert.Equal(2, twice.CoveredLines);
        Assert.Equal(2, twice.TotalLines);
    }

    // ── coverage, and D12's floor ─────────────────────────────────────────────────

    [Fact]
    public async Task Coverage_is_summed_across_the_days_meals()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());

        var covered = await RecipeAsync(client, servings: 2, [
            new RecipeIngredient { Name = "flour", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ]);
        var partly = await RecipeAsync(client, servings: 2, [
            new RecipeIngredient { Name = "flour", Quantity = 100m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 50m, Unit = UnitOfMeasure.Gram },
        ]);

        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, covered.Id);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, partly.Id);

        var monday = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.Equal(2, monday.CoveredLines);
        Assert.Equal(3, monday.TotalLines);
    }

    [Fact]
    public async Task A_thinly_covered_day_is_flagged_rather_than_rendered_as_a_number()
    {
        // D12: below the floor a day displays as incomplete, because an undercounted
        // calorie figure is worse than none. The figures still come back — the flag is
        // what tells a client not to trust them, and the coverage is what explains it.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());

        var mostlyUnknown = await RecipeAsync(client, servings: 2, [
            new RecipeIngredient { Name = "flour", Quantity = 100m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 50m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "zzzz mystery paste", Quantity = 50m, Unit = UnitOfMeasure.Gram },
        ]);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, mostlyUnknown.Id);

        var monday = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.Equal(1, monday.CoveredLines);
        Assert.Equal(3, monday.TotalLines);
        Assert.False(monday.IsSufficientlyCovered);
    }

    [Fact]
    public async Task A_fully_covered_day_is_worth_reading()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var recipe = await FlourRecipeAsync(client, servings: 2, grams: 200m);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        var monday = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.True(monday.IsSufficientlyCovered);
        Assert.Equal(monday.CoveredLines, monday.TotalLines);
    }

    [Fact]
    public async Task A_day_nothing_resolved_in_reports_null_rather_than_zero()
    {
        // Zero would read as "you planned a day with no calories in it". Null reads as
        // "we could not tell", which is true.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());

        var unknowable = await RecipeAsync(client, servings: 2, [
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 100m, Unit = UnitOfMeasure.Gram },
        ]);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, unknowable.Id);

        var monday = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.Null(monday.Kcal);
        Assert.Equal(0, monday.CoveredLines);
        Assert.False(monday.IsSufficientlyCovered);
        // The meal is still counted as planned — the day has a dish, just not a figure.
        Assert.Equal(1, monday.EntryCount);
    }

    [Fact]
    public async Task The_computed_figure_agrees_with_the_recipes_own_insights()
    {
        // The extraction's whole point. A one-meal day must equal that recipe's
        // per-serving figure from /recipes/{id}/insights — two surfaces, one
        // computation, so they cannot drift apart.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var recipe = await FlourRecipeAsync(client, servings: 3, grams: 500m);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        var insights = await client.GetFromJsonAsync<RecipeInsightsResponse>(
            $"/recipes/{recipe.Id}/insights", TestJson.Options);
        var monday = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.Equal(insights!.Nutrition.KcalPerServing, monday.Kcal);
        Assert.Equal(insights.Nutrition.CoveredLines, monday.CoveredLines);
        Assert.Equal(insights.Nutrition.TotalLines, monday.TotalLines);
    }

    // ── scoping ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_users_plan_is_404_never_403()
    {
        var owner = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(owner, MealPlanTestHelper.NextMonday());

        var stranger = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/meal-plans/{plan.Id}/nutrition")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/meal-plans/{plan.Id}/nutrition")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_plan_is_404()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/meal-plans/{Guid.NewGuid()}/nutrition")).StatusCode);
    }

    [Fact]
    public async Task An_entry_whose_recipe_was_deleted_drops_out()
    {
        // Same rule GET /meal-plans/{id} follows, and it has to be the same one: the
        // ribbon must never total a meal the page below it cannot render.
        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());

        var kept = await FlourRecipeAsync(client, servings: 2, grams: 200m);
        var doomed = await FlourRecipeAsync(client, servings: 2, grams: 200m);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, kept.Id);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, doomed.Id);

        (await client.DeleteAsync($"/recipes/{doomed.Id}")).EnsureSuccessStatusCode();

        var monday = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.Equal(1, monday.EntryCount);
        Assert.Equal(1, monday.TotalLines);
    }

    // KAN-1. The ribbon is computed from the author's ingredient LINES, so a withdrawn recipe
    // that keeps contributing is the same leak as the week view's, arrived at by arithmetic: a
    // reader who diffs the day total before and after learns the recipe's per-serving energy
    // to the kcal. It has to drop out for the same reason a deleted one does, and by the same
    // mechanism, or the two causes become distinguishable.
    [Fact]
    public async Task An_entry_whose_recipe_was_withdrawn_drops_out_too()
    {
        var authorClient = await factory.CreateAuthenticatedClientAsync();
        var withdrawn = await FlourRecipeAsync(authorClient, servings: 2, grams: 200m);

        var client = await factory.CreateAuthenticatedClientAsync();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, MealPlanTestHelper.NextMonday());
        var kept = await FlourRecipeAsync(client, servings: 2, grams: 200m);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, kept.Id);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, withdrawn.Id);

        var before = Assert.Single((await GetAsync(client, plan.Id)).Days);
        Assert.Equal(2, before.EntryCount);

        await MealPlanTestHelper.SetVisibilityAsync(authorClient, withdrawn, RecipeVisibility.Private);

        var after = Assert.Single((await GetAsync(client, plan.Id)).Days);

        Assert.Equal(1, after.EntryCount);
        Assert.Equal(1, after.TotalLines);
        // The energy figure moves too — the assertion that would fail if only the counters
        // were gated and the totals kept summing behind them.
        Assert.NotEqual(before.Kcal, after.Kcal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task<MealPlanNutritionResponse> GetAsync(HttpClient client, Guid planId) =>
        (await client.GetFromJsonAsync<MealPlanNutritionResponse>(
            $"/meal-plans/{planId}/nutrition", TestJson.Options))!;

    private static Task<RecipeResponse> FlourRecipeAsync(HttpClient client, int servings, decimal grams) =>
        RecipeAsync(client, servings, [
            new RecipeIngredient { Name = "flour", Quantity = grams, Unit = UnitOfMeasure.Gram },
        ]);

    /// <summary>
    /// Local rather than MealPlanTestHelper.CreateRecipeAsync because servings is the
    /// variable under test here — that helper fixes it at 4.
    /// </summary>
    private static async Task<RecipeResponse> RecipeAsync(
        HttpClient client, int servings, List<RecipeIngredient> ingredients)
    {
        var request = new CreateRecipeRequest(
            Title: $"Plan nutrition probe {Guid.NewGuid():N}",
            Description: "Seeded to exercise the plan's computed nutrition read.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 5,
            Servings: servings,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: null,
            // Deliberately present: the computed figure sits BESIDE the author-typed one
            // and must never be confused with it.
            CaloriesPerServing: 210,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: ingredients,
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            Tags: []);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
