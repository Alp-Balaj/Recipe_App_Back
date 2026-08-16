using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.Infrastructure.Mail;

// Accounts (KAN-19). The provider is chosen HERE, by configuration, and this is the only
// place in the solution that knows which one is in use.
public static class MailServiceCollectionExtensions
{
    public static IServiceCollection AddMail(this IServiceCollection services, IConfiguration configuration)
    {
        // Bound eagerly with code defaults — the ModerationOptions/AiQuotaOptions idiom.
        var options = new MailOptions();
        configuration.GetSection(MailOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // No host configured means no provider configured, and the default is the sender that
        // cannot possibly reach a real inbox. That is what makes "a developer running locally
        // never sends mail" a property of the code rather than of remembering to unset a key.
        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            services.AddSingleton<IMailSender, LoggingMailSender>();
        }
        else
        {
            services.AddSingleton<IMailSender, SmtpMailSender>();
        }

        return services;
    }
}
