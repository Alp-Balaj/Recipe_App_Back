using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Domain.Entities;

namespace RecipeApp.Infrastructure.Persistence;

/// <summary>
/// Loads the committed ingredient catalogue into the database (stream G, slice G2).
///
/// The seed file is an EMBEDDED RESOURCE, not a loose file next to the binary: the API
/// is published into a container, and a file the build has to remember to copy is a file
/// that eventually is not there. Embedding makes "the catalogue shipped" a compile-time
/// fact.
///
/// Why a seeder and not INSERTs inside the migration: 1,500 ingredients and 2,756
/// aliases as generated SQL would be a ~700 KB migration, unreadable in review and
/// impossible to correct without a second migration. This runs at startup, is
/// idempotent, and re-running it after a regenerated seed file UPDATES rows in place
/// — which is what keeps ids stable for the RecipeIngredient.IngredientId values that
/// point at them.
///
/// It never DELETES. A catalogue row that disappeared from a later seed would orphan
/// every recipe that resolved to it, and those ids live inside jsonb where no foreign
/// key can protect them.
/// </summary>
public class IngredientCatalogueSeeder
{
    private const string ResourceName = "RecipeApp.Infrastructure.Seed.ingredients.seed.json";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<IngredientCatalogueSeeder> _logger;

    public IngredientCatalogueSeeder(ApplicationDbContext db, ILogger<IngredientCatalogueSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var seed = ReadSeed();
        if (seed is null)
        {
            _logger.LogError("Ingredient seed resource {Resource} is missing — the catalogue will be empty.", ResourceName);
            return;
        }

        var existingIngredients = await _db.Ingredients
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        var added = 0;
        var updated = 0;

        foreach (var row in seed.Ingredients)
        {
            if (existingIngredients.TryGetValue(row.Id, out var entity))
            {
                updated++;
            }
            else
            {
                entity = new Ingredient { Id = row.Id };
                _db.Ingredients.Add(entity);
                added++;
            }

            entity.Name = row.Name;
            entity.Category = row.Category;
            entity.GramsPerMillilitre = row.GramsPerMillilitre;
            entity.GramsPerPiece = row.GramsPerPiece;
            entity.Kcal = row.Kcal;
            entity.ProteinG = row.ProteinG;
            entity.FatG = row.FatG;
            entity.CarbsG = row.CarbsG;
            entity.FibreG = row.FibreG;
            entity.FdcId = row.FdcId;
        }

        // Ingredients are saved BEFORE aliases: an alias row carries a foreign key to an
        // ingredient, and a single SaveChanges would leave EF to work the order out from
        // the graph it can see — which it cannot do for rows it is only updating.
        await _db.SaveChangesAsync(cancellationToken);

        var existingAliases = await _db.IngredientAliases
            .ToDictionaryAsync(a => a.MatchKey, StringComparer.Ordinal, cancellationToken);

        var aliasesAdded = 0;
        var aliasesRepointed = 0;

        foreach (var row in seed.Aliases)
        {
            if (existingAliases.TryGetValue(row.MatchKey, out var alias))
            {
                // A key that now belongs to a different ingredient. Rare, and worth
                // following: it means the seed builder's collision resolution changed
                // its mind, so recipes resolved under the old answer now mean something
                // else. Counted rather than logged per row — there could be hundreds.
                if (alias.IngredientId != row.IngredientId)
                {
                    alias.IngredientId = row.IngredientId;
                    aliasesRepointed++;
                }
                continue;
            }

            _db.IngredientAliases.Add(new IngredientAlias
            {
                MatchKey = row.MatchKey,
                IngredientId = row.IngredientId,
            });
            aliasesAdded++;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Ingredient catalogue seeded: {Added} added, {Updated} updated, {AliasesAdded} aliases added, {Repointed} aliases repointed.",
            added, updated, aliasesAdded, aliasesRepointed);
    }

    private static SeedFile? ReadSeed()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }

    // Mirrors tools/IngredientSeedBuilder's output. Kept private and minimal: the
    // seed file also carries UnresolvedCorpusNames, which is a report for a human and
    // has no meaning at runtime.
    private sealed record SeedFile(List<SeedIngredient> Ingredients, List<SeedAlias> Aliases);

    private sealed record SeedIngredient(
        Guid Id,
        string Name,
        string Category,
        double? GramsPerMillilitre,
        double? GramsPerPiece,
        double? Kcal,
        double? ProteinG,
        double? FatG,
        double? CarbsG,
        double? FibreG,
        int FdcId);

    private sealed record SeedAlias(string MatchKey, Guid IngredientId);
}
