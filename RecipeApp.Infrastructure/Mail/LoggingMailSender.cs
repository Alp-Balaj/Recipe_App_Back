using Microsoft.Extensions.Logging;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.Infrastructure.Mail;

// The DEFAULT mail sender: it delivers nothing and says so (Accounts, KAN-19).
//
// This is what runs whenever no provider is configured — local development, CI, and any
// deploy that has not been given SMTP settings. A developer following a verification or
// reset flow reads the link out of their own application log, which is why the body is
// logged in full rather than summarised. That is also why this class must never be the
// registration in an environment where the log is not private.
public sealed class LoggingMailSender : IMailSender
{
    private readonly ILogger<LoggingMailSender> _logger;

    public LoggingMailSender(ILogger<LoggingMailSender> logger)
    {
        _logger = logger;
    }

    public Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Mail NOT sent (no provider configured). To: {To} | Subject: {Subject}\n{Body}",
            email.ToAddress, email.Subject, email.TextBody);
        return Task.FromResult(true);
    }
}
