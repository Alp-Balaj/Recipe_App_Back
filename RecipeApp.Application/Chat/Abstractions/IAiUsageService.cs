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

    // Stream M: the cook-mode assistant, the fourth lane and the first one with no row of its
    // own anywhere else in the database. Decision D14 keeps a cook-mode exchange session-scoped
    // — no Conversation, no ChatMessage, nothing persisted but this usage row — because
    // ChatService.BuildHistoryAsync feeds a conversation's trailing 20 messages back into the
    // recommender's grounding, and "can I use margarine instead" is not a thing anyone wants
    // shaping next week's suggestions. That is precisely why the lane has to exist: metering is
    // the ONLY durable trace of a cook-mode turn, so without a constant here the app's most
    // casually-repeated AI call would be the one call the budget cannot see. Stream C shipped
    // propose-week into exactly that hole; this is the same mistake declined in advance.
    public const string CookAssistant = "cook-assistant";
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
