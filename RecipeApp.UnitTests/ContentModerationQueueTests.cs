using Microsoft.Extensions.Logging.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Moderation;

namespace RecipeApp.UnitTests;

// Stream X. The queue's contract is almost entirely about what it does under stress, because
// that is where a create path could be harmed. These tests pin the three properties the
// create paths rely on: it never blocks, it tells the truth about drops, and the master
// switch really is off.
public class ContentModerationQueueTests
{
    private static ContentModerationQueue Build(int capacity = 2, bool enabled = true) =>
        new(new ModerationOptions { QueueCapacity = capacity, Enabled = enabled },
            NullLogger<ContentModerationQueue>.Instance);

    private static ModerationWorkItem AnItem() =>
        new(ReportTargetType.Recipe, Guid.NewGuid());

    [Fact]
    public void Accepts_items_up_to_capacity()
    {
        var queue = Build(capacity: 2);

        Assert.True(queue.TryEnqueue(AnItem()));
        Assert.True(queue.TryEnqueue(AnItem()));
    }

    // The behaviour that makes "must not delay a create" true, and the one that would silently
    // break if BoundedChannelFullMode were ever changed to DropWrite: that mode discards the
    // item and returns TRUE, so every drop would be invisible. Wait + TryWrite is the pairing
    // that returns false immediately instead.
    [Fact]
    public void Reports_false_instead_of_blocking_when_full()
    {
        var queue = Build(capacity: 2);
        queue.TryEnqueue(AnItem());
        queue.TryEnqueue(AnItem());

        var completed = Task.Run(() => queue.TryEnqueue(AnItem()));

        // If the queue ever blocks, this is where the suite hangs rather than fails — so the
        // wait is bounded and the assertion is on completion, not just the result.
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "TryEnqueue blocked on a full queue.");
        Assert.False(completed.Result);
    }

    [Fact]
    public void Draining_frees_capacity_again()
    {
        var queue = Build(capacity: 1);
        Assert.True(queue.TryEnqueue(AnItem()));
        Assert.False(queue.TryEnqueue(AnItem()));

        Assert.True(queue.Reader.TryRead(out _));
        Assert.True(queue.TryEnqueue(AnItem()));
    }

    // Disabled must be inert rather than merely quiet: nothing is queued, so nothing is
    // classified even if a worker were somehow running.
    [Fact]
    public void Disabled_accepts_nothing()
    {
        var queue = Build(enabled: false);

        Assert.False(queue.TryEnqueue(AnItem()));
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public void Items_come_back_out_in_the_order_they_went_in()
    {
        var queue = Build(capacity: 4);
        var first = AnItem();
        var second = AnItem();

        queue.TryEnqueue(first);
        queue.TryEnqueue(second);

        Assert.True(queue.Reader.TryRead(out var read1));
        Assert.True(queue.Reader.TryRead(out var read2));
        Assert.Equal(first.TargetId, read1!.TargetId);
        Assert.Equal(second.TargetId, read2!.TargetId);
    }
}
