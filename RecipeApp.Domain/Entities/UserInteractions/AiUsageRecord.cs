namespace RecipeApp.Domain.Entities;

// One AI provider call made on behalf of one user (ai-quotas, Stream B). The per-user daily
// quota and the remaining-budget indicator are both computed by aggregating these rows over
// the current UTC day — the row is the accounting record, never mutated after insert.
public class AiUsageRecord
{
    public Guid Id { get; set; }

    // Which AI lane spent the tokens ("chat" today; a future vision or generation lane
    // records its own value so per-feature cost stays attributable).
    public string Lane { get; set; } = null!;

    // Provider-reported token counts for this call. Zero when the provider omitted usage
    // metadata — the call itself still counts against the daily call quota.
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    // Provider-reported total. On Gemini 3.x this includes thinking tokens, so it can
    // legitimately exceed PromptTokens + CompletionTokens — always sum THIS column.
    public int TotalTokens { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Owner
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}
