using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace RecipeApp.Infrastructure.Persistence;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            // RecipeApp.API's UserSecretsId (see RecipeApp.API.csproj): the connection string
            // moved out of appsettings.json into user-secrets (audit fix 1). Env vars still win.
            .AddUserSecrets("9fa723d4-22a1-4b7c-821b-217268685cb5")
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection not configured — set via user-secrets or ConnectionStrings__DefaultConnection env var.");

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseNpgsql(RecipeAppDataSource.Build(connectionString));

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}