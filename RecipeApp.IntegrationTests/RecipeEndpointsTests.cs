using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

public class RecipeEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task CreateRecipe_WithValidBody_ReturnsCreatedWithOwnerFromToken()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var request = ValidCreateRecipeRequest();

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal($"/recipes/{body!.Id}", response.Headers.Location?.ToString());
        Assert.Equal(auth.UserId, body.CreatedByUserId);
        Assert.Equal(request.Title, body.Title);

        // jsonb round-trip: read the row back through EF (not the in-memory entity the
        // endpoint mapped its response from) to prove the List<> columns survive Postgres.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Recipes.SingleAsync(r => r.Id == body.Id);

        Assert.Equal(auth.UserId, stored.CreatedByUserId);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);

        var ingredient = Assert.Single(stored.Ingredients);
        Assert.Equal("flour", ingredient.Name);
        Assert.Equal(2.5m, ingredient.Quantity);
        Assert.Equal(UnitOfMeasure.Cup, ingredient.Unit);

        Assert.Equal(2, stored.Steps.Count);
        Assert.Equal("Mix the flour with water.", stored.Steps[0].Description);
        Assert.Equal(600, stored.Steps[1].TimerSeconds);

        Assert.Equal(new List<RecipeTag> { RecipeTag.Vegan, RecipeTag.Quick }, stored.Tags);
    }

    [Fact]
    public async Task CreateRecipe_WithInvalidBody_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var request = ValidCreateRecipeRequest() with { Title = "", Ingredients = [] };

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRecipe_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/recipes", ValidCreateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRecipeById_PublicRecipe_ReturnsOkForAnotherUser()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest());

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal(created.Title, body.Title);
        Assert.Equal(owner.UserId, body.CreatedByUserId);
        Assert.Equal(created.Ingredients.Count, body.Ingredients.Count);
        Assert.Equal(created.Steps.Count, body.Steps.Count);
        Assert.Equal(created.Tags, body.Tags);
    }

    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task GetRecipeById_NonPublicRecipe_ReturnsOkForOwner(RecipeVisibility visibility)
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest() with { Visibility = visibility });

        var response = await client.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal(visibility, body.Visibility);
        Assert.Equal(auth.UserId, body.CreatedByUserId);
    }

    // --- FriendsOnly (stream F, decision D6, 2026-08-05) -------------------------------
    //
    // These three theories replace a pair of [InlineData(Private)] / [InlineData(FriendsOnly)]
    // rows that asserted "another user's non-public recipe is 404" full stop. FriendsOnly is
    // no longer decided by ownership alone, so the relationship between the two accounts
    // becomes the parameter: all four arrangements are enumerated on every read path, and
    // exactly one of them is allowed to return 200.
    //
    // 404 — not 403 — throughout, so the response never confirms a hidden recipe exists.

    // Private is owner-only at EVERY relationship. Pinned explicitly because the obvious way
    // to get FriendsOnly wrong is to widen "non-public" rather than FriendsOnly alone, and
    // this theory is what would catch that: a mutual follow must not unlock a private draft.
    [Theory]
    [InlineData(FollowRelationship.Stranger)]
    [InlineData(FollowRelationship.ViewerFollowsAuthor)]
    [InlineData(FollowRelationship.AuthorFollowsViewer)]
    [InlineData(FollowRelationship.Mutual)]
    public async Task GetRecipeById_AnotherUsersPrivateRecipe_ReturnsNotFoundAtEveryRelationship(
        FollowRelationship relationship)
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(
            ownerClient, ValidCreateRecipeRequest() with { Visibility = RecipeVisibility.Private });

        var otherClient = factory.CreateClient();
        var other = await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await FollowTestHelper.ArrangeAsync(relationship, otherClient, other.UserId, ownerClient, owner.UserId);

        var response = await otherClient.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // FriendsOnly: mutual follow reads it, every one-way arrangement and the stranger do not.
    [Theory]
    [InlineData(FollowRelationship.Stranger, HttpStatusCode.NotFound)]
    [InlineData(FollowRelationship.ViewerFollowsAuthor, HttpStatusCode.NotFound)]
    [InlineData(FollowRelationship.AuthorFollowsViewer, HttpStatusCode.NotFound)]
    [InlineData(FollowRelationship.Mutual, HttpStatusCode.OK)]
    public async Task GetRecipeById_AnotherUsersFriendsOnlyRecipe_RequiresAMutualFollow(
        FollowRelationship relationship, HttpStatusCode expected)
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(
            ownerClient, ValidCreateRecipeRequest() with { Visibility = RecipeVisibility.FriendsOnly });

        var otherClient = factory.CreateClient();
        var other = await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await FollowTestHelper.ArrangeAsync(relationship, otherClient, other.UserId, ownerClient, owner.UserId);

        var response = await otherClient.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(RecipeVisibility.FriendsOnly, body.Visibility);
            Assert.Equal(owner.UserId, body.CreatedByUserId);
        }
    }

    // A guest has no token and therefore no follow graph — there is no arrangement that
    // makes them anyone's friend, so FriendsOnly is 404 for them by construction.
    [Fact]
    public async Task GetRecipeById_FriendsOnlyRecipe_UnauthenticatedGuest_ReturnsNotFound()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(
            ownerClient, ValidCreateRecipeRequest() with { Visibility = RecipeVisibility.FriendsOnly });

        var guest = factory.CreateClient();

        var response = await guest.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecipeById_NonexistentId_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/recipes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecipeById_SoftDeletedRecipe_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        // No DELETE endpoint until checkpoint 05 — soft-delete the row directly.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Recipes.SingleAsync(r => r.Id == created.Id);
            stored.IsDeleted = true;
            stored.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Guest access: GET /recipes/{id} is anonymous-capable — an unknown id is a 404 for a
    // guest, same as for a signed-in caller. The guest visibility matrix (public 200,
    // private/friends 404) lives in GuestAccessTests.
    [Fact]
    public async Task GetRecipeById_WithoutToken_UnknownRecipe_ReturnsNotFound()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/recipes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_AsOwner_ReturnsOkWithFullyReplacedRecipe()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());
        var update = ValidUpdateRecipeRequest();

        // Capture CreatedAt as Postgres materialized it (microsecond precision) — the POST
        // response carries the in-memory pre-save DateTime.UtcNow (100 ns ticks), which
        // differs from the stored value by sub-microsecond truncation.
        DateTime createdAtInDb;
        using (var preUpdateScope = factory.Services.CreateScope())
        {
            var preUpdateDb = preUpdateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            createdAtInDb = (await preUpdateDb.Recipes.SingleAsync(r => r.Id == created.Id)).CreatedAt;
        }

        var response = await client.PutAsJsonAsync($"/recipes/{created.Id}", update, TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal(update.Title, body.Title);
        Assert.Equal(update.Description, body.Description);
        Assert.Equal(update.Difficulty, body.Difficulty);
        Assert.Equal(update.CuisineType, body.CuisineType);
        Assert.Equal(update.Visibility, body.Visibility);
        Assert.NotNull(body.UpdatedAt);
        Assert.Equal(createdAtInDb, body.CreatedAt);
        Assert.Equal(auth.UserId, body.CreatedByUserId);

        // jsonb full replace: re-read the row through a fresh DbContext scope (not the
        // endpoint's in-memory response) to prove the List<> columns were overwritten
        // wholesale in Postgres.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Recipes.SingleAsync(r => r.Id == created.Id);

        Assert.Equal(update.Title, stored.Title);
        Assert.NotNull(stored.UpdatedAt);
        Assert.Equal(createdAtInDb, stored.CreatedAt);
        Assert.Equal(auth.UserId, stored.CreatedByUserId);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);

        var ingredient = Assert.Single(stored.Ingredients);
        Assert.Equal("rye flour", ingredient.Name);
        Assert.Equal(3m, ingredient.Quantity);
        Assert.Equal(UnitOfMeasure.Cup, ingredient.Unit);

        var step = Assert.Single(stored.Steps);
        Assert.Equal("Knead the rye dough and bake.", step.Description);
        Assert.Equal(1200, step.TimerSeconds);

        Assert.Equal(new List<RecipeTag> { RecipeTag.Comfort, RecipeTag.Baking }, stored.Tags);
    }

    // Visible-but-not-owned is a 403; the recipe's existence is already public knowledge.
    [Fact]
    public async Task UpdateRecipe_AnotherUsersPublicRecipe_ReturnsForbidden()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest());

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // 404 — not 403 — so the response doesn't confirm the private recipe exists. FriendsOnly
    // joins Private here for every relationship EXCEPT mutual, which is covered below.
    [Theory]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Mutual)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.ViewerFollowsAuthor)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.AuthorFollowsViewer)]
    public async Task UpdateRecipe_AnotherUsersUnreadableRecipe_ReturnsNotFound(
        RecipeVisibility visibility, FollowRelationship relationship)
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest() with { Visibility = visibility });

        var otherClient = factory.CreateClient();
        var other = await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await FollowTestHelper.ArrangeAsync(relationship, otherClient, other.UserId, ownerClient, owner.UserId);

        var response = await otherClient.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Stream F widened READING, never writing. A mutual friend can now open the recipe, so
    // the 404-not-403 convention flips to its other half for them: they get Forbidden,
    // exactly as any user does on a Public recipe they don't own. The recipe must be
    // untouched afterwards — a 403 that still saved would be the worst of both worlds.
    [Fact]
    public async Task UpdateRecipe_AMutualFriendsFriendsOnlyRecipe_ReturnsForbiddenAndChangesNothing()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(
            ownerClient, ValidCreateRecipeRequest() with { Visibility = RecipeVisibility.FriendsOnly });

        var friendClient = factory.CreateClient();
        var friend = await AuthTestHelper.RegisterAndAuthenticateAsync(friendClient);
        await FollowTestHelper.MakeMutualAsync(friendClient, friend.UserId, ownerClient, owner.UserId);

        var response = await friendClient.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var reread = await ownerClient.GetFromJsonAsync<RecipeResponse>($"/recipes/{created.Id}", TestJson.Options);
        Assert.Equal(created.Title, reread!.Title);
        Assert.Equal(RecipeVisibility.FriendsOnly, reread.Visibility);
    }

    [Fact]
    public async Task UpdateRecipe_NonexistentId_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PutAsJsonAsync($"/recipes/{Guid.NewGuid()}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_SoftDeletedRecipe_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        // No DELETE endpoint until checkpoint 05 — soft-delete the row directly.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Recipes.SingleAsync(r => r.Id == created.Id);
            stored.IsDeleted = true;
            stored.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_WithInvalidBody_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());
        var update = ValidUpdateRecipeRequest() with { Title = "", Ingredients = [] };

        var response = await client.PutAsJsonAsync($"/recipes/{created.Id}", update, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/recipes/{Guid.NewGuid()}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRecipe_AsOwner_ReturnsNoContentAndSoftDeletesRowInPostgres()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        var response = await client.DeleteAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The row must STILL EXIST in Postgres — soft delete, not a SQL DELETE. Read it back
        // through IgnoreQueryFilters() (the global r => !r.IsDeleted filter would otherwise
        // hide it) in a fresh DbContext scope, not via the endpoint's response.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Recipes.IgnoreQueryFilters().SingleAsync(r => r.Id == created.Id);

        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedAt);
    }

    [Fact]
    public async Task DeleteRecipe_AsOwner_RemovesFromDetailAndList()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        // Before deletion the owner sees it in both detail and the list.
        Assert.Contains(created.Id, await GetAllRecipeIdsAsync(client));

        var deleteResponse = await client.DeleteAsync($"/recipes/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // After deletion: 404 on detail and absent from the list — even for the owner.
        var detailResponse = await client.GetAsync($"/recipes/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
        Assert.DoesNotContain(created.Id, await GetAllRecipeIdsAsync(client));
    }

    [Fact]
    public async Task DeleteRecipe_PreservesInteractionRows_NoCascadeFires()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        // Seed a Like, a Comment, and a SavedRecipe referencing the recipe directly via
        // DbContext (no interaction endpoints exist yet). The interacting user is the owner —
        // a registered user, so the FK to Users is satisfied.
        Guid commentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Likes.Add(new Like { UserId = auth.UserId, RecipeId = created.Id });
            var comment = new Comment { Id = Guid.NewGuid(), Content = "Delicious!", UserId = auth.UserId, RecipeId = created.Id };
            commentId = comment.Id;
            db.Comments.Add(comment);
            db.SavedRecipes.Add(new SavedRecipe { UserId = auth.UserId, RecipeId = created.Id });
            await db.SaveChangesAsync();
        }

        var deleteResponse = await client.DeleteAsync($"/recipes/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // No SQL DELETE fired against the recipe, so the implicit cascades never ran — every
        // interaction row still exists. (These DbSets carry no query filter of their own.)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await db.Likes.AnyAsync(l => l.UserId == auth.UserId && l.RecipeId == created.Id));
            Assert.True(await db.Comments.AnyAsync(c => c.Id == commentId));
            Assert.True(await db.SavedRecipes.AnyAsync(s => s.UserId == auth.UserId && s.RecipeId == created.Id));
        }
    }

    [Fact]
    public async Task DeleteRecipe_Repeated_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        var first = await client.DeleteAsync($"/recipes/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // The row is now behind the global query filter, so the second DELETE can't find it.
        var second = await client.DeleteAsync($"/recipes/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // Visible-but-not-owned is a 403; the recipe's existence is already public knowledge.
    [Fact]
    public async Task DeleteRecipe_AnotherUsersPublicRecipe_ReturnsForbidden()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest());

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.DeleteAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // 404 — not 403 — so the response doesn't confirm the private recipe exists. Same
    // relationship matrix as UpdateRecipe: unreadable is 404 at every arrangement below.
    [Theory]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.Private, FollowRelationship.Mutual)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.Stranger)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.ViewerFollowsAuthor)]
    [InlineData(RecipeVisibility.FriendsOnly, FollowRelationship.AuthorFollowsViewer)]
    public async Task DeleteRecipe_AnotherUsersUnreadableRecipe_ReturnsNotFound(
        RecipeVisibility visibility, FollowRelationship relationship)
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest() with { Visibility = visibility });

        var otherClient = factory.CreateClient();
        var other = await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await FollowTestHelper.ArrangeAsync(relationship, otherClient, other.UserId, ownerClient, owner.UserId);

        var response = await otherClient.DeleteAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Deleting stays author-only: a mutual friend can read the recipe, so they get 403 —
    // and the recipe is still there afterwards.
    [Fact]
    public async Task DeleteRecipe_AMutualFriendsFriendsOnlyRecipe_ReturnsForbiddenAndKeepsIt()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(
            ownerClient, ValidCreateRecipeRequest() with { Visibility = RecipeVisibility.FriendsOnly });

        var friendClient = factory.CreateClient();
        var friend = await AuthTestHelper.RegisterAndAuthenticateAsync(friendClient);
        await FollowTestHelper.MakeMutualAsync(friendClient, friend.UserId, ownerClient, owner.UserId);

        var response = await friendClient.DeleteAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/recipes/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task DeleteRecipe_NonexistentId_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.DeleteAsync($"/recipes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRecipe_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/recipes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Walks every page of GET /recipes for the caller (cursor-followed), returning all
    // visible recipe ids. Used to prove presence-then-absence around a delete without
    // depending on how many recipes other tests in this shared-DB class have created.
    private static async Task<List<Guid>> GetAllRecipeIdsAsync(HttpClient client)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = cursor is null
                ? "/recipes?limit=50"
                : $"/recipes?limit=50&cursor={Uri.EscapeDataString(cursor)}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<RecipeListResponse>(TestJson.Options)
                ?? throw new InvalidOperationException("GET /recipes returned an empty body.");
            ids.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return ids;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, CreateRecipeRequest request)
    {
        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest() => new(
        Title: "Integration Test Flatbread",
        Description: "A minimal flatbread used to exercise POST /recipes end to end.",
        PrepTimeMinutes: 10,
        CookTimeMinutes: 15,
        Servings: 4,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: Cuisine.Mediterranean,
        CaloriesPerServing: 180,
        ImageUrl: null,
        Visibility: RecipeVisibility.Public,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 2.5m, Unit = UnitOfMeasure.Cup }],
        Steps:
        [
            new RecipeStep { StepNumber = 1, Description = "Mix the flour with water." },
            new RecipeStep { StepNumber = 2, Description = "Rest the dough.", TimerSeconds = 600 },
        ],
        Tags: [RecipeTag.Vegan, RecipeTag.Quick]);

    // Every field differs from ValidCreateRecipeRequest so the full-replace assertions
    // can't pass by accident.
    private static UpdateRecipeRequest ValidUpdateRecipeRequest() => new(
        Title: "Integration Test Rye Bread",
        Description: "A replacement recipe used to exercise PUT /recipes/{id} end to end.",
        PrepTimeMinutes: 20,
        CookTimeMinutes: 40,
        Servings: 6,
        Difficulty: DifficultyLevel.Medium,
        CuisineType: Cuisine.Nordic,
        CaloriesPerServing: 250,
        ImageUrl: "https://example.test/rye.jpg",
        Visibility: RecipeVisibility.Public,
        Ingredients: [new RecipeIngredient { Name = "rye flour", Quantity = 3m, Unit = UnitOfMeasure.Cup }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Knead the rye dough and bake.", TimerSeconds = 1200 }],
        Tags: [RecipeTag.Comfort, RecipeTag.Baking]);
}
