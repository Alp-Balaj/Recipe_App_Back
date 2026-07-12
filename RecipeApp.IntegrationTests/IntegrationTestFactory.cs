using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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

        builder.ConfigureServices(services =>
        {
            var dbContextOptionsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (dbContextOptionsDescriptor is not null)
            {
                services.Remove(dbContextOptionsDescriptor);
            }

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
    }
}
