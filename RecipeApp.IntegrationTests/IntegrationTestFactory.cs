using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace RecipeApp.IntegrationTests;

public class IntegrationTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("recipeapp_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    // social-feed cp4: each factory gets its own throwaway image-storage root so uploads
    // never land in the repo tree (the production default is <ContentRoot>/uploads/images).
    // Deleted recursively in DisposeAsync.
    private readonly string _imageStorageRoot = Path.Combine(
        Path.GetTempPath(), $"recipeapp-tests-images-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs fails fast when these are missing. They moved out of appsettings.json
        // into user-secrets (audit fix 1), which don't exist in CI — so the tests must be
        // self-contained and inject both here. The host builds lazily on first Services
        // access (after the container has started), so GetConnectionString() is safe; the
        // resulting DbContext registration is replaced below anyway. The signing key is a
        // fixed test-only value (>= 32 bytes for HS256), deliberately NOT a secret.
        builder.UseSetting("ConnectionStrings:DefaultConnection", _dbContainer.GetConnectionString());
        builder.UseSetting("Jwt:Key", "integration-tests-signing-key-not-a-secret-0123456789abcdef");

        // Every TestServer request has a null RemoteIpAddress, so they all fall into the one
        // "unknown" rate-limit partition. The suite makes far more than the production 10/min
        // of auth calls per host, so raise the /auth permit limit out of the way here — the
        // 429 behaviour is verified live, not under the shared TestServer (audit 4.5 note).
        builder.UseSetting("RateLimiting:AuthPermitLimit", "1000000");
        // Same reasoning for the chat lane (chat-ai cp03): all TestServer requests collapse into
        // the one "unknown" rate-limit partition, so raise the chat budget out of the way and
        // verify the real 429 live instead.
        builder.UseSetting("RateLimiting:ChatPermitLimit", "1000000");
        // And the social lane (social-feed cp1) for the same reason.
        builder.UseSetting("RateLimiting:SocialPermitLimit", "1000000");
        // And the images lane (social-feed cp4) — the real 20/min 429 is verified live.
        builder.UseSetting("RateLimiting:ImagesPermitLimit", "1000000");
        // And the meal lane (meal-planning cp02) — shared by cp02–04, same reasoning.
        builder.UseSetting("RateLimiting:MealPermitLimit", "1000000");
        // And the import lane (stream L). Its production budget is the tightest in the app
        // (10/min), which the suite would trip within a single test class — the real 429 is
        // verified live, like every other lane's.
        builder.UseSetting("RateLimiting:ImportPermitLimit", "1000000");

        // social-feed cp4: point IImageStorage (and the /images static-file mount) at the
        // per-factory temp root above instead of the repo tree.
        builder.UseSetting("ImageStorage:RootPath", _imageStorageRoot);

        builder.ConfigureServices(services =>
        {
            var dbContextOptionsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (dbContextOptionsDescriptor is not null)
            {
                services.Remove(dbContextOptionsDescriptor);
            }

            // Replace the Gemini-backed IChatAssistantService with a deterministic fake so CI
            // never needs a Gemini key or makes a real (paid) API call. The real ChatService
            // (IChatService) under test resolves this fake. (This also means the factory-lambda
            // GeminiMessageCaller registration is never invoked, so no key check fires.)
            services.RemoveAll<IChatAssistantService>();
            services.AddScoped<IChatAssistantService, FakeChatAssistantService>();

            // Same for the meal-plan proposal lane (Stream C): the real MealPlanProposalService
            // under test resolves a deterministic IMealPlanAssistantService instead of Gemini.
            services.RemoveAll<IMealPlanAssistantService>();
            services.AddScoped<IMealPlanAssistantService, FakeMealPlanAssistantService>();

            // Same for the recipe generator (stream E): the real RecipeGenerationService
            // under test — quota gate, provenance, persistence, and the deliberate ABSENCE
            // of a rank award — runs against the container DB with a deterministic draft
            // instead of Gemini.
            services.RemoveAll<IRecipeGenerationAssistant>();
            services.AddScoped<IRecipeGenerationAssistant, FakeRecipeGenerationAssistant>();

            // Same for the cook-mode assistant (stream M): the real CookAssistantService under
            // test — visibility gate, budget gate, server-side serving scaling, and the usage
            // row that is the ONLY thing a session-scoped turn persists — runs against the
            // container DB with a deterministic answer instead of Gemini.
            services.RemoveAll<ICookAssistant>();
            services.AddScoped<ICookAssistant, FakeCookAssistant>();

            // Stream X: the moderation classifier. Unlike the three fakes above this one is
            // not optional — ContentModerationWorker is a HOSTED SERVICE that starts with this
            // host and drains the queue for real, so without this replacement every recipe and
            // comment the suite creates would reach for the provider (and the Gemini key check)
            // on a background thread. The real queue and the real worker stay in place: what
            // the suite exercises end-to-end is the out-of-band path itself.
            services.RemoveAll<IContentModerationClassifier>();
            services.AddScoped<IContentModerationClassifier, FakeContentModerationClassifier>();

            // Stream L: BOTH of import's outside-world seams, and they are replaced for two
            // different reasons.
            //
            // The fetcher, because the real one makes an OUTBOUND HTTP REQUEST to whatever
            // address the test names. That is unlike every other fake here — the rest exist to
            // avoid a paid API call, this one exists to stop the suite reaching the internet
            // at all. Leaving it in place would make the tests depend on a third-party site
            // being up and on the CI box having egress.
            //
            // The extraction assistant, for the usual reason: it is the Gemini seam for both
            // model-backed paths. Faking it here rather than the two provider callers beneath
            // it means IVisionMessageCaller's factory lambda is never resolved, so its API-key
            // check never fires — the same lazy-resolution property that lets the chat seam
            // stay unconfigured in CI.
            services.RemoveAll<IRecipePageFetcher>();
            services.AddScoped<IRecipePageFetcher, FakeRecipePageFetcher>();

            services.RemoveAll<IRecipeExtractionAssistant>();
            services.AddScoped<IRecipeExtractionAssistant, FakeRecipeExtractionAssistant>();

            // Built through the shared configurator, exactly like Program.cs and the
            // design-time factory. Two things ride on that: the dynamic-JSON opt-in (the jsonb
            // List<> columns throw NotSupportedException at SaveChangesAsync without it) and
            // the string-enum converter. The second is why the tests must not hand-roll their
            // own builder — a test DB that writes integer enums into jsonb would round-trip
            // its own writes happily and prove nothing about production's encoding.
            var dataSource = RecipeAppDataSource.Build(_dbContainer.GetConnectionString());

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(dataSource));
        });
    }

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();

        // The ingredient catalogue (stream G, slice G2). Seeded HERE rather than relied
        // on from Program.cs: the host is built before this method runs, so the startup
        // seeder fires against a database that has no tables yet, logs its failure and
        // returns. Without this the catalogue would be empty for every test and the
        // resolver would appear to work while resolving nothing.
        await scope.ServiceProvider.GetRequiredService<IngredientCatalogueSeeder>().SeedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        await base.DisposeAsync();

        if (Directory.Exists(_imageStorageRoot))
        {
            Directory.Delete(_imageStorageRoot, recursive: true);
        }
    }
}
