using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Entities.Moderation;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Moderation;

// Stream X. The other half of the first background execution seam in this backend, and the
// one place where the stream's central promise is actually kept: nothing in this file can
// fail, delay, or roll back a create, because by the time any of it runs the create has
// already committed and its HTTP response has already been written.
//
// Three properties hold that up, and each one is a specific catch or scope below:
//
//   1. EVERY per-item failure is swallowed and logged. A provider that is down, throttled,
//      returning garbage, or timing out costs one log line and one unclassified item.
//   2. Each item gets its OWN DI scope, so it gets its own DbContext. A BackgroundService is a
//      singleton and cannot capture a scoped one; sharing a single long-lived context across
//      items would also let one item's failed SaveChanges poison the next item's change tracker.
//   3. The provider call has its own timeout, so a hung request cannot wedge the loop behind it.
//
// The loop is deliberately SERIAL — one item at a time. The queue absorbs bursts, and a single
// consumer means the moderation lane cannot stampede the provider and trip the rate limiting
// that every user-facing lane depends on. If throughput ever becomes the constraint, the fix
// is N workers reading the same channel, not a faster loop.
public sealed class ContentModerationWorker : BackgroundService
{
    private const int SummaryMaxLength = 200;

    private readonly ContentModerationQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModerationOptions _options;
    private readonly ILogger<ContentModerationWorker> _logger;

    public ContentModerationWorker(
        ContentModerationQueue queue,
        IServiceScopeFactory scopeFactory,
        ModerationOptions options,
        ILogger<ContentModerationWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Content moderation is disabled (Moderation:Enabled=false); worker not started.");
            return;
        }

