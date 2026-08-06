using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Moderation.Abstractions;

namespace RecipeApp.Infrastructure.Moderation;

// Stream X. The in-process work queue behind IContentModerationQueue, and half of the first
// background execution seam this backend has ever had.
//
// In-process and non-durable is a deliberate choice, not an oversight. A restart loses the
// pending items, and the honest consequence is that some content goes unclassified — which is
// acceptable precisely because this system only ever ADDS a review item and never removes
// content, so losing one is a missed catch rather than a corrupted state. The alternative (an
// outbox table polled by the worker) buys durability at the cost of a second migration and a
// write on every create path, and the create path is what this stream must not touch. If L or
// N needs durability, that is the upgrade — and it fits behind this same interface.
public sealed class ContentModerationQueue : IContentModerationQueue
{
    private readonly Channel<ModerationWorkItem> _channel;
    private readonly ModerationOptions _options;
    private readonly ILogger<ContentModerationQueue> _logger;

    public ContentModerationQueue(ModerationOptions options, ILogger<ContentModerationQueue> logger)
    {
        _options = options;
        _logger = logger;

        // FullMode.Wait paired with TryWrite is the combination that gives a truthful answer
        // without ever blocking, and it reads backwards, so: TryWrite NEVER waits under any
        // mode — under Wait it simply returns false when the channel is full, which is exactly
        // the "I dropped your item, and I am telling you" contract TryEnqueue promises.
        // The DropWrite mode would be the trap here: it discards the item and returns TRUE,
        // so every drop would be silent. Only WriteAsync can block, and nothing calls it.
        _channel = Channel.CreateBounded<ModerationWorkItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// The drain side, for ContentModerationWorker (and the queue's own unit tests). Public
    /// on the CONCRETE class only — every write path depends on IContentModerationQueue, which
    /// exposes nothing but TryEnqueue, so no create path can reach this even by accident. The
    /// repo has no InternalsVisibleTo convention, and adding one for a single property would
    /// be the larger change.
    /// </summary>
    public ChannelReader<ModerationWorkItem> Reader => _channel.Reader;

    public bool TryEnqueue(ModerationWorkItem item)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(item))
        {
            return true;
        }

        // Warning, not error: a full queue is a degraded-but-working state, and the create
        // that produced this item has already committed successfully. Nothing is rolled back.
        _logger.LogWarning(
            "Moderation queue full ({Capacity}); dropped {TargetType} {TargetId} without classifying it.",
            _options.QueueCapacity, item.TargetType, item.TargetId);
        return false;
    }
}
