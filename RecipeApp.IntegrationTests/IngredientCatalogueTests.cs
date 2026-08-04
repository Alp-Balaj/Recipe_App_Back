using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// The catalogue and the write-path resolver (stream G, slice G2 — D8/D9).
///
/// What these pin is D8's shape as much as the mechanics: resolution is exact, a miss is
/// legal and permanent, and nothing about it can cost a user their recipe.
/// </summary>
public class IngredientCatalogueTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // ── The catalogue ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalogue_is_seeded_and_readable_without_a_token()
    {
        // Anonymous on purpose: a catalogue row belongs to nobody, so unlike every other
        // list in this codebase there is no visibility rule to apply.
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<IngredientListResponse>("/ingredients", TestJson.Options);

        Assert.NotNull(response);
        // The plan asks for "roughly 500-1500 generic ingredients".
        Assert.InRange(response.Total, 500, 1500);
        Assert.NotEmpty(response.Items);
    }

    [Fact]
    public async Task Search_matches_on_display_name()
    {
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<IngredientListResponse>(
            "/ingredients?q=flour", TestJson.Options);

        Assert.NotEmpty(response!.Items);
        Assert.All(response.Items, i => Assert.Contains("flour", i.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_also_matches_an_alias_the_name_does_not_contain()
    {
        // The half of the search a substring over names could never do, and the reason
        // the picker agrees with the resolver: "prawns" shares no letters with "shrimp",
        // and it is the curated alias that connects them.
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<IngredientListResponse>(
            "/ingredients?q=prawns", TestJson.Options);

        Assert.NotEmpty(response!.Items);
        Assert.Contains(response.Items, i => i.Name.Contains("shrimp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_treats_wildcards_as_literals()
    {
        // % and _ reach an ILIKE here, unlike the recipe search's tsquery path where the
        // parser handles them for us. Escaped, they match the CHARACTER rather than
        // acting as wildcards — and USDA's names contain real percent signs ("3.25%
        // milkfat milk"), which is what makes the distinction observable at all.
        var client = factory.CreateClient();

        var percent = await client.GetFromJsonAsync<IngredientListResponse>(
            "/ingredients?q=%25", TestJson.Options);

        // Not everything (unescaped, "%" would match the whole catalogue)...
        Assert.True(percent!.Items.Count < percent.Total,
            "an unescaped % matched every row — the escape is not being applied");
        // ...and every hit genuinely contains the character.
        Assert.All(percent.Items, i => Assert.Contains('%', i.Name));

        // The underscore has no literal use in the catalogue, so it matches nothing.
        var underscore = await client.GetFromJsonAsync<IngredientListResponse>(
            "/ingredients?q=_", TestJson.Options);
        Assert.Empty(underscore!.Items);
    }

    [Fact]
    public async Task Densities_and_nutrition_are_present_on_a_useful_share_of_the_catalogue()
    {
        // The catalogue's whole downstream value: nutrition is G4's input and the
        // density is what lets G3 add 2 cups of flour to 300 g of it. A regression in
        // the ingest that silently produced nulls would pass every other test here.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var total = await db.Ingredients.CountAsync();
        var withKcal = await db.Ingredients.CountAsync(i => i.Kcal != null);
        var withDensity = await db.Ingredients.CountAsync(i => i.GramsPerMillilitre != null);

        Assert.Equal(total, withKcal);
        Assert.True(withDensity > total / 4, $"only {withDensity} of {total} have a density");
    }

    [Fact]
    public async Task Every_alias_points_at_an_ingredient_that_exists()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var orphans = await db.IngredientAliases
            .CountAsync(a => !db.Ingredients.Any(i => i.Id == a.IngredientId));

        Assert.Equal(0, orphans);
    }

    // ── The resolver ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_known_ingredient_name_resolves_on_write()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateAsync(client, [
            new RecipeIngredient { Name = "flour", Quantity = 500m, Unit = UnitOfMeasure.Gram },
        ]);

        var stored = await ReadIngredientsAsync(created.Id);
        Assert.NotNull(Assert.Single(stored).IngredientId);
    }

    [Fact]
    public async Task An_unknown_ingredient_still_saves_with_a_null_id()
    {
        // D8, the whole decision in one test: "resolve, don't constrain". A miss must
        // not be an error, must not drop the line, and must not rewrite the name.
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateAsync(client, [
            new RecipeIngredient { Name = "gochujang", Quantity = 2m, Unit = UnitOfMeasure.Tablespoon },
        ]);

        var stored = Assert.Single(await ReadIngredientsAsync(created.Id));
        Assert.Null(stored.IngredientId);
        Assert.Equal("gochujang", stored.Name);
    }

    [Fact]
    public async Task Resolution_never_rewrites_the_name_it_matched()
    {
        // The author's spelling is what the recipe shows and what SearchVector indexes.
        // Resolution adds an id beside it and nothing more.
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateAsync(client, [
            new RecipeIngredient { Name = "Finely chopped Onions", Quantity = 2m, Unit = UnitOfMeasure.Piece },
        ]);

        var stored = Assert.Single(await ReadIngredientsAsync(created.Id));
        Assert.Equal("Finely chopped Onions", stored.Name);
    }

    [Fact]
    public async Task Editing_a_recipe_re_resolves_in_both_directions()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // Starts unresolvable.
        var created = await CreateAsync(client, [
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 1m, Unit = UnitOfMeasure.Gram },
        ]);
        Assert.Null(Assert.Single(await ReadIngredientsAsync(created.Id)).IngredientId);

        // Corrected to something known — gains an id.
        await UpdateIngredientsAsync(client, created, [
            new RecipeIngredient { Name = "flour", Quantity = 1m, Unit = UnitOfMeasure.Gram },
        ]);
        var resolvedId = Assert.Single(await ReadIngredientsAsync(created.Id)).IngredientId;
        Assert.NotNull(resolvedId);

        // Changed back to something unknown — must LOSE it. A stale id would claim a
        // catalogue entry that no longer describes the line, and G4 would compute
        // nutrition from the wrong food.
        await UpdateIngredientsAsync(client, created, [
            new RecipeIngredient { Name = "zzzz unobtainium", Quantity = 1m, Unit = UnitOfMeasure.Gram },
        ]);
        Assert.Null(Assert.Single(await ReadIngredientsAsync(created.Id)).IngredientId);
    }

    [Fact]
    public async Task Two_spellings_of_one_ingredient_resolve_to_the_same_id()
    {
        // What the catalogue buys the shopping list: IngredientKey already collapsed
        // case and plurals, but "prawns"/"shrimp" needed the alias table.
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var a = await CreateAsync(client, [
            new RecipeIngredient { Name = "Shrimp", Quantity = 200m, Unit = UnitOfMeasure.Gram },
        ]);
        var b = await CreateAsync(client, [
            new RecipeIngredient { Name = "prawns", Quantity = 300m, Unit = UnitOfMeasure.Gram },
        ]);

        var first = Assert.Single(await ReadIngredientsAsync(a.Id)).IngredientId;
        var second = Assert.Single(await ReadIngredientsAsync(b.Id)).IngredientId;

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task The_resolver_agrees_with_the_alias_table_exactly()
    {
        // Exact-match only, per D8 — no edit distance, no trigram, no soundex. The
        // negative half is the point: "flou" is one character from "flour" and must NOT
        // resolve, because the same tolerance that fixed it would merge lime into lemon
        // (see IngredientKeyTests).
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateAsync(client, [
            new RecipeIngredient { Name = "flou", Quantity = 1m, Unit = UnitOfMeasure.Gram },
            new RecipeIngredient { Name = "flours", Quantity = 1m, Unit = UnitOfMeasure.Gram },
        ]);

        var stored = await ReadIngredientsAsync(created.Id);
        Assert.Null(stored.Single(i => i.Name == "flou").IngredientId);
        // "flours" DOES resolve, and not by fuzziness: IngredientKey singularises it to
        // the same key "flour" before the lookup ever happens.
        Assert.NotNull(stored.Single(i => i.Name == "flours").IngredientId);
    }

    [Fact]
    public async Task A_generated_recipe_resolves_on_the_same_terms()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/recipes/generate", new { prompt = "something with flour" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var generated = await response.Content.ReadFromJsonAsync<GenerateRecipeResponse>(TestJson.Options);

        // The fake assistant returns "generated ingredient", which no catalogue knows —
        // so this asserts the null case, which is the honest outcome for an invented
        // name and the same one a human would get.
        var stored = Assert.Single(await ReadIngredientsAsync(generated!.Recipe.Id));
        Assert.Null(stored.IngredientId);
    }

    [Fact]
    public void The_key_the_resolver_uses_is_the_key_the_seed_was_built_with()
    {
        // Both ends call IngredientKey.For — the seed builder is a C# project precisely
        // so it can. This is the assertion that would fail if anyone reimplemented the
        // normalisation on either side.
        Assert.Equal("flour", IngredientKey.For("Flours"));
        Assert.Equal("flour", IngredientKey.For("  FLOUR  "));
        Assert.Equal("onion", IngredientKey.For("Finely chopped Onions").Split(' ').Last());
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private async Task<List<RecipeIngredient>> ReadIngredientsAsync(Guid recipeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recipe = await db.Recipes.SingleAsync(r => r.Id == recipeId);
        return recipe.Ingredients;
    }

    private static async Task<RecipeResponse> CreateAsync(HttpClient client, List<RecipeIngredient> ingredients)
    {
        var request = new CreateRecipeRequest(
            Title: $"Resolver probe {Guid.NewGuid():N}",
            Description: "Seeded to exercise the ingredient resolver.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 5,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: null,
            CaloriesPerServing: null,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: ingredients,
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            Tags: []);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
    }

    private static async Task UpdateIngredientsAsync(
        HttpClient client, RecipeResponse recipe, List<RecipeIngredient> ingredients)
    {
        var request = new UpdateRecipeRequest(
            recipe.Title, recipe.Description, recipe.PrepTimeMinutes, recipe.CookTimeMinutes,
            recipe.Servings, recipe.Difficulty, recipe.CuisineType, recipe.CaloriesPerServing,
            recipe.ImageUrl, recipe.Visibility, ingredients,
            [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            []);

        var response = await client.PutAsJsonAsync($"/recipes/{recipe.Id}", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
    }
}