        _logger.LogInformation(
            "Content moderation worker started (capacity {Capacity}, minimum confidence {MinimumConfidence}).",
            _options.QueueCapacity, _options.MinimumConfidence);

        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown, not a failure. Stop draining and let the host finish.
                break;
            }
            catch (Exception ex)
            {
                // The catch-all that makes the whole design safe. A classifier that is down,
                // throttled, over budget or returning malformed JSON lands HERE — after the
                // content it was going to judge has already been created and served.
                _logger.LogError(
                    ex, "Moderation classification failed for {TargetType} {TargetId}; leaving it unflagged.",
                    item.TargetType, item.TargetId);
            }
        }
    }

    private async Task ProcessAsync(ModerationWorkItem item, CancellationToken stoppingToken)
    {
        // Own scope, own DbContext — see (2) in the class comment.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var content = await LoadAsync(db, item, stoppingToken);
        if (content is null)
        {
            // Edited into nothing, hard-deleted, or soft-deleted between the enqueue and now.
            // Re-reading by id is what makes this the normal, silent case rather than a crash.
            _logger.LogDebug("Moderation target {TargetType} {TargetId} no longer exists; skipping.",
                item.TargetType, item.TargetId);
            return;
        }

        // One OPEN auto-flag per target. This mirrors ReportService's duplicate rule rather
        // than inventing one, and it falls out of the system user being a real reporter: an
        // edit that re-enqueues content already sitting in the queue must not stack a second
        // identical row on the admin's inbox. A target flagged, triaged and then edited again
        // CAN be re-flagged — that is a new signal, exactly as re-reporting after triage is.
        // Resolved to the two nullable FKs BEFORE the query, exactly as ReportService does it.
        // A ternary inside the predicate would have to survive EF's expression translation;
        // two locals are plain parameters and cannot.
        var recipeId = item.TargetType == ReportTargetType.Recipe ? (Guid?)item.TargetId : null;
        var commentId = item.TargetType == ReportTargetType.Comment ? (Guid?)item.TargetId : null;

        var alreadyOpen = await db.Reports.AnyAsync(r =>
            r.ReporterId == SystemUsers.ModerationId
            && r.Status == ReportStatus.Open
            && r.TargetType == item.TargetType
            && r.RecipeId == recipeId
            && r.CommentId == commentId,
            stoppingToken);
        if (alreadyOpen)
        {
            _logger.LogDebug("{TargetType} {TargetId} already has an open auto-flag; skipping.",
                item.TargetType, item.TargetId);
            return;
        }

        var classifier = scope.ServiceProvider.GetRequiredService<IContentModerationClassifier>();
        var usage = scope.ServiceProvider.GetRequiredService<IAiUsageService>();

        // A hung provider must not wedge the single worker loop — see (3).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ClassificationTimeoutSeconds));

        // NOTE THE ABSENCE: no GetBudgetAsync gate. Every other lane checks the caller's
        // budget before spending, but the "caller" here is the platform and the content's
        // author is a third party — gating on their budget would let a user suppress
        // moderation of their own content by exhausting their own quota. See
        // AiUsageLanes.ContentModeration.
        var verdict = await classifier.ClassifyAsync(content.Subject, timeout.Token);

        // Recorded whether or not anything is flagged: the call cost money either way, and a
        // lane that only bills its positives under-reports exactly like propose-week did
        // before stream B metered it. Staged on this scope's DbContext and committed by the
        // SaveChanges below, the same unit-of-work discipline every other lane uses.
        usage.RecordCall(SystemUsers.ModerationId, AiUsageLanes.ContentModeration, verdict.Usage);

        var flag = verdict.ShouldFlag && verdict.Confidence >= _options.MinimumConfidence;
        if (flag)
        {
            db.Reports.Add(new Report
            {
                Id = Guid.NewGuid(),
                // The system user, so Report's required Reporter navigation is satisfied and
                // the row joins into the existing Open queue like any other (decision D20).
                ReporterId = SystemUsers.ModerationId,
                TargetType = item.TargetType,
                RecipeId = recipeId,
                CommentId = commentId,
                TargetSummary = content.Summary,
                Reason = verdict.Reason,
                Details = string.IsNullOrWhiteSpace(verdict.Rationale) ? null : verdict.Rationale,
                Source = ReportSource.Automated,
                Confidence = verdict.Confidence,
                // Status stays Open. Auto-flag, never auto-delete: the content is untouched
                // and stays visible until a human resolves the report through stream D's
                // existing resolve/dismiss path, which writes the audit row.
            });
        }

        await db.SaveChangesAsync(stoppingToken);

        if (flag)
        {
            _logger.LogInformation(
                "Auto-flagged {TargetType} {TargetId} as {Reason} (confidence {Confidence:F2}).",
                item.TargetType, item.TargetId, verdict.Reason, verdict.Confidence);
        }
        else
        {
            _logger.LogDebug(
                "{TargetType} {TargetId} cleared moderation (violates={Violates}, confidence {Confidence:F2}).",
                item.TargetType, item.TargetId, verdict.ShouldFlag, verdict.Confidence);
        }
    }

    // Re-read under the ADMIN reading of visibility, not a user's: a private or soft-deleted
    // recipe is still content this system is responsible for. IgnoreQueryFilters is what makes
    // a recipe soft-deleted between enqueue and classification still readable — but note that
    // LoadAsync returns it, and the flag lands on a hidden row, which is harmless: the triage
    // queue shows it and an admin dismisses it.
    private static async Task<LoadedContent?> LoadAsync(
        ApplicationDbContext db, ModerationWorkItem item, CancellationToken cancellationToken)
    {
        switch (item.TargetType)
        {
            case ReportTargetType.Recipe:
            {
                var recipe = await db.Recipes
                    .IgnoreQueryFilters()
                    .Where(r => r.Id == item.TargetId)
                    .Select(r => new { r.Title, r.Description, r.Steps })
                    .SingleOrDefaultAsync(cancellationToken);
                if (recipe is null)
                {
                    return null;
                }

                var steps = recipe.Steps
                    .OrderBy(s => s.StepNumber)
                    .Select(s => s.Description)
                    .ToList();

                return new LoadedContent(
                    ModerationSubject.ForRecipe(recipe.Title, recipe.Description, steps),
                    Excerpt($"Recipe: {recipe.Title}"));
            }

            case ReportTargetType.Comment:
            {
                var comment = await db.Comments
                    .Where(c => c.Id == item.TargetId)
                    .Select(c => new { c.Content, AuthorUsername = c.User.Username })
                    .SingleOrDefaultAsync(cancellationToken);
                if (comment is null)
                {
                    return null;
                }

                return new LoadedContent(
                    ModerationSubject.ForComment(comment.Content),
                    Excerpt($"Comment by {comment.AuthorUsername}: {comment.Content}"));
            }

            default:
                // ReportTargetType.User is reportable by a human but is not classifiable
                // content — there is no text to judge. Nothing enqueues it; this is the guard.
                return null;
        }
    }

    // Same excerpt rule and length as ReportService, so an auto-flag reads identically to a
    // human report in the queue.
    private static string Excerpt(string text)
    {
        var flattened = text.ReplaceLineEndings(" ");
        return flattened.Length <= SummaryMaxLength ? flattened : flattened[..SummaryMaxLength];
    }

    private sealed record LoadedContent(ModerationSubject Subject, string Summary);
}
