using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>
/// One proposed alias, awaiting a human.
///
/// <see cref="Accepted"/> is a THREE-state field and that is the design: null means nobody
/// has looked yet, which is what the propose mode writes and what the merge mode ignores.
/// There is no auto-accept, because an accepted alias sets ingredient IDENTITY — the thing
/// IngredientKey's prohibition on fuzzy matching exists to protect. The proposer is allowed
/// to be as clever as it likes precisely because it cannot land anything on its own.
///
/// <see cref="MatchKey"/> is shown so a reviewer can see what would actually be written
/// rather than having to run IngredientKey in their head. It is DISPLAY, not authority:
/// merge recomputes it from <see cref="Name"/> and rejects the row if the two disagree.
/// </summary>
public sealed record AliasProposal(
    string Name,
    string MatchKey,
    string ProposedIngredient,
    Guid? ProposedIngredientId,
    string Confidence,
    string Why,
    bool? Accepted);

/// <summary>
/// The review file: <c>tools/IngredientSeedBuilder/AliasProposals.review.json</c>, checked in
/// so the gate is a reviewable diff rather than a prompt nobody sees.
/// </summary>
public sealed record AliasProposalFile(
    string GeneratedFrom,
    string HowToUse,
    List<AliasProposal> Proposals);

public static class AliasProposals
{
    public const string Instructions =
        "Set \"Accepted\": true on the rows you want, false on the ones you do not, and leave " +
        "null on anything you have not decided. Then run the merge mode. Only true lands, and " +
        "a MatchKey the catalogue already owns is refused even when accepted.";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static async Task<AliasProposalFile> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AliasProposalFile>(stream, Options, ct)
               ?? throw new InvalidOperationException($"{path} did not contain a proposal document.");
    }

    public static async Task WriteAsync(string path, AliasProposalFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, Options), ct);
    }
}
