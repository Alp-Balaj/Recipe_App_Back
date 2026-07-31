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
