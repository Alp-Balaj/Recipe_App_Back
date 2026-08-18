using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Mail.Abstractions;
using RecipeApp.Infrastructure.Mail;

namespace RecipeApp.UnitTests;

// Accounts (KAN-19). AddMail's provider choice, which is configuration-shaped and therefore
// invisible until something does not arrive — the failure this pins cost a production
// debugging session to find, and nothing else in the suite would have caught it.
//
// The precedence is the part worth nailing down: Resend BEFORE Smtp, because hosts that block
// outbound SMTP (Railway below Pro) make the HTTP transport the only one that leaves the
// container. An inversion here would send over a port that silently blackholes, and every
// symptom would point at the provider instead of at this file.
//
// Nothing here talks to a network. Registration is resolved and its type asserted, so the
// whole class runs with no Docker, no port and no provider — see the parallel-sessions
// testing policy in CLAUDE.md.
public class MailProviderSelectionTests
{
    private static IMailSender ResolveSenderFor(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMail(configuration);

        return services.BuildServiceProvider().GetRequiredService<IMailSender>();
    }

    [Fact]
    public void NoProviderConfigured_UsesTheSenderThatCannotReachAnInbox()
    {
        // The default, and the one that matters most: a developer running locally, and CI,
        // must never be able to deliver to a real address by forgetting to unset something.
        var sender = ResolveSenderFor();

        Assert.IsType<LoggingMailSender>(sender);
    }

    [Fact]
    public void SmtpHostAlone_UsesSmtp()
    {
        var sender = ResolveSenderFor(("Mail:Smtp:Host", "smtp.example.com"));

        Assert.IsType<SmtpMailSender>(sender);
    }

    [Fact]
    public void ResendKeyAlone_UsesResend()
    {
        var sender = ResolveSenderFor(("Mail:Resend:ApiKey", "re_test_key"));

        Assert.IsType<ResendMailSender>(sender);
    }

    [Fact]
    public void BothConfigured_PrefersResend_BecauseSomeHostsBlockOutboundSmtp()
    {
        // The precedence rule, stated where it can break loudly. On a host that blocks SMTP
        // the alternative is not a slower send but a 100-second hang and no mail at all.
        var sender = ResolveSenderFor(
            ("Mail:Resend:ApiKey", "re_test_key"),
            ("Mail:Smtp:Host", "smtp.example.com"));

        Assert.IsType<ResendMailSender>(sender);
    }

    [Fact]
    public void BlankResendKey_DoesNotCountAsConfigured()
    {
        // An env var set to empty is a normal accident in a deployment dashboard, and it must
        // fall through to the next provider rather than select a sender with no credential.
        var sender = ResolveSenderFor(
            ("Mail:Resend:ApiKey", "   "),
            ("Mail:Smtp:Host", "smtp.example.com"));

        Assert.IsType<SmtpMailSender>(sender);
    }
}
