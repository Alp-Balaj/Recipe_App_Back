namespace RecipeApp.Application.Chat.Abstractions;

// Per-user daily AI accounting (ai-quotas, Stream B). Today the only thing between a user and
// the Gemini bill is a per-IP per-minute rate limit; this service adds the per-user layer —
// every AI call is recorded with its token cost, and callers consult the budget BEFORE spending
// money. Enforcement is deliberately a soft gate: two in-flight calls can both pass the check
// (there is no lock), which at worst overshoots a daily budget by one call.
public interface IAiUsageService
{
    // The caller's budget for the current UTC day: limits from configuration, usage aggregated
    // from the recorded calls. Cheap (one aggregate query) — called once before and once after
    // each AI call.
    Task<AiBudget> GetBudgetAsync(Guid userId, CancellationToken cancellationToken = default);

    // Stages a usage row on the current unit of work WITHOUT saving — the caller's own
    // SaveChanges commits it, so the accounting lands atomically with whatever the call
    // produced (a chat turn persists with its cost, a failed turn persists neither).
    // A null usage records zeros: the call still counts against the daily call quota.
    void RecordCall(Guid userId, string lane, ChatTokenUsage? usage);
}

// Lane identifiers for AiUsageRecord.Lane. One constant per AI feature so per-feature spend
// stays attributable when future lanes (plan generation, vision) start recording.
public static class AiUsageLanes
{
    public const string Chat = "chat";

    // stream E: the recipe generator. Added exactly as this comment anticipated — a new
    // constant for a new feature. Nothing else about the quota system is touched by E:
    // the generator consults GetBudgetAsync before spending and stages RecordCall on its
    // own unit of work, like the chat lane does.
    public const string RecipeGeneration = "recipe-generation";

    // The week proposer. Added 2026-08-05, six days after the lane started spending: stream C
    // shipped propose-week before this system existed, stream B metered the two lanes that
    // existed when it landed, and nothing connected them — so the MOST expensive call in the
    // app (a 50-candidate grounding prompt filling 21 slots against an 8192-token ceiling)
    // was the one call the budget could not see. Under-reporting the ceiling is the part that
    // matters: the remaining-budget figure the chat surface shows was wrong for every user who
    // had proposed a week.
    public const string MealPlanProposal = "meal-plan-proposal";

    // Stream M: the cook-mode assistant, and the first lane with no row of its
    // own anywhere else in the database. Decision D14 keeps a cook-mode exchange session-scoped
    // — no Conversation, no ChatMessage, nothing persisted but this usage row — because
    // ChatService.BuildHistoryAsync feeds a conversation's trailing 20 messages back into the
    // recommender's grounding, and "can I use margarine instead" is not a thing anyone wants
    // shaping next week's suggestions. That is precisely why the lane has to exist: metering is
    // the ONLY durable trace of a cook-mode turn, so without a constant here the app's most
    // casually-repeated AI call would be the one call the budget cannot see. Stream C shipped
    // propose-week into exactly that hole; this is the same mistake declined in advance.
    public const string CookAssistant = "cook-assistant";

    // Stream X: the content-moderation classifier. This lane is unlike the three above in a
    // way that matters more than its name.
    //
    // Every other lane bills the person who pressed the button, and gates on their budget
    // BEFORE spending — GetBudgetAsync(...).IsExhausted returns 429 and no money is spent.
    // A moderation pass is spent on the platform's behalf against content someone ELSE wrote,
    // so there is no such person to gate on. Naively reusing the pre-call gate against the
    // content's author would let a user disable moderation of their own content by exhausting
    // their own quota — the failure mode is exactly backwards, because the author of the
    // content most worth checking is the one most likely to be hammering the API.
    //
    // So: the spend is RECORDED against SystemUsers.ModerationId so it stays attributable and
    // shows up in the same accounting as everything else, and NOTHING is gated on it. The
    // ceiling that keeps this lane bounded is the queue's capacity, not a budget — see
    // ContentModerationQueue. Note the consequence for anyone reading the numbers later: the
    // moderation user's daily "budget" will read as wildly exhausted and that is meaningless,
    // because no code path ever consults it.
    public const string ContentModeration = "content-moderation";

    // Stream L: recipe import — the LLM fallback for a page with no structured data, and the
    // whole photo tier. Gated like every user-facing lane (GetBudgetAsync before the call,
    // RecordCall staged on the same unit of work as the recipe's SaveChanges), NOT like the
    // ContentModeration lane above. The distinction that comment draws is exactly the one that
    // applies: moderation is spent on the platform's behalf against content nobody asked to
    // have checked, whereas an import is a button a user pressed about a page they chose. A
    // person who can afford it gets it; a person out of quota is told so before any money
    // moves.
    //
    // WHAT MAKES THIS LANE UNLIKE THE OTHER FOUR: most imports never touch it. The JSON-LD
    // path is deterministic and free, so a lane row exists only for the fallback and photo
    // cases — which means the number here is a measure of how often structured data was
    // MISSING, not of how often people imported. Anyone reading these figures later to size
    // import's popularity will undercount it, badly, and should count the recipes carrying a
    // SourceUrl instead.
    public const string Import = "recipe-import";

    // Stream N: the food scanner, both modes (pantry photo → detections, receipt photo →
    // draft list) — the seventh lane. Gated like Import's photo tier (GetBudgetAsync before
    // the call → 429, RecordCall staged on the same unit of work), NOT like the ungated
    // ContentModeration exception above: a scan is a button a user pressed.
    //
    // Unlike Import, EVERY scan spends — there is no deterministic free path through a
    // photograph — so this lane's figures mean exactly what they appear to mean: one row
    // per scan, and counting rows here IS counting scans. Import's comment warns the
    // opposite about itself; do not carry that caveat over.
    public const string FoodScan = "food-scan";
}

// A user's AI budget for one UTC day. The window is the calendar day in UTC — simple to
// reason about, and ResetsAtUtc tells the frontend exactly when the counters clear.
// Exhaustion is calls OR tokens: the call limit is the deterministic pre-call gate, the token
// limit backstops unusually expensive calls (tokens are only known after a call returns, so a
// user can cross the token line once — the next call is what gets refused).
public record AiBudget(
    int DailyCallLimit,
    int CallsUsed,
    long DailyTokenLimit,
    long TokensUsed,
    DateTime ResetsAtUtc)
{
    public int CallsRemaining => Math.Max(0, DailyCallLimit - CallsUsed);
    public long TokensRemaining => Math.Max(0, DailyTokenLimit - TokensUsed);
    public bool IsExhausted => CallsUsed >= DailyCallLimit || TokensUsed >= DailyTokenLimit;
}
