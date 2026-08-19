using System.Net;
using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.Infrastructure.Mail;

// The messages the Accounts phases send (KAN-19, extended by KAN-21). Composition lives apart
// from sending so the wording is one readable file rather than string literals scattered
// through a service.
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

    // ── Accounts (KAN-21): the second factor ────────────────────────────────────
    //
    // Three of the four below are sent AFTER the fact and carry no link at all. That is not
    // an oversight: a message about someone else possibly taking your account is exactly the
    // message an attacker would love to be able to imitate, and one that never asks the
    // reader to click anything is one a reader can learn to trust.

    public static OutboundEmail SecondFactorEnrolled(string toAddress) =>
        Compose(
            toAddress,
            subject: $"Two-step sign-in is on for your {AppName} account",
            heading: "Two-step sign-in is on",
            lines:
            [
                $"An authenticator app was set up for your {AppName} account, and every other signed-in device has been signed out.",
                "Keep the recovery codes you were shown somewhere safe. They are the fastest way back in if you lose your phone, and they are the only one that does not involve waiting.",
            ],
            actionLabel: null,
            link: null,
            closing: "If this was not you, whoever did it had your password — change it straight away, and use a recovery code to sign in if you need to.");

    public static OutboundEmail SecondFactorRemoved(string toAddress) =>
        Compose(
            toAddress,
            subject: $"Two-step sign-in is off for your {AppName} account",
            heading: "Two-step sign-in is off",
            lines:
            [
                $"The authenticator app on your {AppName} account has been removed. Your password is now the only thing standing in front of it.",
                "You can set up a new authenticator at any time from Settings → Security.",
            ],
            actionLabel: null,
            link: null,
            closing: "If you did not do this, change your password now and set two-step sign-in back up.");

    // The one link in this group. Clicking it does NOT remove anything — it starts a clock,
    // which the message has to say plainly or the reader will assume they are done.
    public static OutboundEmail SecondFactorResetLink(
        string toAddress, string link, TimeSpan lifetime, TimeSpan coolingOff) =>
        Compose(
            toAddress,
            subject: $"Turning off two-step sign-in for {AppName}",
            heading: "Turn off two-step sign-in",
            lines:
            [
                $"Someone asked to turn off two-step sign-in for the {AppName} account registered to this address, because they cannot use their authenticator.",
                $"The link below works once and expires in {Describe(lifetime)}. It does not turn anything off by itself — it starts a {(int)coolingOff.TotalHours}-hour wait, and we will tell you when it does.",
                "If you still have a recovery code, use that instead. It works immediately, and there is no wait.",
            ],
            actionLabel: "Start the wait",
            link: link,
            closing: "If you did not ask for this, no action is needed — this link will expire on its own and nothing will change.");

    // The message the whole 48-hour design exists to make possible: it turns a silent takeover
    // into two days of warning.
    public static OutboundEmail SecondFactorResetScheduled(string toAddress, DateTime effectiveAtUtc) =>
        Compose(
            toAddress,
            subject: $"Two-step sign-in will be turned off for your {AppName} account",
            heading: "Two-step sign-in will be turned off",
            lines:
            [
                $"Someone confirmed a request to turn off two-step sign-in for your {AppName} account. It stays on until {effectiveAtUtc:HH:mm} UTC on {effectiveAtUtc:d MMMM yyyy}.",
                "If that was you, there is nothing to do — sign in with your password once the wait is over.",
                "If it was NOT you, sign in now with your authenticator or a recovery code and cancel it from Settings → Security. Cancelling takes one click and there is no limit on how often you can do it.",
            ],
            actionLabel: null,
            link: null,
            closing: "We are deliberately slow about this, because anyone who can read your email should not be able to take your account in a single step.");

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
