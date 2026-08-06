using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Social.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// POST /users/me/onboarding — the post-register wizard's write (stream K), and the three
/// consumers reading what it stores.
///
/// The consumer half is the point of this file. A preference the wizard saves and nothing
/// reads is the dead-<c>RankEvent</c> mistake wearing a new name, and that failure is
/// invisible to any test that only round-trips the column: storage and retrieval both work,
/// and the feature still does nothing. So each consumer is asserted through an observable
/// effect — the order the candidates reach the planner in, and the text that reaches the
/// generator — rather than through the value coming back out of the profile endpoint.
/// </summary>
public class OnboardingEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private static List<RecipeIngredient> Ingredients() =>
        [new RecipeIngredient { Name = "Test ingredient", Quantity = 1m, Unit = UnitOfMeasure.Piece }];

    // ── The wizard's own contract ────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshUser_NeedsOnboarding_UntilTheWizardIsAnswered()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var before = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.True(before!.NeedsOnboarding);

        var response = await client.PostAsJsonAsync("/users/me/onboarding", new CompleteOnboardingRequest(
            CuisinePreferences: [Cuisine.Thai, Cuisine.Korean],
            DietaryRestrictions: [DietaryRestriction.Pescatarian]), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>(TestJson.Options);
        Assert.Equal([Cuisine.Thai, Cuisine.Korean], profile!.CuisinePreferences);
        Assert.Equal([DietaryRestriction.Pescatarian], profile.DietaryRestrictions);

        var after = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.False(after!.NeedsOnboarding);
    }

    // Skipping is a real answer, not a no-op: the user who chose nothing must not be asked
    // again on the next device they sign in on. This is the assertion that would fail if
    // NeedsOnboarding were ever derived from "both lists are empty" instead of the stamp.
    [Fact]
    public async Task SkippingTheWizard_StillCountsAsAnswered()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync(
            "/users/me/onboarding", new CompleteOnboardingRequest(), TestJson.Options);
        response.EnsureSuccessStatusCode();

        var me = await client.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);
        Assert.False(me!.NeedsOnboarding);

        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>(TestJson.Options);
        Assert.Empty(profile!.CuisinePreferences);
        Assert.Empty(profile.DietaryRestrictions);
    }

    // The wizard writes ONLY what it collects. If it ever went through PUT /users/me — a full
    // replace — everything the user set at registration would be cleared by a form that never
    // asked about it.
    [Fact]
    public async Task Onboarding_LeavesTheRestOfTheAccountAlone()
    {
        var client = _factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var username = $"kept_{Guid.NewGuid():N}";

        (await client.PutAsJsonAsync("/users/me", new UpdateProfileRequest(
            username, "Bio that must survive.", "/images/keep.jpg",
            RecipeVisibility.FriendsOnly), TestJson.Options)).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync("/users/me/onboarding", new CompleteOnboardingRequest(
            CuisinePreferences: [Cuisine.Greek]), TestJson.Options)).EnsureSuccessStatusCode();

        var profile = await client.GetFromJsonAsync<UserProfileResponse>(
            $"/users/{auth.UserId}", TestJson.Options);
        Assert.Equal(username, profile!.Username);
        Assert.Equal("Bio that must survive.", profile.Bio);
        Assert.Equal("/images/keep.jpg", profile.ProfileImageUrl);
        Assert.Equal(RecipeVisibility.FriendsOnly, profile.DefaultRecipeVisibility);
        Assert.Equal([Cuisine.Greek], profile.CuisinePreferences);
    }

    [Fact]
    public async Task Onboarding_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/users/me/onboarding", new CompleteOnboardingRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // An out-of-range ordinal binds happily through the enum converter, so the validator is
    // the only thing standing between it and a jsonb row nothing can ever match.
    [Fact]
    public async Task Onboarding_WithAnUnknownCuisine_Returns400()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync(
            "/users/me/onboarding",
            JsonContent.Create(new { cuisinePreferences = new[] { 9999 } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Consumer 1: propose-week candidate weighting ─────────────────────────────────────
    //
    // FakeMealPlanAssistantService fills open slots round-robin over the candidates IN ORDER,
    // so candidates[0] is exactly the recipe the first proposed slot names — which makes the
    // candidate ordering observable end-to-end without reaching into the service.
    //
    // Both recipes are PRIVATE so only this user's candidate load can see them (the DB is
    // shared across the class), and the Thai one is created FIRST so recency puts it SECOND.
    // That is what makes the assertion meaningful: without the weighting the newer Italian
    // recipe leads, so this test fails if the preference is ignored.
    [Fact]
    public async Task ProposeWeek_LeadsWithARecipeInAPreferredCuisine()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var thai = await MealPlanTestHelper.CreateRecipeAsync(
            client, $"Thai Curry {Guid.NewGuid():N}", Ingredients(), RecipeVisibility.Private, Cuisine.Thai);
        var italian = await MealPlanTestHelper.CreateRecipeAsync(
            client, $"Italian Ragu {Guid.NewGuid():N}", Ingredients(), RecipeVisibility.Private, Cuisine.Italian);

        // Sanity: with no preference the newer (Italian) recipe leads.
        var unweighted = await ProposeAsync(client, MealPlanTestHelper.NextMonday());
        Assert.Equal(italian.Id, unweighted.Slots[0].Recipe.Id);

        (await client.PostAsJsonAsync("/users/me/onboarding", new CompleteOnboardingRequest(
            CuisinePreferences: [Cuisine.Thai]), TestJson.Options)).EnsureSuccessStatusCode();

        var weighted = await ProposeAsync(client, MealPlanTestHelper.NextMonday().AddDays(7));
        Assert.Equal(thai.Id, weighted.Slots[0].Recipe.Id);

        // Weighting, not filtering — the un-preferred recipe is still a candidate. If it had
        // been dropped, the 21 slots would all name the Thai recipe.
        Assert.Contains(weighted.Slots, s => s.Recipe.Id == italian.Id);
    }

    // ── Consumer 2: generator defaults ───────────────────────────────────────────────────
    //
    // FakeRecipeGenerationAssistant echoes what it was handed into the draft's description,
    // so this asserts the preference actually REACHED the generator rather than merely that
    // it was stored.
    [Fact]
    public async Task Generator_ReceivesTheCallersCuisinePreferences()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        (await client.PostAsJsonAsync("/users/me/onboarding", new CompleteOnboardingRequest(
            CuisinePreferences: [Cuisine.MiddleEastern]), TestJson.Options)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/recipes/generate", new GenerateRecipeRequest("something for tonight", null, null), TestJson.Options);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GenerateRecipeResponse>(TestJson.Options);

        // Vocabulary.Describe renders the member as prose before it reaches the prompt.
        Assert.Contains("cuisines-Middle Eastern", body!.Recipe.Description);
    }

    [Fact]
    public async Task Generator_GetsNoCuisines_WhenTheWizardWasSkipped()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        (await client.PostAsJsonAsync(
            "/users/me/onboarding", new CompleteOnboardingRequest(), TestJson.Options)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/recipes/generate", new GenerateRecipeRequest("something for tonight", null, null), TestJson.Options);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GenerateRecipeResponse>(TestJson.Options);
        Assert.Contains("cuisines-none", body!.Recipe.Description);
    }

    private static async Task<ProposeWeekResponse> ProposeAsync(HttpClient client, DateTime weekStart)
    {
        var response = await client.PostAsJsonAsync(
            "/meal-plans/propose-week", new ProposeWeekRequest(weekStart), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProposeWeekResponse>(TestJson.Options))!;
    }
}
