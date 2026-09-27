using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Identity.Endpoints.AuthCenter;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// AUTH-001-RP-INITIATED-LOGOUT, AUTH-001-BACKCHANNEL-LOGOUT, AUTH-001-MFA-STEP-UP and
/// AUTH-001-ACCESS-DENIED-MESSAGE against the fake AuthCenter.
/// </summary>
public sealed partial class AuthCenterBffTests
{
    private const string MfaRequiredCode = "MFA_REQUIRED";

    // ---- RP-initiated logout -------------------------------------------------------------------

    [Fact]
    public async Task Logout_returns_the_end_session_url_with_the_id_token_hint_and_the_exact_post_logout_redirect_uri()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var idToken = factory.AuthCenter.LastIdToken;
        var csrf = await ReadCsrfAsync(browser);

        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("no-store", logout.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var endSessionUrl = await ReadEndSessionUrlAsync(logout);
        Assert.NotNull(endSessionUrl);
        FakeAuthCenterServer.AssertEndSessionRequest(endSessionUrl, idToken);
        Assert.Single(factory.AuthCenter.RevokedTokens);
        AssertSessionCookieDeleted(logout);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task Logout_destroys_the_session_even_when_discovery_fails()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var callback = await SignInAsync(factory, browser, ViewerSubject);
        var stolenCookie = SessionCookie(callback);
        var csrf = await ReadCsrfAsync(browser);
        factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AuthCenterDefaults.OpenIdConnectScheme)
            .ConfigurationManager = new UnavailableConfigurationManager();

        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Null(await ReadEndSessionUrlAsync(logout));
        Assert.Empty(factory.AuthCenter.RevokedTokens);
        AssertSessionCookieDeleted(logout);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        using var replay = await SendWithCookieAsync(factory, HttpMethod.Get, AuthCenterDefaults.SessionPath, stolenCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Logout_without_csrf_is_rejected_and_neither_ends_the_session_nor_reveals_an_end_session_url()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);

