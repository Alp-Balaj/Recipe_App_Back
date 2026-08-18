using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.API;
using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-20, ADR-0009) — cookie-borne sessions, refresh, and per-device revocation,
/// driven through HTTP.
///
/// Every test here uses a COOKIE-CARRYING client and sends no Authorization header at all,
/// because that is what the SPA now does. The rest of this suite authenticates with a bearer
/// through AuthTestHelper, and it still does: the bearer path is deliberately kept working
/// (see the OnMessageReceived comment in Program.cs), so those ~40 files go on testing what
/// they were written to test. This class is the one that proves the cookie path.
///
/// Time is not abstracted here either. The rotation grace and session expiry are exercised by
/// writing the STORED timestamps into the past, the same idiom as the account-token and
/// suspension tests.
/// </summary>
public class SessionEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    /// <summary>
    /// A client with a cookie jar, and a User-Agent, so it behaves like a browser: cookies set
    /// by one response ride on the next request without the test having to move them by hand.
    /// </summary>
    private HttpClient NewBrowser(string userAgent = ChromeOnWindows)
    {
        var client = _factory.CreateDefaultClient(new CookieContainerHandler());
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string SafariOnIPhone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    private sealed record Account(string Username, string Email, string Password, Guid UserId);

    /// <summary>Register and sign in through <paramref name="browser"/>, leaving it holding cookies.</summary>
    private static async Task<Account> SignUpAsync(HttpClient browser)
    {
        var username = $"sessiontest_{Guid.NewGuid():N}";
        var email = $"{username}@example.com";
        const string password = "Password123!";

        var registered = await browser.PostAsJsonAsync("/auth/register", new RegisterRequest(username, email, password));
        registered.EnsureSuccessStatusCode();

        var body = await registered.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        return new Account(username, email, password, body!.UserId);
    }

    /// <summary>Sign in a SECOND device for the same account, with its own cookie jar.</summary>
    private async Task<HttpClient> SignInAgainAsync(Account account, string userAgent)
    {
        var browser = NewBrowser(userAgent);
        var response = await browser.PostAsJsonAsync(
            "/auth/login", new LoginRequest(account.Username, account.Password));
        response.EnsureSuccessStatusCode();
        return browser;
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static string? SetCookie(HttpResponseMessage response, string name) =>
        SetCookies(response).FirstOrDefault(v => v.StartsWith($"{name}=", StringComparison.Ordinal));

    private static string? CookieValue(HttpResponseMessage response, string name) =>
        SetCookie(response, name)?.Split(';')[0].Split('=', 2)[1];

    // ── The cookies themselves ──────────────────────────────────────────────────────────

    // The properties the whole phase rests on. HttpOnly is what puts the session out of
    // script's reach, and SameSite is the entire CSRF answer for a single-origin app with no
    // CORS anywhere — so both are pinned here rather than left to a reviewer's memory.
    [Fact]
    public async Task SigningIn_SetsAnHttpOnlySameSiteLaxSessionPair()
    {
        using var browser = NewBrowser();
        var username = $"sessiontest_{Guid.NewGuid():N}";

        var response = await browser.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(username, $"{username}@example.com", "Password123!"));

        foreach (var name in new[] { SessionCookies.AccessCookieName, SessionCookies.RefreshCookieName })
        {
            var cookie = SetCookie(response, name);
            Assert.NotNull(cookie);
            Assert.Contains("httponly", cookie!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Secure is CONDITIONAL on the request scheme, and it has to be: a Secure cookie is
    // discarded by the browser over plain HTTP, so hard-coding it would make the session
    // silently fail to stick in local development and in every test here — with nothing in the
    // logs to say why. TestServer speaks http, so the absence below is the assertion.
    [Fact]
    public async Task SigningIn_OverPlainHttp_DoesNotMarkTheCookiesSecure()
    {
        using var browser = NewBrowser();
        var username = $"sessiontest_{Guid.NewGuid():N}";

        var response = await browser.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(username, $"{username}@example.com", "Password123!"));

        var cookie = SetCookie(response, SessionCookies.RefreshCookieName);
        Assert.DoesNotContain("secure", cookie!, StringComparison.OrdinalIgnoreCase);
    }

    // The point of the phase: a request authenticated by NOTHING but cookies. No Authorization
    // header is ever set on these clients.
    [Fact]
    public async Task ACookieAlone_AuthenticatesARequest()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        var me = await browser.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<MeResponse>(TestJson.Options);
        Assert.Equal(account.UserId, body!.UserId);
    }

    // A browser with no session is not signed in — the guard the SPA's boot depends on.
    [Fact]
    public async Task NoCookies_Is401()
    {
        using var browser = NewBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);
    }

    // ── Refresh ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refreshing_IssuesANewPairAndKeepsTheSessionUsable()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        var refreshed = await browser.PostAsync("/auth/refresh", content: null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotNull(SetCookie(refreshed, SessionCookies.AccessCookieName));
        Assert.NotNull(SetCookie(refreshed, SessionCookies.RefreshCookieName));

        var me = await browser.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<MeResponse>(TestJson.Options);
        Assert.Equal(account.UserId, body!.UserId);
    }

    // Refreshing does not open a second device. The row is the same one — which is what makes
    // the devices list stable across a month of ordinary use rather than filling up with a new
    // entry every fifteen minutes.
    [Fact]
    public async Task Refreshing_DoesNotOpenASecondSession()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        await browser.PostAsync("/auth/refresh", content: null);
        await browser.PostAsync("/auth/refresh", content: null);

        Assert.Equal(1, await CountSessionsAsync(account.UserId));
    }

    [Fact]
    public async Task Refreshing_WithNoCookie_Is401()
    {
        using var browser = NewBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/auth/refresh", content: null)).StatusCode);
    }

    // Rotation: the token that was spent stops working once its grace has lapsed. Without this
    // the "rotation" would just be two live refresh tokens.
    [Fact]
    public async Task Refreshing_RetiresTheSpentRefreshToken_OnceTheGraceHasLapsed()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        var spent = await CurrentRefreshCookieAsync(browser, account);
        await browser.PostAsync("/auth/refresh", content: null);
        await LeaveTheRotationGraceAsync(account.UserId);

        // A fresh jar carrying only the retired token — a client that kept the old cookie.
        using var stale = _factory.CreateDefaultClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.Headers.Add("Cookie", $"{SessionCookies.RefreshCookieName}={spent}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.SendAsync(request)).StatusCode);
    }

    // THE TWO-TAB RACE, end to end. The second tab's in-flight refresh arrives holding the
    // token its sibling has just spent. Inside the grace window it is answered — and answered
    // with an access cookie and NO refresh cookie, because the sibling's response already
    // replaced the shared one and overwriting it would put a retired token back in the jar.
    [Fact]
    public async Task Refreshing_WithAJustSupersededToken_SucceedsAndLeavesTheRefreshCookieAlone()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        var spent = await CurrentRefreshCookieAsync(browser, account);
        await browser.PostAsync("/auth/refresh", content: null);

        using var secondTab = _factory.CreateDefaultClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.Headers.Add("Cookie", $"{SessionCookies.RefreshCookieName}={spent}");
        var response = await secondTab.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(SetCookie(response, SessionCookies.AccessCookieName));
        Assert.Null(SetCookie(response, SessionCookies.RefreshCookieName));

        // And the winner's token still works, which is the property that stops the two tabs
        // taking turns invalidating each other forever.
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/refresh", content: null)).StatusCode);
    }

    [Fact]
    public async Task Refreshing_AnExpiredSession_Is401_AndClearsTheCookies()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        await ExpireSessionsAsync(account.UserId);

        var response = await browser.PostAsync("/auth/refresh", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Whatever the caller is holding does not work; leaving it in the jar means every
        // future request carries a dead credential.
        Assert.NotNull(SetCookie(response, SessionCookies.RefreshCookieName));
        Assert.Equal(string.Empty, CookieValue(response, SessionCookies.RefreshCookieName));
    }

    // A refresh MINTS a token, so it has to re-ask the question the request pipeline asks.
    // Without this an admin ban would hold for one access-token lifetime and then undo itself,
    // and the session row would keep handing out new tokens for a month.
    [Fact]
    public async Task Refreshing_ABannedAccount_Is401_AndDropsItsSessions()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        await BanAsync(account.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/auth/refresh", content: null)).StatusCode);
        Assert.Equal(0, await CountSessionsAsync(account.UserId));
    }

    // ── Logout ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoggingOut_EndsTheSessionAndClearsTheCookies()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        var response = await browser.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(account.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);
    }

    // Anonymous ON PURPOSE. A user coming back to a tab after lunch has an expired access
    // token, and a logout that 401s would leave alive exactly the session they were trying to
    // end. The credential it uses is the refresh cookie, which outlives the access one.
    [Fact]
    public async Task LoggingOut_WorksWithoutAUsableAccessToken()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);

        // Only the refresh cookie, as a tab whose access cookie has aged out would send.
        using var stale = _factory.CreateDefaultClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        request.Headers.Add("Cookie", $"{SessionCookies.RefreshCookieName}={await CurrentRefreshCookieAsync(browser, account)}");

        Assert.Equal(HttpStatusCode.NoContent, (await stale.SendAsync(request)).StatusCode);
        Assert.Equal(0, await CountSessionsAsync(account.UserId));
    }

    // Logging out of nothing is not an error, and an endpoint that reports which cookies are
    // real is an oracle nobody needs.
    [Fact]
    public async Task LoggingOut_WithNoSession_Is204()
    {
        using var browser = NewBrowser();

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/logout", content: null)).StatusCode);
    }

    [Fact]
    public async Task LoggingOut_LeavesTheOtherDevicesAlone()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        using var phone = await SignInAgainAsync(account, SafariOnIPhone);

        await browser.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/auth/me")).StatusCode);
    }

    // ── The devices list ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDevicesList_ShowsEachSignedInDevice_AndMarksTheCallersOwn()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        using var phone = await SignInAgainAsync(account, SafariOnIPhone);

        var list = await browser.GetFromJsonAsync<List<SessionSummary>>("/auth/sessions", TestJson.Options);

        Assert.Equal(2, list!.Count);
        Assert.Single(list, s => s.Current);
        Assert.Equal("Chrome on Windows", list.Single(s => s.Current).Label);
        Assert.Equal("Safari on iPhone", list.Single(s => !s.Current).Label);
    }

    [Fact]
    public async Task TheDevicesList_NeedsASession()
    {
        using var browser = NewBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/sessions")).StatusCode);
    }

    // Decision D2, end to end: dropping a device kills the access token it is ALREADY holding.
    // Without the session check in the pipeline this would pass at the row level and fail in
    // the only way that matters — the stolen device would keep working for another quarter of
    // an hour.
    [Fact]
    public async Task DroppingADevice_StopsItImmediately()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        using var phone = await SignInAgainAsync(account, SafariOnIPhone);

        var list = await browser.GetFromJsonAsync<List<SessionSummary>>("/auth/sessions", TestJson.Options);
        var phoneSession = list!.Single(s => !s.Current);

        var dropped = await browser.DeleteAsync($"/auth/sessions/{phoneSession.Id}");

        Assert.Equal(HttpStatusCode.NoContent, dropped.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/me")).StatusCode);
    }

    // A dropped device cannot refresh its way back in either — the row is gone, so there is
    // nothing left for its refresh cookie to name.
    [Fact]
    public async Task ADroppedDevice_CannotRefreshItsWayBack()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        using var phone = await SignInAgainAsync(account, SafariOnIPhone);

        var list = await browser.GetFromJsonAsync<List<SessionSummary>>("/auth/sessions", TestJson.Options);
        await browser.DeleteAsync($"/auth/sessions/{list!.Single(s => !s.Current).Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsync("/auth/refresh", content: null)).StatusCode);
    }

    // Ownership is part of the service's predicate, so somebody else's session id is
    // indistinguishable here from one that does not exist — which is also the only answer that
    // does not turn this endpoint into a "does this session id exist" oracle.
    [Fact]
    public async Task DroppingSomebodyElsesDevice_Is404_AndLeavesItAlone()
    {
        using var mine = NewBrowser();
        await SignUpAsync(mine);

        using var theirs = NewBrowser(SafariOnIPhone);
        var stranger = await SignUpAsync(theirs);
        var theirList = await theirs.GetFromJsonAsync<List<SessionSummary>>("/auth/sessions", TestJson.Options);

        var response = await mine.DeleteAsync($"/auth/sessions/{theirList!.Single().Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await theirs.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(1, await CountSessionsAsync(stranger.UserId));
    }

    // "Sign out everywhere" means everywhere ELSE. A button that signs you out of the device
    // you are pressing it on is one people press once and then never trust again.
    [Fact]
    public async Task SigningOutOtherDevices_KeepsTheOneAsking()
    {
        using var browser = NewBrowser();
        var account = await SignUpAsync(browser);
        using var phone = await SignInAgainAsync(account, SafariOnIPhone);
        using var laptop = await SignInAgainAsync(account, ChromeOnWindows);

        var response = await browser.DeleteAsync("/auth/sessions/others");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(1, await CountSessionsAsync(account.UserId));
    }

    // ── The bearer path, which this phase deliberately did not break ────────────────────

    // Sessions issued before KAN-20 carry no `sid` claim and must keep working — otherwise
    // deploying this signs out everybody at once. The Authorization header also has to WIN
    // over a cookie, which is what lets the rest of the suite go on authenticating its own way.
    [Fact]
    public async Task ABearerToken_StillAuthenticates_AndOutranksACookie()
    {
        using var bearerClient = _factory.CreateClient();
        var account = await AuthTestHelper.RegisterAndAuthenticateAsync(bearerClient);

        // A second account's cookies in the same jar as the first account's bearer.
        using var browser = NewBrowser();
        await SignUpAsync(browser);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);

        var me = await browser.GetFromJsonAsync<MeResponse>("/auth/me", TestJson.Options);

        Assert.Equal(account.UserId, me!.UserId);
    }

    // ── Helpers that move stored timestamps, because there is no clock to move ───────────

    private async Task<int> CountSessionsAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserSessions.CountAsync(s => s.UserId == userId);
    }

    private async Task ExpireSessionsAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.UserSessions
            .Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
    }

    private async Task LeaveTheRotationGraceAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var past = DateTime.UtcNow - Infrastructure.Auth.UserSessionService.RotationGrace - TimeSpan.FromSeconds(1);
        await db.UserSessions
            .Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RotatedAtUtc, past));
    }

    private async Task BanAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.IsBanned = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The refresh token the browser is currently holding, read out of the database rather than
    /// out of the jar — the jar hands back the value, but only the row can say which value is
    /// live. Used to build the "a client that kept the old cookie" cases above.
    /// </summary>
    private async Task<string> CurrentRefreshCookieAsync(HttpClient browser, Account account)
    {
        // The plaintext is never stored, so it has to be captured from the wire. Refreshing
        // once and reading the Set-Cookie is the only way a test can hold one.
        var response = await browser.PostAsync("/auth/refresh", content: null);
        response.EnsureSuccessStatusCode();
        return CookieValue(response, SessionCookies.RefreshCookieName)!;
    }
}
