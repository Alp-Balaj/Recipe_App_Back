namespace RecipeApp.Infrastructure.Chat;

// Per-user daily AI quota configuration (ai-quotas, Stream B). Mirrors the GeminiOptions
// `const string SectionName` precedent; bound in ChatServiceCollectionExtensions and
// overridable via AiQuota:DailyCallLimit / AiQuota:DailyTokenLimit (or the __ env-var forms).
//
// These were PROPOSALS pending decision D7. D7 was decided on 2026-08-05: CONFIRMED AS
// SHIPPED — the numbers below are the real ceiling, not a placeholder, and no code change
// followed beyond this comment.
//   * 50 calls/day — roomy for a person actually using the assistant (a long planning session
//     is maybe 20 turns), stingy for a script hammering the endpoint.
//   * 250k tokens/day — a typical turn measures ~3-5k total tokens (50-candidate grounding
//     prompt + Gemini 3.x thinking + the small structured reply), so this backstops ~50 calls
//     at the observed shape and trips earlier only if calls get unusually expensive.
// At Gemini Flash list prices 250k tokens is under a cent per user-day, so the ceiling is
// about abuse containment, not shaving normal use.
//
// The ceiling only became HONEST later the same day, when propose-week was metered: until
// then the most expensive call in the app spent against no budget at all, so the
// remaining-allowance figure under-reported for anyone who had proposed a week.
public sealed class AiQuotaOptions
{
    public const string SectionName = "AiQuota";

    public int DailyCallLimit { get; set; } = 50;

    public long DailyTokenLimit { get; set; } = 250_000;
}
