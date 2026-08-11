using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// meal-planning-ui plan, Task 1: GET /meal-plans (keyset list + exact ?weekStart= filter).
// Mirrors ShoppingListEndpointsTests' style — fresh user per test, shared Testcontainers DB.
public class MealPlanListEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private static DateTime UtcMidnight(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task List_ReturnsCallersPlans_NewestWeekFirst()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        foreach (var day in new[] { 6, 20, 13 })
        {
            var created = await client.PostAsJsonAsync("/meal-plans",
                new CreateMealPlanRequest(UtcMidnight(2026, 7, day)), TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var response = await client.GetAsync("/meal-plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(3, body!.Items.Count);
        Assert.Equal(UtcMidnight(2026, 7, 20), body.Items[0].WeekStartDate);
        Assert.Equal(UtcMidnight(2026, 7, 13), body.Items[1].WeekStartDate);
        Assert.Equal(UtcMidnight(2026, 7, 6), body.Items[2].WeekStartDate);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task List_OmitsOtherUsersPlans()
    {
        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await otherClient.PostAsJsonAsync("/meal-plans",
            new CreateMealPlanRequest(UtcMidnight(2026, 7, 20)), TestJson.Options);

        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var body = await (await client.GetAsync("/meal-plans"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task List_WeekStartFilter_ReturnsOnlyThatWeek()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(UtcMidnight(2026, 7, 13)), TestJson.Options);
        await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(UtcMidnight(2026, 7, 20)), TestJson.Options);

        var body = await (await client.GetAsync("/meal-plans?weekStart=2026-07-20T00:00:00Z"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal(UtcMidnight(2026, 7, 20), item.WeekStartDate);
    }

    [Fact]
    public async Task List_WeekStartFilter_NoMatch_ReturnsEmpty()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var body = await (await client.GetAsync("/meal-plans?weekStart=2026-01-05T00:00:00Z"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task List_WeekStartNotUtcMidnight_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?weekStart=2026-07-20T03:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Week/shopping rework fix wave, F3: this route hand-rolled the UTC/midnight half of the
    // week rule and omitted the Monday half, so a Wednesday MIDNIGHT value 200'd with an empty
    // list here while GET /shopping-list 400'd on the exact same value. 2026-07-22 is a
    // Wednesday and a legitimate UTC midnight — the only thing wrong with it is the day of
    // week, which is precisely what the old check could not see.
    [Fact]
    public async Task List_WeekStartUtcMidnightButNotMonday_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?weekStart=2026-07-22T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_EntryCount_CountsEntries()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipeId = await CreateRecipeAsync(client);

        var plan = await (await client.PostAsJsonAsync("/meal-plans",
            new CreateMealPlanRequest(UtcMidnight(2026, 7, 20)), TestJson.Options))
            .Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);

        await client.PostAsJsonAsync($"/meal-plans/{plan!.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Monday, MealType.Breakfast, recipeId), TestJson.Options);

        var body = await (await client.GetAsync("/meal-plans"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.Equal(1, body!.Items[0].EntryCount);
    }

    // --- meal-plan redesign: TotalMinutes on the summary ---------------------------------
    // The month view's week rail shows cook load per week; totalling it client-side needed a
    // GET /recipes/{id} per entry, so the surface shipped without it.

    [Fact]
    public async Task List_TotalMinutes_SumsPrepPlusCookAcrossEntries()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var quick = await CreateRecipeAsync(client, prepMinutes: 5, cookMinutes: 10);   // 15
        var slow = await CreateRecipeAsync(client, prepMinutes: 20, cookMinutes: 45);   // 65
        var plan = await CreatePlanAsync(client, UtcMidnight(2026, 8, 3));

        await AddEntryAsync(client, plan, DayOfWeek.Monday, MealType.Breakfast, quick);
        await AddEntryAsync(client, plan, DayOfWeek.Tuesday, MealType.Dinner, slow);

        var summary = await GetSummaryAsync(client, UtcMidnight(2026, 8, 3));

        Assert.Equal(2, summary.EntryCount);
        Assert.Equal(80, summary.TotalMinutes);
    }

    // Per ENTRY, not per distinct recipe — cooking the same dish twice costs the time twice.
    // Same reasoning that ended the shopping-list dedupe.
    [Fact]
    public async Task List_TotalMinutes_CountsARepeatedRecipeOncePerEntry()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await CreateRecipeAsync(client, prepMinutes: 15, cookMinutes: 25);  // 40
        var plan = await CreatePlanAsync(client, UtcMidnight(2026, 8, 10));

        await AddEntryAsync(client, plan, DayOfWeek.Monday, MealType.Dinner, recipe);
        await AddEntryAsync(client, plan, DayOfWeek.Thursday, MealType.Dinner, recipe);

        var summary = await GetSummaryAsync(client, UtcMidnight(2026, 8, 10));

        Assert.Equal(2, summary.EntryCount);
        Assert.Equal(80, summary.TotalMinutes);
    }

    [Fact]
    public async Task List_PlanWithNoEntries_ReportsZeroCountAndZeroMinutes()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await CreatePlanAsync(client, UtcMidnight(2026, 8, 17));

        var summary = await GetSummaryAsync(client, UtcMidnight(2026, 8, 17));

        Assert.Equal(0, summary.EntryCount);
        Assert.Equal(0, summary.TotalMinutes);
    }

    // The two counters answer DIFFERENT questions and KAN-1 split them apart to say so. Both
    // used to run over one join against Recipes, so an unavailable recipe took the meal off the
    // count as well as its minutes off the clock. Now:
    //
    //   EntryCount   — how many meals the week HOLDS. Unchanged by availability: the entry is
    //                  the caller's own record and GET /meal-plans/{id} still renders its slot,
    //                  so dropping it here would have the list say "1 meal" about a week
    //                  showing two.
    //   TotalMinutes — how long the week's cooking TAKES. Availability decides it, because
    //                  prep and cook time are the author's content; a withdrawn recipe's time
    //                  showing up in the sum is the leak arriving as arithmetic.
    [Fact]
    public async Task List_SoftDeletedRecipe_KeepsItsEntryCountAndLosesItsMinutes()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var kept = await CreateRecipeAsync(client, prepMinutes: 5, cookMinutes: 5);      // 10
        var doomed = await CreateRecipeAsync(client, prepMinutes: 30, cookMinutes: 30);  // 60
        var plan = await CreatePlanAsync(client, UtcMidnight(2026, 8, 24));

        await AddEntryAsync(client, plan, DayOfWeek.Monday, MealType.Lunch, kept);
        await AddEntryAsync(client, plan, DayOfWeek.Tuesday, MealType.Lunch, doomed);

        var before = await GetSummaryAsync(client, UtcMidnight(2026, 8, 24));
        Assert.Equal(2, before.EntryCount);
        Assert.Equal(70, before.TotalMinutes);

        (await client.DeleteAsync($"/recipes/{doomed}")).EnsureSuccessStatusCode();

        var after = await GetSummaryAsync(client, UtcMidnight(2026, 8, 24));
        Assert.Equal(2, after.EntryCount);
        Assert.Equal(10, after.TotalMinutes);

        // The week view agrees — the consistency the split is FOR. Two slots, one of them
        // unavailable, which is exactly what "2 meals · 10 min" describes.
        var week = await client.GetFromJsonAsync<MealPlanResponse>($"/meal-plans/{plan}", TestJson.Options);
        Assert.Equal(2, week!.Entries.Count);
        Assert.Single(week.Entries, e => e.Recipe is null);
    }

    // The same, for the cause the summary had no notion of at all. 60 minutes of a stranger's
    // withdrawn recipe stayed in this sum indefinitely.
    [Fact]
    public async Task List_WithdrawnRecipe_KeepsItsEntryCountAndLosesItsMinutes()
    {
        var authorClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);
        var withdrawn = await CreateRecipeAsync(authorClient, prepMinutes: 30, cookMinutes: 30); // 60

        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var kept = await CreateRecipeAsync(client, prepMinutes: 5, cookMinutes: 5);              // 10
        var plan = await CreatePlanAsync(client, UtcMidnight(2026, 8, 31));

        await AddEntryAsync(client, plan, DayOfWeek.Monday, MealType.Lunch, kept);
        await AddEntryAsync(client, plan, DayOfWeek.Tuesday, MealType.Lunch, withdrawn);

        var before = await GetSummaryAsync(client, UtcMidnight(2026, 8, 31));
        Assert.Equal(2, before.EntryCount);
        Assert.Equal(70, before.TotalMinutes);

        await MealPlanTestHelper.SetVisibilityAsync(authorClient, withdrawn, RecipeVisibility.Private);

        var after = await GetSummaryAsync(client, UtcMidnight(2026, 8, 31));
        Assert.Equal(2, after.EntryCount);
        Assert.Equal(10, after.TotalMinutes);
    }

    [Fact]
    public async Task List_LimitZero_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_MalformedCursor_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_PagesByKeyset()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        foreach (var day in new[] { 6, 13, 20 })
        {
            await client.PostAsJsonAsync("/meal-plans",
                new CreateMealPlanRequest(UtcMidnight(2026, 7, day)), TestJson.Options);
        }

        var first = await (await client.GetAsync("/meal-plans?limit=2"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.Equal(2, first!.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await (await client.GetAsync($"/meal-plans?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.Single(second!.Items);
        Assert.Equal(UtcMidnight(2026, 7, 6), second.Items[0].WeekStartDate);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task List_RequiresAuth()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/meal-plans");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient client, DateTime weekStart)
    {
        var response = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStart), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var plan = await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);
        return plan!.Id;
    }

    private static async Task AddEntryAsync(HttpClient client, Guid planId, DayOfWeek day, MealType meal, Guid recipeId)
    {
        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{planId}/entries",
            new AddMealPlanEntryRequest(day, meal, recipeId),
            TestJson.Options);
        response.EnsureSuccessStatusCode();
    }

    // Scoped by ?weekStart= so the shared database can't leak another test's plan in.
    private static async Task<MealPlanSummaryResponse> GetSummaryAsync(HttpClient client, DateTime weekStart)
    {
        var body = await client.GetFromJsonAsync<MealPlanListResponse>(
            $"/meal-plans?weekStart={weekStart:yyyy-MM-dd}T00:00:00Z", TestJson.Options);
        return Assert.Single(body!.Items);
    }

    // Same request shape MealPlanEndpointsTests uses to create the recipe an entry needs.
    // Prep/cook are parameterised so the TotalMinutes tests can assert an exact, distinctive
    // sum rather than a multiple of one shared constant.
    private static async Task<Guid> CreateRecipeAsync(HttpClient client, int prepMinutes = 10, int cookMinutes = 20)
    {
        var response = await client.PostAsJsonAsync("/recipes", new CreateRecipeRequest(
            Title: "Meal Plan List Test Focaccia",
            Description: "A minimal focaccia used to exercise the meal-plan list endpoint.",
            PrepTimeMinutes: prepMinutes,
            CookTimeMinutes: cookMinutes,
            Servings: 4,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: Cuisine.Italian,
            CaloriesPerServing: 210,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = UnitOfMeasure.Cup }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
            Tags: [RecipeTag.Bread]), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var recipe = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        return recipe!.Id;
    }
}
