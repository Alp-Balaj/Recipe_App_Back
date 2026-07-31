using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.MealPlanning.Abstractions;
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

            // Same dynamic-JSON opt-in as Program.cs/ApplicationDbContextFactory: the jsonb
            // List<> columns (Recipe.Ingredients/Steps/Tags) throw NotSupportedException at
            // SaveChangesAsync without it. A plain UseNpgsql(connectionString) is not enough.
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(_dbContainer.GetConnectionString());
            dataSourceBuilder.EnableDynamicJson();
            var dataSource = dataSourceBuilder.Build();

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
