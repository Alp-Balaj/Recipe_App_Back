namespace RecipeApp.Infrastructure.Mail;

// Accounts (KAN-19). Bound eagerly with code defaults, like ModerationOptions and
// AiQuotaOptions — the only genuinely secret value here is the SMTP password, which arrives
// as an env var like every other secret.
public class MailOptions
{
    public const string SectionName = "Mail";

    /// <summary>
    /// Where a link in an email points. The app is served single-origin in production (the
    /// SPA and the API share a host), so this is the SPA's origin. The default is the Vite
    /// dev server, which is what a developer running the no-op sender sees in their log.
    /// </summary>
    public string AppBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>The From address. Must be one the sending domain is allowed to send as.</summary>
    public string FromAddress { get; set; } = "no-reply@whatarewecooking.app";

    public string FromName { get; set; } = "What are we cooking?";

    /// <summary>
    /// The HTTP provider. An API key here selects <see cref="ResendMailSender"/>, and it is
    /// checked FIRST because it is the transport that works on hosts that block outbound
    /// SMTP — see MailServiceCollectionExtensions.
    /// </summary>
    public ResendOptions Resend { get; set; } = new();

    /// <summary>
    /// The SMTP provider. Empty (the default) means no SMTP provider is configured. With no
    /// provider of either kind the logging no-op sender is used, so local development and
    /// the tests never deliver a message to a real inbox.
    /// </summary>
    public SmtpOptions Smtp { get; set; } = new();

    public class ResendOptions
    {
        /// <summary>The API key. Secret: it arrives as an env var, never from appsettings.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Overridable so a test or a proxy can point this somewhere else.</summary>
        public string BaseUrl { get; set; } = "https://api.resend.com/";
    }

    public class SmtpOptions
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 587;
        public bool UseStartTls { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
