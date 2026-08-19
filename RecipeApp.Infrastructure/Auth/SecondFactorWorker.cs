using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Events;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Mail;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-21): the background half of the second factor — the 48-hour cooling-off
// sweep, and the retention sweep for abandoned challenges.
//
// THE DETERMINISTIC WORKER PATTERN, as AccountTokenPruneWorker, UserSessionPruneWorker and
// AppEventPruneWorker use it: the WORK is a static method taking the database and a cutoff,
// and the loop below is only a scheduler. Tests call the static methods directly with a
// cutoff of their choosing, so the 48-hour rule is exercised in milliseconds and nothing has
// to drive a timer. It is deliberately NOT the polling pattern ContentModerationWorker needs
// — that one waits for work to arrive, this one asks a question about time.
//
// Named for the feature rather than for a table because it genuinely spans two, and a class
// called SecondFactorResetWorker that also pruned challenges would be the small lie the
// sibling workers' comments warn about.
public sealed class SecondFactorWorker : BackgroundService
{
    /// <summary>
    /// How often the sweep runs. Fifteen minutes against a 48-hour deadline: the wait is the
    /// promise, and being a few minutes late keeping it costs nobody anything, while polling
    /// harder would only spend queries on a table that is almost always empty.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecondFactorWorker> _logger;

    public SecondFactorWorker(IServiceScopeFactory scopeFactory, ILogger<SecondFactorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Who a completed sweep needs to tell, and about what.</summary>
    public record ResetCompletion(Guid UserId, string Email);

    /// <summary>
    /// Strip the second factor from every account whose cooling-off period ended before
    /// <paramref name="cutoffUtc"/>, and hand back who to notify.
    ///
    /// Sending the mail is the CALLER's job, not this method's, and that split is what keeps
    /// the rule testable: "did the factor come off after 48 hours and not before" is a
    /// question about rows, and answering it should not require a mail seam.
    ///
    /// Everything a stripped account no longer needs goes with it — the secret, the recovery
    /// codes, any live challenge, any outstanding reset link — through the same
    /// RemoveFactorRowsAsync the user-initiated disable and the admin reset use, so the three
    /// cannot leave different debris behind.
    /// </summary>
    public static async Task<IReadOnlyList<ResetCompletion>> SweepResetsAsync(
        ApplicationDbContext db, DateTime cutoffUtc, CancellationToken ct)
    {
        var due = await db.SecondFactorResetRequests
            .Include(r => r.User)
            .Where(r => r.CancelledAt == null && r.CompletedAt == null && r.EffectiveAtUtc <= cutoffUtc)
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            return [];
        }

        var completed = new List<ResetCompletion>(due.Count);

        foreach (var request in due)
        {
            await SecondFactorService.RemoveFactorRowsAsync(db, request.UserId, ct);

            // Every session goes. There is no acting session to spare here — by construction
            // nobody proved anything to reach this point — and the account's security has
            // just materially changed, so a device signed in from before the change must not
            // ride through it. The refresh cookies die with the rows; the TokenVersion bump
            // below is what kills the access tokens already in flight.
            db.UserSessions.RemoveRange(
                await db.UserSessions.Where(s => s.UserId == request.UserId).ToListAsync(ct));

            request.User.TokenVersion++;
            request.CompletedAt = cutoffUtc;

            completed.Add(new ResetCompletion(request.UserId, request.User.Email));
        }

        await db.SaveChangesAsync(ct);
        return completed;
    }

    /// <summary>
    /// Delete challenges that expired before <paramref name="cutoffUtc"/>.
    ///
    /// A challenge is answered or superseded in the ordinary course, so this only collects the
    /// abandoned ones — somebody who typed a password, saw the code prompt, and closed the
    /// tab. Nothing user-visible depends on it; it is about the table's size.
    /// </summary>
    public static Task<int> PruneChallengesAsync(ApplicationDbContext db, DateTime cutoffUtc, CancellationToken ct) =>
        db.SignInChallenges.Where(c => c.ExpiresAtUtc < cutoffUtc).ExecuteDeleteAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var now = DateTime.UtcNow;

                var securityState = scope.ServiceProvider.GetRequiredService<IUserSecurityStateService>();

                foreach (var completion in await SweepResetsAsync(db, now, stoppingToken))
                {
                    // The TokenVersion bump the sweep wrote is read through a 60-second cache
                    // on every request, so without this it does not bite for up to a minute.
                    // Same call AdminService makes after a ban.
                    securityState.Invalidate(completion.UserId);

                    _logger.LogWarning(
                        "The cooling-off period ended for user {UserId}; their second factor has been removed.",
                        completion.UserId);

                    var events = scope.ServiceProvider.GetRequiredService<IAppEventLogger>();
                    await events.LogAsync(AppEventType.SecondFactorResetCompleted, actorUserId: completion.UserId);

                    // Told after the fact, and the message asks for nothing. A failure to
                    // deliver it must not undo a removal that has already happened, so it is
                    // logged and the sweep stands.
                    var mail = scope.ServiceProvider.GetRequiredService<IMailSender>();
                    if (!await mail.SendAsync(AccountMailMessages.SecondFactorRemoved(completion.Email), stoppingToken))
                    {
                        _logger.LogError(
                            "The second-factor-removed notice for user {UserId} was not delivered.", completion.UserId);
                        await events.LogAsync(
                            AppEventType.MailSendFailed, actorUserId: completion.UserId, detail: "second-factor-removed");
                    }
                }

                var pruned = await PruneChallengesAsync(db, now, stoppingToken);
                if (pruned > 0)
                {
                    _logger.LogInformation("Pruned {Count} abandoned sign-in challenges.", pruned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The second-factor sweep failed; retrying next cycle.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
