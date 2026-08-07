namespace RecipeApp.Application.Scanning;

// Stream N's outcome type, a sibling of RecipeImportOutcome for the reason that one is a
// sibling of RecipeGenerationOutcome: widening a shared enum would add unhandled cases to
// every existing endpoint switch. It is DELIBERATELY smaller than import's — there is no
// caller-named address here, so the two fetch failures have no scanner equivalent, and a
// photo with nothing recognisable in it is a SUCCESS carrying an empty list rather than a
// failure. Inventing a NothingDetected outcome would have turned the scanner's most
// important honest answer into an error code.
public enum FoodScanOutcome
{
    Success,

    // The vision call failed or returned something unsalvageable. Nothing billed — the same
    // funnel every other AI lane uses.
    AssistantUnavailable,

    // Refused BEFORE the model call. Unlike import, EVERY scan spends — there is no
    // deterministic free path through a photograph — so this gate has no branch it skips.
    QuotaExceeded,
}

public record FoodScanResult<T>(FoodScanOutcome Outcome, T? Value)
{
    public static FoodScanResult<T> Success(T value) => new(FoodScanOutcome.Success, value);

    public static FoodScanResult<T> AssistantUnavailable() =>
        new(FoodScanOutcome.AssistantUnavailable, default);

    public static FoodScanResult<T> QuotaExceeded() => new(FoodScanOutcome.QuotaExceeded, default);
}
