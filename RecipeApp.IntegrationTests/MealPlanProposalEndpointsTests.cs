using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// POST /meal-plans/propose-week (Stream C, D2 = propose-then-accept). The real
// MealPlanProposalService runs against the container DB; only the LLM seam is faked
// (FakeMealPlanAssistantService fills every open slot, round-robin over the candidates).
// The DB is shared across the class and public recipes enter every user's candidate set,
// so tests assert on slot structure (counts, occupancy, ordering), never on exact recipes —
// except via PRIVATE recipes, which only their owner's candidate load can see.
public class MealPlanProposalEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private static List<RecipeIngredient> Ingredients() =>
        [new RecipeIngredient { Name = "Test ingredient", Quantity = 1m, Unit = "piece" }];

    [Fact]
    public async Task ProposeWeek_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProposeWeek_NonMondayWeekStart_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday().AddDays(1)),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProposeWeek_FreshWeek_ProposesAllTwentyOneSlots_MondayFirst()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await MealPlanTestHelper.CreateRecipeAsync(client, "Proposal Soup", Ingredients());
        var weekStart = MealPlanTestHelper.NextMonday();

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week", new ProposeWeekRequest(weekStart), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.Content.ReadFromJsonAsync<ProposeWeekResponse>(TestJson.Options))!;
        Assert.Equal(weekStart, proposal.WeekStartDate);
        // 7 days × Breakfast/Lunch/Dinner, nothing occupied — and no Dessert/Snack rows the
        // week board couldn't render.
        Assert.Equal(21, proposal.Slots.Count);
        Assert.Equal(DayOfWeek.Monday, proposal.Slots[0].DayOfWeek);
        Assert.Equal(MealType.Breakfast, proposal.Slots[0].MealType);
        Assert.All(proposal.Slots, s => Assert.Contains(s.MealType, new[] { MealType.Breakfast, MealType.Lunch, MealType.Dinner }));
        Assert.All(proposal.Slots, s => Assert.False(string.IsNullOrEmpty(s.Recipe.Title)));
        // Every (day, meal) is unique — the proposal can't double-book a slot.
        Assert.Equal(21, proposal.Slots.Select(s => (s.DayOfWeek, s.MealType)).Distinct().Count());
    }

    [Fact]
    public async Task ProposeWeek_OccupiedSlot_IsNeverProposed()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var recipe = await MealPlanTestHelper.CreateRecipeAsync(client, "Already Planned Stew", Ingredients());
        var weekStart = MealPlanTestHelper.NextMonday();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, weekStart);
        await MealPlanTestHelper.AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week", new ProposeWeekRequest(weekStart), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.Content.ReadFromJsonAsync<ProposeWeekResponse>(TestJson.Options))!;
        Assert.Equal(20, proposal.Slots.Count);
        Assert.DoesNotContain(proposal.Slots, s => s.DayOfWeek == DayOfWeek.Monday && s.MealType == MealType.Dinner);
    }

    [Fact]
    public async Task ProposeWeek_AssistantFailure_Returns502()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        // PRIVATE so the sentinel only enters THIS user's candidate set — a public one would
        // blow up every other test's proposal on the shared DB.
        await MealPlanTestHelper.CreateRecipeAsync(
            client, $"{FakeMealPlanAssistantService.FailSentinel} recipe", Ingredients(), RecipeVisibility.Private);

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
