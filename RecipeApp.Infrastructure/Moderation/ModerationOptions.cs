namespace RecipeApp.Infrastructure.Moderation;

// Stream X. Config section "Moderation". Bound eagerly with code defaults, like AiQuotaOptions
// — no secret is involved, and every value here needs a sensible answer on a fresh checkout.
public sealed class ModerationOptions
{
    public const string SectionName = "Moderation";

    /// <summary>
    /// Master switch. Off leaves the create paths byte-identical to before this stream: the
    /// queue accepts and discards, and the worker never starts. This is what makes the whole
    /// feature safe to disable in an incident without a deploy that touches the write paths.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many pending items the channel holds. The queue is bounded and the overflow is
    /// DROPPED rather than queued or awaited: an unbounded queue trades a moderation backlog
    /// for a memory leak, and a blocking one trades it for slow writes — and slow writes are
    /// the one thing this stream exists to prevent. 1000 is far above any burst this app
    /// produces, so hitting it means something is wrong and the log line will say so.
    /// </summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>
    /// The confidence at or above which a verdict becomes a Report. Deliberately low, because
    /// a flag opens a queue item and never removes anything: the model carries recall, the
    /// human carries precision. Raising this trades unmoderated content for a shorter queue.
    /// </summary>
    public double MinimumConfidence { get; set; } = 0.6;

    /// <summary>
    /// Per-item ceiling on the provider call. A hung classifier must not wedge the single
    /// worker loop behind it forever — the item is abandoned and the loop moves on.
    /// </summary>
    public int ClassificationTimeoutSeconds { get; set; } = 30;
}
