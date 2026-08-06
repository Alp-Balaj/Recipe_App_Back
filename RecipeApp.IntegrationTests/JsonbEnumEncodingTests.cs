using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Stream G, sharp edge 1 — the one that bites quietly.
///
/// Enums inside jsonb must persist as their member NAME. Every other test in this suite
/// would pass just as happily if they persisted as integers, because the same converter
/// writes and reads them: a round-trip is symmetric and proves nothing about the encoding.
/// What an integer encoding would break is the NEXT change — inserting a member into the
/// middle of UnitOfMeasure would silently reinterpret every stored recipe, with no
/// migration, no error, and no way afterwards to tell which reading was meant.
///
/// So these assert on the RAW jsonb text, read straight out of Postgres, bypassing the
/// deserializer entirely. That is the only way to catch a RecipeAppDataSource that lost its
/// converter, and it is why this file exists separately from the projection tests.
/// </summary>
public class JsonbEnumEncodingTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Ingredient_units_and_tags_are_stored_as_names_not_ordinals()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateAsync(client);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // ::text on the jsonb column, so what comes back is the stored document verbatim
        // rather than anything the CLR mapping had a hand in.
        var ingredientsJson = await ReadScalarAsync(
            db, $"""SELECT "Ingredients"::text FROM "Recipes" WHERE "Id" = '{created.Id}'""");
        var tagsJson = await ReadScalarAsync(
            db, $"""SELECT "Tags"::text FROM "Recipes" WHERE "Id" = '{created.Id}'""");

        Assert.Contains("\"Cup\"", ingredientsJson);
        Assert.Contains("\"Kilogram\"", ingredientsJson);
        Assert.Contains("\"OnePot\"", tagsJson);
        Assert.Contains("\"Vegan\"", tagsJson);

        // The negative half, and the one that actually fails if the converter goes missing:
        // Cup is ordinal 8 and Kilogram is 1, so an integer encoding would render the unit
        // as a bare number.
        Assert.DoesNotContain("\"Unit\": 8", ingredientsJson);
        Assert.DoesNotContain("\"Unit\":8", ingredientsJson);
        Assert.DoesNotContain("\"Unit\": 1", ingredientsJson);
        Assert.DoesNotContain("\"Unit\":1", ingredientsJson);
    }

    [Fact]
    public async Task Dietary_restrictions_are_stored_as_names_not_ordinals()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var update = new UpdateProfileRequest(
            Username: auth.Username,
            Bio: null,
            ProfileImageUrl: null,
            DefaultRecipeVisibility: RecipeVisibility.Public,
            DietaryRestrictions: [DietaryRestriction.Vegan, DietaryRestriction.GlutenFree]);

        var response = await client.PutAsJsonAsync("/users/me", update, TestJson.Options);
        response.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var json = await ReadScalarAsync(
            db, $"""SELECT "DietaryRestrictions"::text FROM "Users" WHERE "Id" = '{auth.UserId}'""");

        Assert.Contains("\"Vegan\"", json);
        Assert.Contains("\"GlutenFree\"", json);
        Assert.DoesNotContain("1", json);
    }

    // Onboarding (stream K), and the reason this file was worth extending rather than
    // trusting the round-trip in OnboardingEndpointTests. CuisinePreferences is a primitive
    // collection like DietaryRestrictions above, so it needs EF's OWN element conversion —
    // the RecipeAppDataSource converter does not reach it. Miss that and the column lands as
    // [19, 11] with a symmetric read that hides the problem completely, until Cuisine gains a
    // member and every stored preference silently shifts one place.
    //
    // Thai is ordinal 19 and Korean 11, so the negative assertions below are what actually
    // fail if the conversion is dropped.
    [Fact]
    public async Task Cuisine_preferences_are_stored_as_names_not_ordinals()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/users/me/onboarding",
            new CompleteOnboardingRequest(CuisinePreferences: [Cuisine.Thai, Cuisine.Korean]),
            TestJson.Options);
        response.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var json = await ReadScalarAsync(
            db, $"""SELECT "CuisinePreferences"::text FROM "Users" WHERE "Id" = '{auth.UserId}'""");

        Assert.Contains("\"Thai\"", json);
        Assert.Contains("\"Korean\"", json);
        Assert.DoesNotContain("19", json);
        Assert.DoesNotContain("11", json);
    }

    // The jsonpath the SearchVector generated column depends on is '$[*].Name' — keeping
    // RecipeIngredient.Name a string is what leaves that column and its GIN index untouched
    // (sharp edge 2). This is the assertion that the typing pass did not disturb it.
    [Fact]
    public async Task Ingredient_names_still_reach_the_search_vector()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateAsync(client, ingredientName: "kohlrabi");

        var found = await client.GetFromJsonAsync<RecipeListResponse>("/recipes?search=kohlrabi", TestJson.Options);

        Assert.Contains(found!.Items, r => r.Id == created.Id);
    }

    private static async Task<string> ReadScalarAsync(ApplicationDbContext db, string sql)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await db.Database.OpenConnectionAsync();
        return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }

    private static async Task<RecipeResponse> CreateAsync(HttpClient client, string ingredientName = "flour")
    {
        var request = new CreateRecipeRequest(
            Title: "Jsonb Encoding Probe",
            Description: "Seeded to inspect how enums land inside the jsonb columns.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 5,
            Servings: 2,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: null,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients:
            [
                new RecipeIngredient { Name = ingredientName, Quantity = 2m, Unit = UnitOfMeasure.Cup },
                new RecipeIngredient { Name = "potato", Quantity = 1m, Unit = UnitOfMeasure.Kilogram },
            ],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            Tags: [RecipeTag.OnePot, RecipeTag.Vegan]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
    }
}