        using var withoutCsrf = await browser.PostAsync(AuthCenterDefaults.LogoutPath, null);
        using var wrongCsrf = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, "x" + await ReadCsrfAsync(browser));

        Assert.Equal(HttpStatusCode.Unauthorized, withoutCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCsrf.StatusCode);
        Assert.DoesNotContain("endSessionUrl", await withoutCsrf.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.AuthCenter.LastIdToken, await wrongCsrf.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(factory.AuthCenter.RevokedTokens);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://authcenter.test/oauth/logout")]
    [InlineData("https://evil.test/oauth/logout")]
    [InlineData("https://authcenter.test.evil.test/oauth/logout")]
    [InlineData("/oauth/logout")]
    public async Task End_session_url_is_null_without_a_usable_AuthCenter_endpoint(string? endpoint)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        factory.AuthCenter.PublishedEndSessionEndpoint = endpoint;
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);

        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Null(await ReadEndSessionUrlAsync(logout));
        AssertSessionCookieDeleted(logout);
    }

    [Fact]
    public async Task End_session_url_is_null_for_a_session_without_an_id_token()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var callback = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);
        // A ticket written before the ID token was retained (or by any other path) has none.
        await RewriteStoredTicketAsync(factory, SessionCookie(callback), ticket =>
            ticket.Properties.StoreTokens(ticket.Properties.GetTokens().Where(token => token.Name != "id_token").ToArray()));

        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Null(await ReadEndSessionUrlAsync(logout));
        Assert.Single(factory.AuthCenter.RevokedTokens);
        AssertSessionCookieDeleted(logout);
    }

    // ---- Back-channel logout -------------------------------------------------------------------

    [Fact]
    public async Task Backchannel_logout_ends_the_matching_session_on_its_next_request()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var other = factory.CreateBrowser();
        using var callback = await SignInAsync(factory, browser, ViewerSubject);
        var sessionId = factory.AuthCenter.LastSessionId;
        // The same person on another device: a different AuthCenter single sign-on session.
        factory.AuthCenter.Behavior = new TokenBehavior { SessionId = Guid.NewGuid().ToString() };
        using var otherCallback = await SignInAsync(factory, other, ViewerSubject);
        using var before = await browser.GetAsync(ActiveProbe);

        using var response = await PostLogoutTokenAsync(
            factory,
            factory.AuthCenter.CreateLogoutToken(ViewerSubject, sessionId));

        Assert.Equal(HttpStatusCode.NoContent, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var after = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        AssertSessionCookieDeleted(after);
        using var replayedCookie = await SendWithCookieAsync(factory, HttpMethod.Get, ActiveProbe, SessionCookie(callback));
        Assert.Equal(HttpStatusCode.Unauthorized, replayedCookie.StatusCode);
        // Another AuthCenter session of the same person is untouched.
        using var otherSession = await other.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
    }

    [Fact]
    public async Task Backchannel_logout_with_only_sub_ends_sessions_issued_before_it_but_not_later_ones()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);

        using var response = await PostLogoutTokenAsync(factory, factory.AuthCenter.CreateLogoutToken(ViewerSubject, sessionId: null));
        using var ended = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        using var again = await SignInAsync(factory, browser, ViewerSubject);
        using var renewed = await browser.GetAsync(AuthCenterDefaults.SessionPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    [Fact]
    public async Task Replayed_logout_token_is_rejected_and_does_not_end_a_newer_session()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var first = await SignInAsync(factory, browser, ViewerSubject);
        var token = factory.AuthCenter.CreateLogoutToken(ViewerSubject, sessionId: null);
        using var accepted = await PostLogoutTokenAsync(factory, token);
        using var second = await SignInAsync(factory, browser, ViewerSubject);

        using var replay = await PostLogoutTokenAsync(factory, token);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await AssertInvalidRequestAsync(replay);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    public static TheoryData<string> RejectedLogoutTokens => new()
    {
        "replayed jti",
        "ID token",
        "other audience",
        "other issuer",
        "issuer with trailing slash",
        "typ JWT",
        "HS256",
        "alg none",
        "with nonce",
        "without events",
        "other event",
        "expired",
        "issued too long ago",
        "issued in the future",
        "without sid and sub",
        "without jti",
        "not a JWT",
        "empty",
    };

    [Theory]
    [MemberData(nameof(RejectedLogoutTokens))]
    public async Task Invalid_logout_token_is_rejected_with_400_and_the_session_survives(string scenario)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var fake = factory.AuthCenter;
        var sid = fake.LastSessionId;
        if (scenario == "replayed jti")
        {
            // The jti was already used by a token that named another session.
            using var used = await PostLogoutTokenAsync(
                factory,
                fake.CreateLogoutToken("someone-else", Guid.NewGuid().ToString(), new LogoutTokenOptions { TokenId = "jti-1" }));
            Assert.Equal(HttpStatusCode.OK, used.StatusCode);
        }

        var token = scenario switch
        {
            "replayed jti" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { TokenId = "jti-1" }),
            "ID token" => fake.LastIdToken,
            "other audience" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { Audience = "another-client" }),
            "other issuer" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { Issuer = "https://evil.test" }),
            "issuer with trailing slash" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { Issuer = FakeAuthCenterServer.Issuer + "/" }),
            "typ JWT" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { TokenType = "JWT" }),
            "HS256" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { SignWithHs256 = true }),
            "alg none" => Unsigned(fake.CreateLogoutToken(ViewerSubject, sid)),
            "with nonce" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { Nonce = "n-0S6_WzA2Mj" }),
            "without events" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { IncludeEvents = false }),
            "other event" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { EventName = "http://schemas.openid.net/event/other" }),
            "expired" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { IssuedAtOffset = TimeSpan.FromMinutes(-4) }),
            "issued too long ago" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { IssuedAtOffset = TimeSpan.FromMinutes(-10), Lifetime = TimeSpan.FromMinutes(20) }),
            "issued in the future" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { IssuedAtOffset = TimeSpan.FromMinutes(5) }),
            "without sid and sub" => fake.CreateLogoutToken(subject: null, sessionId: null),
            "without jti" => fake.CreateLogoutToken(ViewerSubject, sid, new LogoutTokenOptions { IncludeTokenId = false }),
            "not a JWT" => "not-a-token",
            "empty" => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        using var response = await PostLogoutTokenAsync(factory, token);

        await AssertInvalidRequestAsync(response);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task Backchannel_logout_requires_exactly_one_form_encoded_logout_token()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var token = factory.AuthCenter.CreateLogoutToken(ViewerSubject, Guid.NewGuid().ToString());

        using var json = await client.PostAsync(
            AuthCenterDefaults.BackchannelLogoutPath,
            new StringContent(JsonSerializer.Serialize(new { logout_token = token }), Encoding.UTF8, "application/json"));
        using var missing = await client.PostAsync(
            AuthCenterDefaults.BackchannelLogoutPath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        using var duplicated = await client.PostAsync(
            AuthCenterDefaults.BackchannelLogoutPath,
            new FormUrlEncodedContent([new("logout_token", token), new("logout_token", token)]));
        using var get = await client.GetAsync(AuthCenterDefaults.BackchannelLogoutPath);

        await AssertInvalidRequestAsync(json);
        await AssertInvalidRequestAsync(missing);
        await AssertInvalidRequestAsync(duplicated);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    // ---- MFA step-up ---------------------------------------------------------------------------

    [Fact]
    public async Task Privileged_route_without_mfa_answers_a_distinguishable_403_but_other_denials_stay_generic()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var admin = factory.CreateBrowser();
        using var viewer = factory.CreateBrowser();
        using var a = await SignInAsync(factory, admin, PrivilegedSubject);
        using var v = await SignInAsync(factory, viewer, ViewerSubject);

        using var mfaMissing = await admin.GetAsync(PrivilegedProbe);
        using var roleMissing = await viewer.GetAsync(PrivilegedProbe);

        Assert.Equal(HttpStatusCode.Forbidden, mfaMissing.StatusCode);
        var mfaProblem = await mfaMissing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MfaRequiredCode, mfaProblem.GetProperty("code").GetString());
        Assert.Equal("Forbidden", mfaProblem.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, roleMissing.StatusCode);
        var genericProblem = await roleMissing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(genericProblem.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task Step_up_login_sends_acr_values_mfa_and_a_plain_login_does_not()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var plain = await browser.GetAsync(AuthCenterDefaults.LoginPath);
        factory.AuthCenter.Authorize(plain.Headers.Location!, PrivilegedSubject);
        var plainAcr = factory.AuthCenter.LastRequestedAcrValues;
        using var other = await browser.GetAsync(AuthCenterDefaults.LoginPath + "?mfa=REQUIRED");
        factory.AuthCenter.Authorize(other.Headers.Location!, PrivilegedSubject);
        var otherAcr = factory.AuthCenter.LastRequestedAcrValues;
        using var stepUp = await browser.GetAsync(AuthCenterDefaults.LoginPath + "?mfa=required&return_url=%2Fops%2Fdashboard");
        factory.AuthCenter.Authorize(stepUp.Headers.Location!, PrivilegedSubject);

        Assert.Null(plainAcr);
        Assert.Null(otherAcr);
        Assert.Equal(HttpStatusCode.Redirect, stepUp.StatusCode);
        Assert.Contains("no-store", stepUp.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(FakeAuthCenterServer.AcrMfa, factory.AuthCenter.LastRequestedAcrValues);
    }

    [Fact]
    public async Task Satisfying_step_up_replaces_the_session_and_the_old_cookie_stops_working()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var firstCallback = await SignInAsync(factory, browser, PrivilegedSubject);
        var oldCookie = SessionCookie(firstCallback);
        var oldCsrf = await ReadCsrfAsync(browser);
        using var denied = await browser.GetAsync(PrivilegedProbe);

        using var stepUp = await StepUpAsync(factory, browser, PrivilegedSubject, "/ops/finance");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, stepUp.StatusCode);
        Assert.Equal("/ops/finance", stepUp.Headers.Location!.OriginalString);
        var newCookie = SessionCookie(stepUp);
        Assert.NotEqual(oldCookie, newCookie);
        using var privileged = await browser.GetAsync(PrivilegedProbe);
        Assert.Equal(HttpStatusCode.NoContent, privileged.StatusCode);
        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        Assert.True(session.GetProperty("mfa").GetBoolean());
        Assert.NotEqual(oldCsrf, session.GetProperty("csrfToken").GetString());
        using var oldReplay = await SendWithCookieAsync(factory, HttpMethod.Get, AuthCenterDefaults.SessionPath, oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, oldReplay.StatusCode);
    }

    public static TheoryData<string, TokenBehavior> UnsatisfiedStepUps => new()
    {
        { "AuthCenter ignored acr_values", new TokenBehavior { IgnoreAcrValues = true } },
        { "acr mfa without amr mfa", new TokenBehavior { IgnoreAcrValues = true, Acr = FakeAuthCenterServer.AcrMfa, Amr = ["pwd"] } },
        { "amr mfa with acr 1fa", new TokenBehavior { Amr = ["pwd", "otp", "mfa"], Acr = FakeAuthCenterServer.AcrSingleFactor } },
        { "unknown acr", new TokenBehavior { Amr = ["pwd", "otp", "mfa"], Acr = "urn:authcenter:acr:unknown" } },
    };

    [Theory]
    [MemberData(nameof(UnsatisfiedStepUps))]
    public async Task Step_up_whose_acr_does_not_satisfy_the_requirement_is_rejected_and_the_old_session_is_kept(
        string scenario,
        TokenBehavior behavior)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, PrivilegedSubject);
        factory.AuthCenter.Behavior = behavior;

        using var stepUp = await StepUpAsync(factory, browser, PrivilegedSubject, "/ops/finance");

        AssertRejectedCallback(stepUp, scenario);
        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        Assert.False(session.GetProperty("mfa").GetBoolean());
        using var privileged = await browser.GetAsync(PrivilegedProbe);
        Assert.Equal(HttpStatusCode.Forbidden, privileged.StatusCode);
    }

    [Fact]
    public async Task Phishing_resistant_step_up_satisfies_the_mfa_requirement()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        factory.AuthCenter.Behavior = new TokenBehavior { Amr = ["pop", "mfa"], Acr = "urn:authcenter:acr:phr" };
        using var browser = factory.CreateBrowser();

        using var stepUp = await StepUpAsync(factory, browser, PrivilegedSubject, "/ops/finance");
        using var privileged = await browser.GetAsync(PrivilegedProbe);

        Assert.Equal("/ops/finance", stepUp.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NoContent, privileged.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test/steal")]
    [InlineData("//evil.test/steal")]
    [InlineData("/\\evil.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("%2F%2Fevil.test")]
    public async Task Step_up_return_url_outside_the_application_falls_back_to_root(string returnUrl)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var stepUp = await StepUpAsync(factory, browser, PrivilegedSubject, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, stepUp.StatusCode);
        Assert.Equal("/", stepUp.Headers.Location!.OriginalString);
    }

    // ---- access_denied -------------------------------------------------------------------------

    [Fact]
    public async Task Access_denied_from_AuthCenter_redirects_to_its_own_login_error_without_a_session()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, UnprovisionedSubject);

        using var callback = await browser.GetAsync(FakeAuthCenterServer.CallbackErrorPath("access_denied", grant.State));

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(AuthCenterDefaults.AccessDeniedRedirect, callback.Headers.Location!.OriginalString);
        Assert.DoesNotContain("secret-detail", callback.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(string.Empty, factory.AuthCenter.LastIdToken);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Theory]
    [InlineData("server_error")]
    [InlineData("login_required")]
    [InlineData("interaction_required")]
    [InlineData("invalid_request")]
    [InlineData("temporarily_unavailable")]
    public async Task Other_AuthCenter_errors_stay_generic(string error)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, ViewerSubject);

        using var callback = await browser.GetAsync(FakeAuthCenterServer.CallbackErrorPath(error, grant.State));

        AssertRejectedCallback(callback, error);
        Assert.DoesNotContain("secret-detail", callback.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_denied_without_valid_state_stays_generic()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var callback = await browser.GetAsync(FakeAuthCenterServer.CallbackErrorPath("access_denied", "forged"));

        AssertRejectedCallback(callback, "access_denied with forged state");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> StepUpAsync(
        AuthCenterWebApplicationFactory factory,
        HttpClient browser,
        string subject,
        string returnUrl)
    {
        using var login = await browser.GetAsync(
            AuthCenterDefaults.LoginPath + "?mfa=required&return_url=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, subject);
        Assert.Equal(FakeAuthCenterServer.AcrMfa, factory.AuthCenter.LastRequestedAcrValues);
        return await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State));
    }

    private static Task<HttpResponseMessage> PostLogoutTokenAsync(AuthCenterWebApplicationFactory factory, string token)
    {
        // AuthCenter posts server-to-server: no cookie, no CSRF header, no Origin.
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        return client.PostAsync(
            AuthCenterDefaults.BackchannelLogoutPath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));
    }

    private static async Task AssertInvalidRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Single(body.EnumerateObject());
    }

    private static async Task<string?> ReadEndSessionUrlAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(body.EnumerateObject());
        var value = body.GetProperty("endSessionUrl");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];

    private static void AssertSessionCookieDeleted(HttpResponseMessage response) =>
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=;", StringComparison.Ordinal));

    private static async Task<HttpResponseMessage> SendWithCookieAsync(
        AuthCenterWebApplicationFactory factory,
        HttpMethod method,
        string path,
        string cookie)
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static async Task RewriteStoredTicketAsync(
        AuthCenterWebApplicationFactory factory,
        string sessionCookie,
        Action<AuthenticationTicket> change)
    {
        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthCenterDefaults.CookieScheme);
        var envelope = cookie.TicketDataFormat.Unprotect(sessionCookie[(sessionCookie.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        var key = envelope!.Principal.FindFirst("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!.Value;
        var ticket = (await cookie.SessionStore!.RetrieveAsync(key))!;
        change(ticket);
        await cookie.SessionStore.RenewAsync(key, ticket);
    }

    private static string Unsigned(string token)
    {
        var parts = token.Split('.');
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"logout+jwt"}""");
        return header + "." + parts[1] + ".";
    }

    private sealed class UnavailableConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            throw new InvalidOperationException("IDX20803: Unable to obtain configuration.");

        public void RequestRefresh()
        {
        }
    }
}
