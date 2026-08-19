using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-21). Getting an account to the state the interesting tests start from:
/// verified email, enrolled authenticator, recovery codes in hand.
///
/// The codes are computed here the way an authenticator app computes them — from the secret
/// the server handed out, through the same RFC 6238 arithmetic TotpTests pins against the
/// RFC's own vectors. Nothing is faked or stubbed: if the server's TOTP were wrong, these
/// tests would fail rather than agree with it.
/// </summary>
public static class SecondFactorTestHelper
{
    /// <summary>An enrolled account: its credentials, its TOTP secret, and its recovery codes.</summary>
    public record EnrolledAccount(
        AuthTestHelper.TestAccount Account,
        string Secret,
        IReadOnlyList<string> RecoveryCodes)
    {
        public Guid UserId => Account.Auth.UserId;
    }

    /// <summary>The code an authenticator would be showing right now for this secret.</summary>
    public static string CodeFor(string secret, DateTime? at = null)
    {
        Assert.True(Base32.TryDecode(secret, out var bytes));
        return Totp.Compute(bytes, Totp.StepAt(at ?? DateTime.UtcNow));
    }

    /// <summary>
    /// A code for a step the account cannot have spent yet. Used wherever a test needs a
    /// SECOND valid code in the same run — the server refuses a step at or below the last one
    /// it accepted, which is the replay rule, so reusing "now" twice would fail for the right
    /// reason at the wrong moment.
    /// </summary>
    public static string NextCodeFor(string secret, int stepsAhead = 1) =>
        CodeFor(secret, DateTime.UtcNow.AddSeconds(Totp.Step.TotalSeconds * stepsAhead));

    /// <summary>
    /// Register an account, verify its email directly in the database, and walk the real
    /// two-step enrolment over HTTP. The client is left holding the account's bearer token.
    ///
    /// The email is verified by writing the column rather than by clicking a link: this helper
    /// exists so tests about the FACTOR do not re-walk KAN-19's flow, which has its own suite.
    /// </summary>
    public static async Task<EnrolledAccount> EnrolAsync(IntegrationTestFactory factory, HttpClient client)
    {
        var account = await AuthTestHelper.RegisterAccountAsync(client);
        await VerifyEmailAsync(factory, account.Auth.UserId);

        var begin = await client.PostAsync("/auth/second-factor/enrolment", null);
        begin.EnsureSuccessStatusCode();
        var enrolment = await begin.Content.ReadFromJsonAsync<SecondFactorEnrolmentResponse>(TestJson.Options);

        var confirm = await client.PostAsJsonAsync(
            "/auth/second-factor/enrolment/confirm",
            new ConfirmSecondFactorRequest(CodeFor(enrolment!.Secret)));
        confirm.EnsureSuccessStatusCode();
        var codes = await confirm.Content.ReadFromJsonAsync<RecoveryCodesResponse>(TestJson.Options);

        return new EnrolledAccount(account, enrolment.Secret, codes!.Codes);
    }

    /// <summary>Mark an address verified without walking KAN-19's link flow.</summary>
    public static async Task VerifyEmailAsync(IntegrationTestFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.EmailVerifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Sign in with a password and read back the challenge. Uses a FRESH client so the
    /// caller's bearer token cannot make the call look authenticated.
    /// </summary>
    public static async Task<(HttpClient Client, SecondFactorChallengeResponse Challenge)> SignInToChallengeAsync(
        IntegrationTestFactory factory, AuthTestHelper.TestAccount account)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        response.EnsureSuccessStatusCode();

        var challenge = await response.Content.ReadFromJsonAsync<SecondFactorChallengeResponse>(TestJson.Options);
        Assert.True(challenge!.ChallengeRequired);
        return (client, challenge);
    }

    /// <summary>Attach a bearer token to a client's default headers, the way AuthTestHelper does.</summary>
    public static void Authenticate(HttpClient client, AuthResponse auth) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
}
