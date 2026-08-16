using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.IntegrationTests;

// Accounts (KAN-19). The mail seam's stand-in, registered exactly the way the chat,
// moderation, fetcher and scan fakes are: remove the real registration, add this one.
//
// It records what it "sent" KEYED BY RECIPIENT, and that choice is what makes it safe under
// the shared TestServer with no per-test reset: the auth helper already generates a unique
// address per test, so two tests running against the same host can never read each other's
// mail. A single "last message sent" field would have needed a reset hook and would still
// have raced.
//
// Recording rather than counting is deliberate too. A test that asserts "the mailer was
// called twice" pins an implementation detail; a test that pulls the real link out of the
// real message and clicks it exercises the whole flow the user is going to walk.
public sealed class FakeMailSender : IMailSender
{
    private readonly ConcurrentDictionary<string, List<OutboundEmail>> _byRecipient = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Addresses matching this are refused, so the failed-send path is reachable.</summary>
    public const string UndeliverableMarker = "__nodeliver";

    public Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        if (email.ToAddress.Contains(UndeliverableMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        _byRecipient.AddOrUpdate(
            email.ToAddress,
            _ => [email],
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(email);
                    return existing;
                }
            });

        return Task.FromResult(true);
    }

    /// <summary>Everything sent to an address, oldest first.</summary>
    public IReadOnlyList<OutboundEmail> SentTo(string address)
    {
        if (!_byRecipient.TryGetValue(address, out var messages))
        {
            return [];
        }

        lock (messages)
        {
            return messages.ToList();
        }
    }

    /// <summary>The most recent message sent to an address; throws when there is none.</summary>
    public OutboundEmail LastSentTo(string address)
    {
        var messages = SentTo(address);
        return messages.Count > 0
            ? messages[^1]
            : throw new InvalidOperationException($"No mail was sent to {address}.");
    }

    /// <summary>
    /// The token out of the newest link sent to an address — i.e. what the reader would end
    /// up with by clicking. Read out of the real body, so the whole composition path is under
    /// test rather than mocked past.
    /// </summary>
    public string LinkTokenSentTo(string address)
    {
        var body = LastSentTo(address).TextBody;
        var match = Regex.Match(body, @"[?&]token=([^\s&]+)");
        return match.Success
            ? Uri.UnescapeDataString(match.Groups[1].Value)
            : throw new InvalidOperationException($"The last mail to {address} carries no link token:\n{body}");
    }
}
