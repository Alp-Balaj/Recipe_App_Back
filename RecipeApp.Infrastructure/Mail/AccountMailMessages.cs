using System.Net;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.Infrastructure.Mail;

// The three messages this phase sends (Accounts, KAN-19). Composition lives apart from
// sending so the wording is one readable file rather than string literals scattered through
// a service.
//
// Every message follows the same three rules, and they are the rules that let a reader tell
// a genuine message from a phishing attempt:
//   1. Name the app in the first line, and say what was requested.
//   2. Say how long the link lasts, so an old message in the mailbox is self-evidently stale.
//   3. Say plainly what to do if the reader did not ask for this — which, for both requests,
//      is nothing at all. A message that demands action from someone who did not act is the
//      shape phishing takes.
internal static class AccountMailMessages
{
    private const string AppName = "What are we cooking?";

    public static OutboundEmail EmailVerification(string toAddress, string link, TimeSpan lifetime) =>
        Compose(
            toAddress,
            subject: $"Verify your email for {AppName}",
            heading: "Verify your email address",
            lines:
            [
                $"Someone asked {AppName} to verify that this address belongs to you.",
                $"The link below works once and expires in {Describe(lifetime)}.",
            ],
            actionLabel: "Verify this address",
            link: link,
            closing: "If you did not ask for this, no action is needed — you can ignore this message and nothing will change.");

    public static OutboundEmail PasswordReset(string toAddress, string link, TimeSpan lifetime) =>
        Compose(
            toAddress,
            subject: $"Reset your {AppName} password",
            heading: "Reset your password",
            lines:
            [
                $"Someone asked to reset the password for the {AppName} account registered to this address.",
                $"The link below works once and expires in {Describe(lifetime)}. Any earlier reset link you were sent has stopped working.",
            ],
            actionLabel: "Choose a new password",
            link: link,
            closing: "If you did not ask for this, no action is needed — your password has not changed and this link will expire on its own.");

    // Sent AFTER the fact, so it has no link and asks for nothing. Its whole job is to make an
    // unauthorised reset visible to the person it happened to.
    public static OutboundEmail PasswordChanged(string toAddress) =>
        Compose(
            toAddress,
            subject: $"Your {AppName} password was changed",
            heading: "Your password was changed",
            lines:
            [
                $"The password for your {AppName} account was just reset, and every other signed-in device has been signed out.",
            ],
            actionLabel: null,
            link: null,
            closing: "If this was you, there is nothing to do. If it was not, reset your password again straight away — that will sign out whoever did this.");

    private static OutboundEmail Compose(
        string toAddress,
        string subject,
        string heading,
        string[] lines,
        string? actionLabel,
        string? link,
        string closing)
    {
        var text = string.Join(
            "\n\n",
            new[] { heading }
                .Concat(lines)
                .Concat(link is null ? [] : new[] { $"{actionLabel}: {link}" })
                .Concat([closing, $"— {AppName}"]));

        var html =
            $"""
            <div style="font-family:system-ui,sans-serif;font-size:15px;line-height:1.55;color:#22201d;">
              <h1 style="font-size:19px;margin:0 0 14px;">{Escape(heading)}</h1>
              {string.Concat(lines.Select(l => $"<p style=\"margin:0 0 12px;\">{Escape(l)}</p>"))}
              {(link is null ? string.Empty : $"<p style=\"margin:18px 0;\"><a href=\"{Escape(link)}\">{Escape(actionLabel!)}</a></p>")}
              <p style="margin:0 0 12px;color:#6c665e;">{Escape(closing)}</p>
              <p style="margin:0;color:#6c665e;">— {Escape(AppName)}</p>
            </div>
            """;

        return new OutboundEmail(toAddress, subject, text, html);
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string Describe(TimeSpan lifetime) =>
        lifetime.TotalHours >= 2 ? $"{(int)lifetime.TotalHours} hours" : "1 hour";
}
