using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

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
        [new RecipeIngredient { Name = "Test ingredient", Quantity = 1m, Unit = UnitOfMeasure.Piece }];

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

    // --- AI budget & quotas (ai-quotas, wired into this lane 2026-08-05) ---------------------
    //
    // Same shape as the chat lane's quota tests: the factory sets no AiQuota:* config, so these
    // run on the code defaults (50 calls / 250k tokens per UTC day) and exhaustion is arranged
    // by seeding a usage row rather than burning fifty calls.

    [Fact]
    public async Task ProposeWeek_RecordsOneUsageRow_OnItsOwnLane()
    {
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await MealPlanTestHelper.CreateRecipeAsync(client, "Metered Proposal Soup", Ingredients());

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.AiUsageRecords.SingleAsync(r => r.UserId == auth.UserId);

        // The lane name is the assertion that matters: spend attributed to "chat" would still
        // enforce the ceiling correctly while making per-feature cost unreadable.
        Assert.Equal("meal-plan-proposal", row.Lane);
        Assert.Equal(FakeMealPlanAssistantService.Usage.PromptTokens, row.PromptTokens);
        Assert.Equal(FakeMealPlanAssistantService.Usage.CompletionTokens, row.CompletionTokens);
        Assert.Equal(FakeMealPlanAssistantService.Usage.TotalTokens, row.TotalTokens);
    }

    [Fact]
    public async Task ProposeWeek_ExhaustedBudget_Returns429_AndSpendsNothing()
    {
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await MealPlanTestHelper.CreateRecipeAsync(client, "Unaffordable Stew", Ingredients());
        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // Refused BEFORE the provider call, so the seeded row is still the only one.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.AiUsageRecords.CountAsync(r => r.UserId == auth.UserId));
    }

    [Fact]
    public async Task ProposeWeek_AssistantFailure_IsNotBilled()
    {
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await MealPlanTestHelper.CreateRecipeAsync(
            client, $"{FakeMealPlanAssistantService.FailSentinel} unbilled", Ingredients(), RecipeVisibility.Private);

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week",
            new ProposeWeekRequest(MealPlanTestHelper.NextMonday()),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        // A failed call produced nothing and must cost nothing — the recording happens after
        // the call returns, never before it.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AiUsageRecords.AnyAsync(r => r.UserId == auth.UserId));
    }

    [Fact]
    public async Task ProposeWeek_WithNoOpenSlots_IsNeitherBilledNorRefused()
    {
        // Pins where the budget gate sits. A full week reaches no provider, so refusing it for
        // budget would charge the user for the app's own arithmetic: the gate belongs BELOW the
        // cheap exits and above the call. Exhausted budget + nothing to propose = a plain 200.
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipe = await MealPlanTestHelper.CreateRecipeAsync(client, "Fills The Week", Ingredients());
        var weekStart = MealPlanTestHelper.NextMonday();
        var plan = await MealPlanTestHelper.CreateMealPlanAsync(client, weekStart);
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            foreach (var mealType in new[] { MealType.Breakfast, MealType.Lunch, MealType.Dinner })
            {
                await MealPlanTestHelper.AddEntryAsync(client, plan.Id, day, mealType, recipe.Id);
            }
        }

        await ExhaustBudgetAsync(auth.UserId);

        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week", new ProposeWeekRequest(weekStart), TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.Content.ReadFromJsonAsync<ProposeWeekResponse>(TestJson.Options))!;
        Assert.Empty(proposal.Slots);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.AiUsageRecords.CountAsync(r => r.UserId == auth.UserId));
    }

    // --- candidate-set visibility (stream F's policy, aligned here 2026-08-05) ---------------

    [Fact]
    public async Task ProposeWeek_FriendsOnlyCandidate_LastsExactlyAsLongAsTheMutualFollow()
    {
        // Before this alignment the candidate set was hand-written "Public OR own", so a
        // friend's FriendsOnly recipe could never be proposed even though the rest of the app
        // showed it. The second half is the sharper half: the SavedRecipes row survives the
        // unfollow, so anything caching visibility at save time would keep proposing it.
        var viewerClient = _factory.CreateClient();
        var viewer = await AuthTestHelper.RegisterAndAuthenticateAsync(viewerClient);
        var authorClient = _factory.CreateClient();
        var author = await AuthTestHelper.RegisterAndAuthenticateAsync(authorClient);

        var friendsOnly = await MealPlanTestHelper.CreateRecipeAsync(
            authorClient, "Shared With Friends Only", Ingredients(), RecipeVisibility.FriendsOnly);

        await FollowTestHelper.MakeMutualAsync(viewerClient, viewer.UserId, authorClient, author.UserId);
        (await viewerClient.PostAsync($"/recipes/{friendsOnly.Id}/saves", null)).EnsureSuccessStatusCode();

        var weekStart = MealPlanTestHelper.NextMonday();
        var whileFriends = await ProposeAsync(viewerClient, weekStart);
        Assert.Contains(whileFriends.Slots, s => s.Recipe.Id == friendsOnly.Id);

        // One edge breaks; the save stays.
        await FollowTestHelper.UnfollowAsync(authorClient, viewer.UserId);

        var afterUnfollow = await ProposeAsync(viewerClient, weekStart);
        Assert.DoesNotContain(afterUnfollow.Slots, s => s.Recipe.Id == friendsOnly.Id);
    }

    private async Task<ProposeWeekResponse> ProposeAsync(HttpClient client, DateTime weekStart)
    {
        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week", new ProposeWeekRequest(weekStart), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProposeWeekResponse>(TestJson.Options))!;
    }

    // Puts a user on the token ceiling with one seeded row — the realistic shape (an expensive
    // call crossed the line) without fifty round trips.
    private async Task ExhaustBudgetAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AiUsageRecords.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Lane = "meal-plan-proposal",
            PromptTokens = 200_000,
            CompletionTokens = 50_000,
            TotalTokens = 250_000,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
