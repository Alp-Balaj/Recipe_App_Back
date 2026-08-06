using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Moderation.Abstractions;

// Stream X. The out-of-band seam, and the first background execution seam in this backend —
// before it there was no BackgroundService, no IHostedService, no Channel and no Task.Run
// outside the test projects. Every AI call in the app is a user waiting on a request; this is
// the one that is not, and streams L and N are expected to reach for the same shape.
//
// The load-bearing property is negative: a classifier that is slow, rate-limited, over budget
// or simply down must not be able to fail, delay, or roll back a create. That is why the only
// method here is synchronous, returns a bool instead of throwing, and hands back a value the
// caller is free to ignore. There is deliberately no EnqueueAsync — an awaitable enqueue is an
// await on the create path, which is the exact coupling this interface exists to prevent.
public interface IContentModerationQueue
{
    /// <summary>
    /// Offers one piece of content for classification and returns immediately. Never blocks,
    /// never throws, and never touches the database. Returns <c>false</c> when the queue is
    /// full and the item was dropped — dropping is the designed behaviour under load, because
    /// an unbounded queue would trade a moderation backlog for a memory leak, and a blocking
    /// one would trade it for slow writes.
    /// </summary>
    bool TryEnqueue(ModerationWorkItem item);
}

/// <summary>
/// A pointer, never a payload. The worker re-reads the content by id inside its own DI scope:
/// the enqueuing request's DbContext is disposed the moment its response is written, so a
/// tracked entity carried across that boundary would be attached to a dead context. Re-reading
/// also gives the right behaviour for free when content is edited or deleted between the
/// enqueue and the classification — the worker sees the current row, or no row at all.
/// </summary>
/// <param name="TargetType">Recipe or Comment. User is not classifiable content.</param>
/// <param name="TargetId">The row to re-read.</param>
public record ModerationWorkItem(ReportTargetType TargetType, Guid TargetId);
