using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.Infrastructure.Mail;

// The real sender (Accounts, KAN-19). SMTP over System.Net.Mail deliberately: it is in
// the framework, so the provider choice stays a matter of configuration rather than of which
// vendor SDK the solution took a dependency on. Every transactional mail service worth using
// speaks SMTP, so switching provider is a change to Mail:Smtp:* and nothing else.
//
// A send failure is LOGGED AND REPORTED, never thrown. Non-delivery is an ordinary outcome
// of talking to somebody else's server, and the caller has account state riding on the
// answer — see AccountRecoveryService, which undoes its own token row when this returns
// false so the user's account is not left in a state their next attempt cannot repair.
public sealed class SmtpMailSender : IMailSender
{
    private readonly MailOptions _options;
    private readonly ILogger<SmtpMailSender> _logger;

    public SmtpMailSender(MailOptions options, ILogger<SmtpMailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        var smtp = _options.Smtp;

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrEmpty(smtp.Username))
        {
            client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = email.Subject,
            Body = email.TextBody,
            IsBodyHtml = false,
        };
        message.To.Add(email.ToAddress);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            email.HtmlBody, null, "text/html"));

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Sent mail {Subject} to {To}.", email.Subject, email.ToAddress);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surfaced, not swallowed: silent non-delivery is indistinguishable from success
            // to everyone except the person waiting for the message.
            _logger.LogError(ex, "Mail send FAILED — {Subject} to {To} was not delivered.",
                email.Subject, email.ToAddress);
            return false;
        }
    }
}
