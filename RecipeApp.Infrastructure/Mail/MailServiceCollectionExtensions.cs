using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // Nothing configured means the sender that cannot possibly reach a real inbox. That is
        // what makes "a developer running locally never sends mail" a property of the code
        // rather than of remembering to unset a key.
        //
        // Resend is checked FIRST, and the order is the point: many hosts (Railway below Pro,
        // among others) block outbound SMTP entirely, so where both are configured the HTTP
        // transport is the one that actually leaves the container. Set the SMTP host alone to
        // choose SMTP; clear the API key to fall back to it.
        if (!string.IsNullOrWhiteSpace(options.Resend.ApiKey))
        {
            // A named client with its own ceiling, the AddVisionCaller idiom. 30 s is generous
            // for one JSON POST and a world away from SmtpClient's 100-second default, which is
            // what a blocked port previously cost every caller on the request thread.
            services.AddHttpClient(ResendMailSender.ClientName, http =>
            {
                http.BaseAddress = new Uri(options.Resend.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(30);
            });

            services.AddSingleton<IMailSender>(sp => new ResendMailSender(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(ResendMailSender.ClientName),
                options,
                sp.GetRequiredService<ILogger<ResendMailSender>>()));
        }
        else if (!string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            services.AddSingleton<IMailSender, SmtpMailSender>();
        }
        else
        {
            services.AddSingleton<IMailSender, LoggingMailSender>();
        }

        return services;
    }
}
