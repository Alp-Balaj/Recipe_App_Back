namespace RecipeApp.Application.Mail.Abstractions;

/// <summary>
/// One message, addressed to one person. Plain text plus an HTML body: every message this
/// app sends is a sentence and a link, so nothing here models attachments, templates or
/// bulk recipients — the day one of those is needed is the day this record grows a field.
/// </summary>
public record OutboundEmail(string ToAddress, string Subject, string TextBody, string HtmlBody);

/// <summary>
/// The ONE mail-sending seam (Accounts, KAN-19). Everything above Infrastructure talks
/// to this and nothing else knows which provider is in use, so swapping vendors is a change
/// to one registration in Program.cs.
///
/// Implementations must not throw for an ordinary delivery failure: they return false and
/// log, and the caller decides what a failed send means. That keeps "the mail did not go
/// out" from leaving an account in a half-built state — the caller can undo its own work,
/// and the user can simply ask again.
/// </summary>
public interface IMailSender
{
    /// <summary>
    /// True when this sender accepted the message; false when it could not.
    ///
    /// Deliberately "this sender accepted it" and not "it was delivered" — no sender can
    /// promise delivery, and the no-op sender that runs when no provider is configured
    /// returns true having sent nothing at all, which is the whole point of it: local
    /// development and the tests must walk the same success path production walks.
    /// False therefore means "I could not even hand this on", which is the one outcome a
    /// caller can actually do something about.
    /// </summary>
    Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default);
}
