using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>
/// One row of the ingredient catalogue as it is written to the seed file.
///
/// The id is derived from the FDC id (see DeterministicId), which is the promise that lets
/// the catalogue be regenerated at all: rebuilding keeps every id it had, so the
/// <c>RecipeIngredient.IngredientId</c> values already sitting in jsonb do not orphan.
/// </summary>
public sealed record SeedIngredient(
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

/// <summary>A spelling, and the catalogue row it means. MatchKey is the primary key.</summary>
public sealed record SeedAlias(string MatchKey, Guid IngredientId);

/// <summary>
/// The whole seed document, exactly as <c>RecipeApp.Infrastructure/Seed/ingredients.seed.json</c>
/// carries it.
///
/// It is a first-class type rather than a shape known only to the build path because the
/// propose and merge modes have to READ the committed seed. They cannot rebuild it: doing
/// that needs the FoodData Central bulk export, which is deliberately not in the repository,
/// so a loop that could only close "on the next full build" would never close at all on a
/// machine that has the recipes but not the CSVs.
/// </summary>
public sealed record SeedFile(
    string GeneratedFrom,
    List<SeedIngredient> Ingredients,
    List<SeedAlias> Aliases,
    List<string> UnresolvedCorpusNames);

public static class SeedDocument
{
    // WriteIndented and null-skipping match what the build path has always emitted, so a
    // merge produces a diff of the lines it changed rather than of the whole file.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<SeedFile> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SeedFile>(stream, Options, ct)
               ?? throw new InvalidOperationException($"{path} did not contain a seed document.");
    }

    public static async Task WriteAsync(string path, SeedFile seed, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(seed, Options), ct);
    }
}
