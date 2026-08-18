using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.Infrastructure.Mail;

// The HTTP sender (Accounts, KAN-19). Same seam as SmtpMailSender, different transport:
// one POST to the provider's REST API over 443 instead of an SMTP conversation on 587.
//
// It exists because the deployment host BLOCKS OUTBOUND SMTP. Railway closes ports 25, 465,
// 587 and 2525 on every plan below Pro, and a blocked port does not refuse a connection —
// it blackholes it, so SmtpMailSender hung for its full 100-second default timeout on every
// send and reported a timeout that looked like a provider outage. Nothing was misconfigured;
// the packets simply never left the container. HTTPS on 443 is not blocked anywhere, which
// makes this the transport that works rather than the transport that is nicer.
//
// It is also better on its own merits: the provider answers in milliseconds and its failures
// arrive as a JSON body naming the actual problem — an unverified sending domain, a sandbox
// recipient restriction — where SMTP offered a socket exception and a guess.
//
// SmtpMailSender is deliberately kept. This is a host constraint, not a verdict on SMTP, and
// the choice between them stays a matter of configuration — see MailServiceCollectionExtensions.
public sealed class ResendMailSender : IMailSender
{
    /// <summary>The named client, so this call's timeout is its own — see AddMail.</summary>
    public const string ClientName = "resend-mail";

    private readonly HttpClient _http;
    private readonly MailOptions _options;
    private readonly ILogger<ResendMailSender> _logger;

    public ResendMailSender(HttpClient http, MailOptions options, ILogger<ResendMailSender> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        // "Name <address>" is the provider's expected form; the bare address is still valid
        // when no display name is configured.
        var from = string.IsNullOrWhiteSpace(_options.FromName)
            ? _options.FromAddress
            : $"{_options.FromName} <{_options.FromAddress}>";

        var payload = new ResendRequest(
            From: from,
            To: [email.ToAddress],
            Subject: email.Subject,
            Html: email.HtmlBody,
            Text: email.TextBody);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
            {
                Content = JsonContent.Create(payload),
            };
            // Per-request rather than on the client, so the key is never a property of a
            // shared, injectable HttpClient that some other caller could pick up.
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.Resend.ApiKey);

            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Sent mail {Subject} to {To}.", email.Subject, email.ToAddress);
                return true;
            }

            // The body is the useful half: it names the reason (unverified domain, sandbox
            // recipient restriction, bad key) where the status code alone would not. It
            // contains no credential — the key travels in the request header, not the reply.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Mail send FAILED — {Subject} to {To} was rejected with {Status}: {Body}",
                email.Subject, email.ToAddress, (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surfaced, not swallowed, and not thrown: silent non-delivery is indistinguishable
            // from success to everyone except the person waiting for the message, and the
            // caller has account state riding on the answer. Same contract as SmtpMailSender.
            _logger.LogError(ex, "Mail send FAILED — {Subject} to {To} was not delivered.",
                email.Subject, email.ToAddress);
            return false;
        }
    }

    // Property names are the provider's wire contract, hence the explicit names rather than
    // a serializer policy: this shape must not drift with the app's JSON conventions.
    private sealed record ResendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text);
}
