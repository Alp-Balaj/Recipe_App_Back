using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// Tests in this class share one database (sequential within the class, own container via
// the class fixture), so every test scopes its assertions with unique marker tags/cuisines
// or walks all pages and asserts id presence/absence — never "the list contains only X".
public class RecipeListEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task ListRecipes_MultiPageWalk_ReturnsAllPagesWithNoDuplicatesOrSkips()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var marker = UniqueTag();

        var seeded = new List<RecipeResponse>();
        for (var i = 1; i <= 7; i++)
        {
            seeded.Add(await SeedRecipeAsync(client, title: $"Walk {i}", tags: [marker, "walk"]));
        }

        var pages = await WalkAllPagesAsync(client, $"/recipes?tags={marker}&limit=3");

        Assert.Equal(3, pages.Count);
        Assert.Equal([3, 3, 1], pages.Select(p => p.Items.Count));
        Assert.NotNull(pages[0].NextCursor);
        Assert.NotNull(pages[1].NextCursor);
        Assert.Null(pages[2].NextCursor);

        // Newest first (CreatedAt DESC, Id DESC) with no duplicates and no skips across
        // page boundaries: the walk must yield exactly the seeds in reverse creation order.
        var walkedIds = pages.SelectMany(p => p.Items).Select(r => r.Id).ToList();
        var expectedIds = seeded.AsEnumerable().Reverse().Select(r => r.Id).ToList();
        Assert.Equal(expectedIds, walkedIds);

        // Items are the full RecipeResponse (Decisions §4) — ingredients/steps included.
        var newest = pages[0].Items[0];
        Assert.Equal("Walk 7", newest.Title);
        Assert.NotEmpty(newest.Ingredients);
        Assert.NotEmpty(newest.Steps);
        Assert.Contains(marker, newest.Tags);
    }

    [Fact]
    public async Task ListRecipes_LastPageExactlyFitsLimit_NextCursorIsNull()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var marker = UniqueTag();

        for (var i = 0; i < 3; i++)
        {
            await SeedRecipeAsync(client, tags: [marker]);
        }

        var page = await GetListAsync(client, $"/recipes?tags={marker}&limit=3");

        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListRecipes_CuisineFilter_IsCaseInsensitiveExactMatch()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var suffix = Guid.NewGuid().ToString("N");

        var fusion = await SeedRecipeAsync(client, cuisineType: $"Fusion{suffix}");
        await SeedRecipeAsync(client, cuisineType: $"Other{suffix}");

        // Different casing than stored — must still match exactly one recipe.
        var matched = await GetListAsync(client, $"/recipes?cuisine=fUsIoN{suffix.ToUpperInvariant()}");
        Assert.Equal([fusion.Id], matched.Items.Select(r => r.Id));

        // Substring of a stored cuisine — exact match only, so nothing comes back.
        var substring = await GetListAsync(client, $"/recipes?cuisine=usion{suffix}");
        Assert.Empty(substring.Items);
    }

    [Fact]
    public async Task ListRecipes_DifficultyFilter_NarrowsResults()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var marker = UniqueTag();

        await SeedRecipeAsync(client, tags: [marker], difficulty: DifficultyLevel.Easy);
        var hard = await SeedRecipeAsync(client, tags: [marker], difficulty: DifficultyLevel.Hard);

        var page = await GetListAsync(client, $"/recipes?tags={marker}&difficulty=Hard");

        Assert.Equal([hard.Id], page.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task ListRecipes_TagsFilter_MatchesAllRequestedTags()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var tag1 = UniqueTag();
        var tag2 = UniqueTag();

        var onlyTag1 = await SeedRecipeAsync(client, tags: [tag1]);
        await SeedRecipeAsync(client, tags: [tag2]);
        var both = await SeedRecipeAsync(client, tags: [tag1, tag2]);

        // Match-ALL: requesting both tags returns only the recipe carrying both.
        var bothPage = await GetListAsync(client, $"/recipes?tags={tag1}&tags={tag2}");
        Assert.Equal([both.Id], bothPage.Items.Select(r => r.Id));

        // A single tag still matches every recipe carrying it.
        var tag1Page = await GetListAsync(client, $"/recipes?tags={tag1}");
        Assert.Equal(new HashSet<Guid> { onlyTag1.Id, both.Id }, tag1Page.Items.Select(r => r.Id).ToHashSet());
    }

    // Visibility rule 1 (recipe-management plan): the caller's own non-public recipes
    // appear in their list; another user's Private/FriendsOnly recipes never do.
    [Fact]
    public async Task ListRecipes_NonPublicRecipes_VisibleToOwnerOnly()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var privateRecipe = await SeedRecipeAsync(ownerClient, visibility: RecipeVisibility.Private);
        var friendsOnlyRecipe = await SeedRecipeAsync(ownerClient, visibility: RecipeVisibility.FriendsOnly);
        var publicRecipe = await SeedRecipeAsync(ownerClient, visibility: RecipeVisibility.Public);

        var ownerIds = await WalkAllIdsAsync(ownerClient, "/recipes?limit=50");
        Assert.Contains(privateRecipe.Id, ownerIds);
        Assert.Contains(friendsOnlyRecipe.Id, ownerIds);
        Assert.Contains(publicRecipe.Id, ownerIds);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var otherIds = await WalkAllIdsAsync(otherClient, "/recipes?limit=50");
        Assert.DoesNotContain(privateRecipe.Id, otherIds);
        Assert.DoesNotContain(friendsOnlyRecipe.Id, otherIds);
        Assert.Contains(publicRecipe.Id, otherIds);
    }

    [Fact]
    public async Task ListRecipes_SoftDeletedRecipe_IsExcluded()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var marker = UniqueTag();

        var kept = await SeedRecipeAsync(client, tags: [marker]);
        var deleted = await SeedRecipeAsync(client, tags: [marker]);

        // No DELETE endpoint until checkpoint 05 — soft-delete the row directly.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Recipes.SingleAsync(r => r.Id == deleted.Id);
            stored.IsDeleted = true;
            stored.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var page = await GetListAsync(client, $"/recipes?tags={marker}");

        Assert.Equal([kept.Id], page.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task ListRecipes_LimitDefaultsTo20_AndValuesAboveCapClampTo50()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // 51 public recipes guarantee more than both the default and the cap exist,
        // regardless of what other tests have already seeded into the shared database.
        for (var i = 0; i < 51; i++)
        {
            await SeedRecipeAsync(client, title: $"Cap {i}");
        }

        var defaultPage = await GetListAsync(client, "/recipes");
        Assert.Equal(20, defaultPage.Items.Count);
        Assert.NotNull(defaultPage.NextCursor);

        // Above the cap: clamped silently to 50 — a 200 with 50 items, not an error.
        var clampedPage = await GetListAsync(client, "/recipes?limit=100");
        Assert.Equal(50, clampedPage.Items.Count);
        Assert.NotNull(clampedPage.NextCursor);
    }

    [Theory]
    [InlineData("!!!not-base64url!!!")] // invalid base64url characters
    [InlineData("aGVsbG8")]             // base64url("hello") — not JSON
    [InlineData("e30")]                 // base64url("{}") — missing c and i
    public async Task ListRecipes_MalformedCursor_ReturnsBadRequest(string cursor)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/recipes?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListRecipes_InvalidDifficulty_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/recipes?difficulty=Impossible");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ListRecipes_NonPositiveLimit_ReturnsBadRequest(int limit)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/recipes?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Guest access: GET /recipes is anonymous-capable — the public-only guest projection
    // is covered in GuestAccessTests.
    [Fact]
    public async Task ListRecipes_WithoutToken_ReturnsOk()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/recipes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Keyset tie-break coverage (audit 4.5): the tie branch in RecipeService
    // (r.CreatedAt == cursor.CreatedAt && r.Id.CompareTo(cursor.Id) < 0) only fires when two
    // rows share an exact CreatedAt. HTTP POSTs get distinct microsecond timestamps and never
    // collide, so the rows are seeded DIRECTLY via a DbContext scope with one identical
    // CreatedAt value (already at microsecond precision, so the DB round-trip stays equal —
    // Decisions/postgres-microsecond-timestamp-precision.md).
    [Fact]
    public async Task ListRecipes_RowsWithIdenticalCreatedAt_PaginateOrderedByIdDescWithoutSkips()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var marker = UniqueTag();

        // Microsecond-precise (no sub-microsecond ticks), so every row stores the exact same
        // instant and the keyset predicate must fall through to the Id tie-break.
        var sharedCreatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var seededIds = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < 5; i++)
            {
                var recipe = new Recipe
                {
                    Id = Guid.NewGuid(),
                    Title = $"Tie {i}",
                    Description = "Seeded with an identical CreatedAt to exercise the keyset tie-break.",
                    PrepTimeMinutes = 1,
                    CookTimeMinutes = 1,
                    Servings = 1,
                    Difficulty = DifficultyLevel.Easy,
                    Visibility = RecipeVisibility.Public,
                    CreatedAt = sharedCreatedAt,
                    CreatedByUserId = auth.UserId,
                    Ingredients = [new RecipeIngredient { Name = "water", Quantity = 1m, Unit = "cup" }],
                    Steps = [new RecipeStep { StepNumber = 1, Description = "Combine." }],
                    Tags = [marker],
                };
                db.Recipes.Add(recipe);
                seededIds.Add(recipe.Id);
            }
            await db.SaveChangesAsync();
        }

        // limit=2 forces at least three pages, so the cursor crosses the equal-CreatedAt
        // boundary twice — exactly where a missing tie-break would skip or duplicate a row.
        var walkedIds = (await WalkAllPagesAsync(client, $"/recipes?tags={marker}&limit=2"))
            .SelectMany(p => p.Items)
            .Select(r => r.Id)
            .ToList();

        // No skips, no duplicates: the walk yields exactly the seeded set.
        Assert.Equal(seededIds.OrderBy(id => id).ToList(), walkedIds.OrderBy(id => id).ToList());

        // With CreatedAt tied, the sole ordering key is Id DESC. Postgres orders uuid by its
        // 16 bytes, which equals ordinal comparison of the canonical lowercase hex ("N"), so
        // the returned sequence must be strictly descending under that comparison.
        for (var i = 1; i < walkedIds.Count; i++)
        {
            var previous = walkedIds[i - 1].ToString("N");
            var current = walkedIds[i].ToString("N");
            Assert.True(
                string.CompareOrdinal(previous, current) > 0,
                $"Expected Id DESC across the tie: {previous} should sort after {current}.");
        }
    }

    private static string UniqueTag() => $"list-{Guid.NewGuid():N}";

    private static async Task<RecipeListResponse> GetListAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<RecipeListResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("GET /recipes returned an empty body.");
    }

    /// <summary>Follows NextCursor until it is null, returning every page in order.</summary>
    private static async Task<List<RecipeListResponse>> WalkAllPagesAsync(HttpClient client, string firstPageUrl)
    {
        var pages = new List<RecipeListResponse>();
        string? cursor = null;
        do
        {
            var separator = firstPageUrl.Contains('?') ? '&' : '?';
            var url = cursor is null ? firstPageUrl : $"{firstPageUrl}{separator}cursor={Uri.EscapeDataString(cursor)}";
            var page = await GetListAsync(client, url);
            pages.Add(page);
            cursor = page.NextCursor;
            Assert.True(pages.Count <= 100, "Runaway pagination: walked more than 100 pages without a null NextCursor.");
        } while (cursor is not null);
        return pages;
    }

    private static async Task<HashSet<Guid>> WalkAllIdsAsync(HttpClient client, string firstPageUrl)
    {
        var pages = await WalkAllPagesAsync(client, firstPageUrl);
        var ids = new HashSet<Guid>();
        foreach (var item in pages.SelectMany(p => p.Items))
        {
            Assert.True(ids.Add(item.Id), $"Recipe {item.Id} appeared on more than one page.");
        }
        return ids;
    }

    private static async Task<RecipeResponse> SeedRecipeAsync(
        HttpClient client,
        string title = "List Test Recipe",
        List<string>? tags = null,
        string? cuisineType = null,
        DifficultyLevel difficulty = DifficultyLevel.Easy,
        RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "Seeded by RecipeListEndpointsTests to exercise GET /recipes.",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 10,
            Servings: 2,
            Difficulty: difficulty,
            CuisineType: cuisineType,
            CaloriesPerServing: null,
            ImageUrl: null,
            Visibility: visibility,
            Ingredients: [new RecipeIngredient { Name = "water", Quantity = 1m, Unit = "cup" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and serve." }],
            Tags: tags ?? []);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
    }
}
