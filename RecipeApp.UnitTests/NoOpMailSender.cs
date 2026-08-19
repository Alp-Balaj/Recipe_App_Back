using RecipeApp.Application.Mail.Abstractions;

namespace RecipeApp.UnitTests;

// Accounts (KAN-21). Shared no-op fake for unit tests that construct a service directly and
// need SOMETHING implementing IMailSender in the constructor — the same role
// NoOpAppEventLogger plays beside it.
//
// It reports SUCCESS, which is the interesting half: a service whose notification fails has
// its own repair paths (KAN-19 discards the link nobody received), and a fake that always
// failed would silently put every test on those paths instead of the ordinary one. A class
// that wants to assert about what was sent uses its own recording double.
public sealed class NoOpMailSender : IMailSender
{
    public Task<bool> SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
