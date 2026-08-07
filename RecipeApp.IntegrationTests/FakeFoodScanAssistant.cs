using System.Text;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Scanning.Abstractions;

namespace RecipeApp.IntegrationTests;

// Stream N. Deterministic stand-in for the Gemini-backed food scanner, so CI never makes a
// paid vision call on either scan mode.
//
// A PURE FUNCTION of its inputs, like every fake in this project (see
// FakeRecipeExtractionAssistant's note on why a call counter is the wrong instrument under
// the shared TestServer). The photo BYTES are the input, so the sentinels ride inside them:
// the endpoint's magic-byte sniff only reads the header, which leaves the rest of the file
// free to carry a marker. A bare 8-byte PNG magic simulates provider failure — the same
// truncated-image trick L's fake uses.
public sealed class FakeFoodScanAssistant : IFoodScanAssistant
{
    /// <summary>Append after a valid image header to make either scan return empty.</summary>
    public static readonly byte[] EmptyMarker = Encoding.ASCII.GetBytes("__NO_FOOD__");

    // An image this small cannot contain anything, which is how failure is signalled
    // without mutable state.
    private const int FailingImageByteCount = 8;

    /// <summary>
    /// What every non-sentinel pantry photo "contains". Two catalogue staples the seeded
    /// catalogue resolves, plus one honest unknown it cannot.
    /// </summary>
    public static readonly string[] PantryNames = ["flour", "butter", "haskap berries"];

    /// <summary>The unknown above, for asserting the unresolved path by name.</summary>
    public const string UnknownDetection = "haskap berries";

    /// <summary>What every non-sentinel receipt photo "prints".</summary>
    public static readonly (string Name, string? Quantity)[] ReceiptItems =
    [
        ("Whole milk", "2 x 1L"),
        ("Eggs", "12"),
        ("Cheddar", null),
    ];

    // Non-zero so the usage row is visibly real and the budget actually moves.
    public static ChatTokenUsage Usage => new(150, 60, 210);

    public Task<PantryDetection> DetectPantryAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(images);

        return Task.FromResult(IsEmptySentinel(images)
            ? new PantryDetection([], Usage)
            : new PantryDetection([.. PantryNames], Usage));
    }

    public Task<ReceiptRead> ReadReceiptAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(images);

        return Task.FromResult(IsEmptySentinel(images)
            ? new ReceiptRead([], Usage)
            : new ReceiptRead(
                ReceiptItems.Select(i => new ReceiptLine(i.Name, i.Quantity)).ToList(), Usage));
    }

    private static void ThrowIfFailing(IReadOnlyList<RecipeImageContent> images)
    {
        if (images.Count == 0 || images[0].Content.Length <= FailingImageByteCount)
        {
            throw new InvalidOperationException("Simulated vision scanner failure.");
        }
    }

    private static bool IsEmptySentinel(IReadOnlyList<RecipeImageContent> images) =>
        images[0].Content.AsSpan().IndexOf(EmptyMarker) >= 0;
}
