using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Scanning.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

// POST /scan/pantry and /scan/receipt (stream N). The real FoodScanService runs against the
// container DB and its seeded ingredient catalogue; only the vision seam is faked
// (FakeFoodScanAssistant).
//
// Shared-DB class: every test registers its own user and scopes assertions to that user's
// ids. The recipes tests create ride along — matching composes the visibility policy, so
// one user's Private recipes can never leak into another test's matches.
//
// The load-bearing assertions are stream N's three promises:
//   1. a photo with no food comes back EMPTY — the scanner never invents a pantry;
//   2. an unknown detection is SURFACED as unresolved, never dropped;
//   3. neither scan persists anything but its usage row (D19) — in particular, a receipt
//      scan writes NO ShoppingListItem rows.
public class ScanEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // A valid PNG header followed by the fake's empty marker: passes the endpoint's
    // magic-byte sniff, and tells the fake to "see" no food.
    private static byte[] EmptyMarkedPng() =>
        [.. OnePixelPng, .. FakeFoodScanAssistant.EmptyMarker];

    // Exactly the 8-byte PNG magic: passes the sniff, trips the fake's failure branch.
    private static byte[] TruncatedPng() => OnePixelPng[..8];

    private static MultipartFormDataContent Photo(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", "scan.png" } };
    }

    private static async Task<PantryScanResponse> ScanPantryOkAsync(HttpClient client, byte[] bytes)
    {
        var response = await client.PostAsync("/scan/pantry", Photo(bytes));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PantryScanResponse>(TestJson.Options))!;
    }

    private static async Task<ReceiptScanResponse> ScanReceiptOkAsync(HttpClient client, byte[] bytes)
    {
        var response = await client.PostAsync("/scan/receipt", Photo(bytes));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReceiptScanResponse>(TestJson.Options))!;
    }

    private static Task<HttpResponseMessage> CreateRecipeAsync(
        HttpClient client, string title, RecipeVisibility visibility, params string[] ingredientNames)
    {
        var request = new CreateRecipeRequest(
            title, $"{title} description", 10, 20, 2, DifficultyLevel.Easy, null, null, null,
            visibility,
            ingredientNames
                .Select(n => new RecipeIngredient { Name = n, Quantity = 1m, Unit = UnitOfMeasure.Piece })
                .ToList(),
            [new RecipeStep { StepNumber = 1, Description = "Cook it." }],
            []);
        return client.PostAsJsonAsync("/recipes", request, TestJson.Options);
    }

    // ── Mode 1: pantry ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PantryScan_ResolvesKnownDetections_AndSurfacesTheUnknownOne()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ScanPantryOkAsync(client, OnePixelPng);

        Assert.Equal(FakeFoodScanAssistant.PantryNames.Length, result.Detected.Count);

        // The two catalogue staples resolve through the exact-match rule…
        var flour = result.Detected.Single(d => d.Name == "flour");
        Assert.NotNull(flour.IngredientId);
        Assert.NotNull(flour.CatalogueName);

        // …and the unknown is REPORTED with a null id, not silently dropped. Promise 2.
        var unknown = result.Detected.Single(d => d.Name == FakeFoodScanAssistant.UnknownDetection);
        Assert.Null(unknown.IngredientId);
        Assert.Null(unknown.CatalogueName);
    }

    [Fact]
    public async Task PantryScan_MatchesRecipes_WithCheckableCoverageCounts()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // Three lines, two of them "in the pantry" (fake detects flour + butter): 2 of 3.
        var created = await CreateRecipeAsync(
            client, "Shortbread of coverage", RecipeVisibility.Private, "flour", "butter", "caster sugar");
        created.EnsureSuccessStatusCode();

        var result = await ScanPantryOkAsync(client, OnePixelPng);

        var match = result.Matches.Single(m => m.Title == "Shortbread of coverage");
        Assert.Equal(2, match.MatchedIngredientCount);
        Assert.Equal(3, match.TotalIngredientCount);
        Assert.Contains("flour", match.MatchedIngredientNames);
        Assert.Contains("butter", match.MatchedIngredientNames);
        Assert.Equal(["caster sugar"], match.MissingIngredientNames);
    }

    // The key-equality branch: a recipe line the catalogue has never heard of still matches
    // a detection spelling the same thing — two honest unknowns agreeing.
    [Fact]
    public async Task PantryScan_MatchesAnUncataloguedLine_ByKeyEquality()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var created = await CreateRecipeAsync(
            client, "Haskap surprise", RecipeVisibility.Private, FakeFoodScanAssistant.UnknownDetection);
        created.EnsureSuccessStatusCode();

        var result = await ScanPantryOkAsync(client, OnePixelPng);

        var match = result.Matches.Single(m => m.Title == "Haskap surprise");
        Assert.Equal(1, match.MatchedIngredientCount);
        Assert.Equal(1, match.TotalIngredientCount);
    }

    // The corpus is RecipeVisibilityPolicy.VisibleTo — a stranger's Private recipe is not
    // something the caller "could cook", and must not surface however well it matches.
    [Fact]
    public async Task PantryScan_NeverMatchesAStrangersPrivateRecipe()
    {
        var strangerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(strangerClient);
        var created = await CreateRecipeAsync(
            strangerClient, "Stranger's private flour hoard", RecipeVisibility.Private, "flour", "butter");
        created.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ScanPantryOkAsync(client, OnePixelPng);

        Assert.DoesNotContain(result.Matches, m => m.Title == "Stranger's private flour hoard");
    }

    // Promise 1, the test that matters most: no food means an EMPTY answer, end to end —
    // no invented detections, and therefore no matches.
    [Fact]
    public async Task PhotoWithNoFood_ComesBackEmpty_NotAnInventedPantry()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ScanPantryOkAsync(client, EmptyMarkedPng());

        Assert.Empty(result.Detected);
        Assert.Empty(result.Matches);
        // Still billed: the provider read the photo and honestly found nothing.
        Assert.True(result.Budget.CallsUsed >= 1);
    }

    // ── Mode 2: receipt ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiptScan_ReturnsADraft_AndWritesNoShoppingListRows()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var result = await ScanReceiptOkAsync(client, OnePixelPng);

        Assert.Equal(FakeFoodScanAssistant.ReceiptItems.Length, result.Items.Count);
        Assert.Equal("Whole milk", result.Items[0].Name);
        // Free text as printed — never parsed into a typed quantity.
        Assert.Equal("2 x 1L", result.Items[0].Quantity);
        Assert.Null(result.Items[2].Quantity);

        // Promise 3: the draft is the whole product. AddManualAsync (POST /shopping-list)
        // stays this table's only writer, and confirming is the USER's action.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.ShoppingListItems.AnyAsync(i => i.UserId == auth.UserId));
    }

    [Fact]
    public async Task ConfirmedDraftRow_LandsThroughTheExistingManualAdd()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var draft = await ScanReceiptOkAsync(client, OnePixelPng);

        // What the SPA does on confirm: one POST /shopping-list per accepted line, into a
        // UTC-midnight Monday week. Round-tripped here to pin the two contracts together.
        var monday = DateTime.UtcNow.Date;
        while (monday.DayOfWeek != DayOfWeek.Monday)
        {
            monday = monday.AddDays(-1);
        }

        var response = await client.PostAsJsonAsync("/shopping-list", new
        {
            Ingredient = draft.Items[0].Name,
            Quantity = draft.Items[0].Quantity,
            WeekStartDate = monday,
        }, TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Shared surface ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/scan/pantry")]
    [InlineData("/scan/receipt")]
    public async Task Scan_RequiresAuthentication(string path)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync(path, Photo(OnePixelPng));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The same magic-byte sniff an upload gets, BEFORE the provider — a mislabelled file is
    // a free 400 here, not a paid vision call.
    [Theory]
    [InlineData("/scan/pantry")]
    [InlineData("/scan/receipt")]
    public async Task Scan_RejectsAFileThatIsNotAnImage(string path)
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00];
        var response = await client.PostAsync(path, Photo(gif, "image/gif"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scan_RejectsARequestWithNoFile()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var content = new MultipartFormDataContent { { new StringContent("nope"), "note" } };
        var response = await client.PostAsync("/scan/pantry", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ScannerFailure_Is502_AndBillsNothing()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/scan/pantry", Photo(TruncatedPng()));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AiUsageRecords.AnyAsync(u => u.UserId == auth.UserId));
    }

    // ── Metering — gated like Import's photo tier, and EVERY scan spends ────────────────

    [Fact]
    public async Task EachScan_RecordsOneUsageRow_OnTheFoodScanLane()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var pantry = await ScanPantryOkAsync(client, OnePixelPng);
        Assert.Equal(1, pantry.Budget.CallsUsed);

        var receipt = await ScanReceiptOkAsync(client, OnePixelPng);
        Assert.Equal(2, receipt.Budget.CallsUsed);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.AiUsageRecords.Where(u => u.UserId == auth.UserId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("food-scan", r.Lane);
            Assert.Equal(FakeFoodScanAssistant.Usage.TotalTokens, r.TotalTokens);
        });
    }

    [Fact]
    public async Task ExhaustedBudget_Is429_BeforeAnyMoneyMoves()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // Put the user at the token ceiling with a single seeded row, the same shape the
        // chat lane's exhaustion test uses.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AiUsageRecords.Add(new AiUsageRecord
            {
                Id = Guid.NewGuid(),
                UserId = auth.UserId,
                Lane = "food-scan",
                PromptTokens = 200_000,
                CompletionTokens = 50_000,
                TotalTokens = 250_000,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync("/scan/pantry", Photo(OnePixelPng));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // Refused BEFORE the provider call: the seeded row is still the only one.
        using var assertScope = factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await assertDb.AiUsageRecords.CountAsync(r => r.UserId == auth.UserId));
    }
}
