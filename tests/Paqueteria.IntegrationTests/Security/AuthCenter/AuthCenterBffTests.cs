using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Endpoints.AuthCenter;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

public sealed class AuthCenterBffTests
{
    private const string ViewerSubject = "mock-subject-active-viewer";
    private const string PrivilegedSubject = "mock-subject-platform-admin-mfa";
    private const string UnprovisionedSubject = "mock-subject-not-provisioned";
    private const string ActiveProbe = "/__tests/security/authenticated";
    private const string PrivilegedProbe = "/__tests/security/privileged";
    private const string WriteProbe = "/__tests/security/write";

    [Fact]
    public async Task Login_redirects_to_AuthCenter_with_code_flow_PKCE_S256_exact_redirect_and_scopes()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var login = await browser.GetAsync("/auth/login?return_url=%2Fops%2Fdashboard");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var location = login.Headers.Location!;
        factory.AuthCenter.Authorize(location, ViewerSubject);
        Assert.DoesNotContain("client_secret", location.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeAuthCenterServer.ClientSecret, location.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(login.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_sign_in_issues_only_a_host_prefixed_secure_http_only_session_cookie()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var callback = await SignInAsync(factory, browser, ViewerSubject, "/ops/dashboard");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/ops/dashboard", callback.Headers.Location!.OriginalString);
        var sessionCookie = Assert.Single(
            callback.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal));
        Assert.StartsWith("__Host-", sessionCookie, StringComparison.Ordinal);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var allCookies = string.Join('\n', callback.Headers.GetValues("Set-Cookie"));
        AssertNoTokens(factory, allCookies);
        Assert.Empty(factory.AuthCenter.TokenRequestFailures);
    }

    [Fact]
    public async Task Session_endpoint_returns_csrf_and_profile_but_never_tokens_or_subject()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);

        using var response = await browser.GetAsync(AuthCenterDefaults.SessionPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var raw = await response.Content.ReadAsStringAsync();
        AssertNoTokens(factory, raw);
        Assert.DoesNotContain(ViewerSubject, raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.True(root.GetProperty("authenticated").GetBoolean());
        Assert.True(root.GetProperty("authorized").GetBoolean());
        Assert.False(root.GetProperty("mfa").GetBoolean());
        Assert.True(root.GetProperty("csrfToken").GetString()!.Length >= 32);
        Assert.Matches("^[A-Za-z0-9_-]{32}$", root.GetProperty("sessionNamespace").GetString()!);
        Assert.Equal("Usuario Sintético", root.GetProperty("user").GetProperty("name").GetString());
        // The fake mirrors the real AuthCenter ID token (sid, acr, azp, boolean email_verified); none
        // of those protocol claims is echoed to the browser.
        Assert.DoesNotContain(factory.AuthCenter.SessionId, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("urn:authcenter:acr", raw, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("sid", out var sid), sid.ToString());
        Assert.False(root.TryGetProperty("acr", out var acr), acr.ToString());
    }

    [Fact]
    public async Task Anonymous_session_request_returns_generic_401_without_redirect()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var response = await browser.GetAsync(AuthCenterDefaults.SessionPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Cookie_session_authorizes_tenant_policies_from_the_Paquetenvia_database_not_the_token()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);

        using var active = await browser.GetAsync(ActiveProbe);
        using var privileged = await browser.GetAsync(PrivilegedProbe);

        // The fake IdP emits AUTHCENTER_SUPERADMIN roles and permissions; they must be ignored.
        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, privileged.StatusCode);
        Assert.Contains(ViewerSubject, factory.Resolver.ResolvedSubjects);
    }

    [Fact]
    public async Task Mfa_is_recognized_only_from_the_validated_id_token_amr_claim()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        factory.AuthCenter.Behavior = new TokenBehavior { Amr = ["pwd", "mfa"] };
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, PrivilegedSubject);

        using var privileged = await browser.GetAsync(PrivilegedProbe);

        Assert.Equal(HttpStatusCode.NoContent, privileged.StatusCode);
    }

    [Fact]
    public async Task Privileged_role_without_amr_mfa_is_forbidden()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, PrivilegedSubject);

        using var privileged = await browser.GetAsync(PrivilegedProbe);

        Assert.Equal(HttpStatusCode.Forbidden, privileged.StatusCode);
    }

    [Fact]
    public async Task Unprovisioned_subject_is_authenticated_but_unauthorized_and_nothing_is_created()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, UnprovisionedSubject);

        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        using var active = await browser.GetAsync(ActiveProbe);

        Assert.False(session.GetProperty("authorized").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, active.StatusCode);
    }

    [Fact]
    public async Task Suspension_in_Paquetenvia_takes_effect_on_the_next_request_without_new_login()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        using var before = await browser.GetAsync(ActiveProbe);

        factory.Resolver.Suspended[ViewerSubject] = true;
        using var after = await browser.GetAsync(ActiveProbe);

        Assert.Equal(HttpStatusCode.NoContent, before.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    public static TheoryData<string, TokenBehavior> RejectedIdTokens => new()
    {
        { "wrong issuer", new TokenBehavior { OverrideIssuer = "https://evil.test" } },
        { "issuer with trailing slash", new TokenBehavior { OverrideIssuer = FakeAuthCenterServer.Issuer + "/" } },
        { "wrong audience", new TokenBehavior { OverrideAudience = "another-client" } },
        { "wrong nonce", new TokenBehavior { OverrideNonce = "replayed-nonce" } },
        { "expired", new TokenBehavior { Expired = true } },
        { "HS256 signature", new TokenBehavior { SignWithHs256 = true } },
        { "foreign RSA key", new TokenBehavior { SignWithForeignKey = true } },
        { "PKCE verifier mismatch", new TokenBehavior { OverrideExpectedCodeChallenge = "not-the-challenge" } },
    };

    [Theory]
    [MemberData(nameof(RejectedIdTokens))]
    public async Task Invalid_callback_is_rejected_without_a_session(string scenario, TokenBehavior behavior)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        factory.AuthCenter.Behavior = behavior;
        using var browser = factory.CreateBrowser();

        using var callback = await SignInAsync(factory, browser, ViewerSubject);

        AssertRejectedCallback(callback, scenario);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    public static TheoryData<string, string?> RejectedAuthorizationResponseIssuers => new()
    {
        { "authorization response omits iss", null },
        { "empty iss", string.Empty },
        { "wrong iss", "https://evil.test" },
        { "iss with trailing slash", FakeAuthCenterServer.Issuer + "/" },
        { "iss with different case", FakeAuthCenterServer.Issuer.ToUpperInvariant() },
    };

    [Theory]
    [MemberData(nameof(RejectedAuthorizationResponseIssuers))]
    public async Task Authorization_response_without_the_exact_issuer_is_rejected_before_code_redemption(
        string scenario,
        string? issuer)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, ViewerSubject);

        using var callback = await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State, issuer));

        AssertRejectedCallback(callback, scenario);
        Assert.Equal(string.Empty, factory.AuthCenter.LastIdToken);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task Tampered_state_is_rejected()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var login = await browser.GetAsync("/auth/login");
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, ViewerSubject);

        using var callback = await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State + "x"));

        AssertRejectedCallback(callback, "tampered state");
    }

    [Fact]
    public async Task Callback_without_the_correlation_cookie_is_rejected()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var loginBrowser = factory.CreateBrowser();
        using var attackerBrowser = factory.CreateBrowser();
        using var login = await loginBrowser.GetAsync("/auth/login");
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, ViewerSubject);

        using var callback = await attackerBrowser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State));

        AssertRejectedCallback(callback, "login CSRF / missing correlation");
    }

    [Fact]
    public async Task Redirect_uri_is_pinned_to_the_public_origin_and_an_altered_one_is_rejected_by_the_IdP()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        browser.DefaultRequestHeaders.Host = "internal-api.cluster.local";

        using var login = await browser.GetAsync("/auth/login");
        var location = login.Headers.Location!;
        Assert.Contains(
            "redirect_uri=" + Uri.EscapeDataString(FakeAuthCenterServer.RedirectUri),
            location.Query,
            StringComparison.Ordinal);

        var altered = new Uri(location.ToString().Replace(
            Uri.EscapeDataString(FakeAuthCenterServer.RedirectUri),
            Uri.EscapeDataString("https://evil.test/signin-authcenter"),
            StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => factory.AuthCenter.Authorize(altered, ViewerSubject));
    }

    [Theory]
    [InlineData("https://evil.test/steal")]
    [InlineData("//evil.test/steal")]
    [InlineData("/\\evil.test")]
    [InlineData("javascript:alert(1)")]
    public async Task Return_url_outside_the_application_falls_back_to_root(string returnUrl)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var callback = await SignInAsync(factory, browser, ViewerSubject, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Writes_require_the_session_bound_csrf_header()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);

        using var missing = await browser.PostAsync(WriteProbe, null);
        using var wrong = await SendWriteAsync(browser, WriteProbe, csrf + "x");
        using var valid = await SendWriteAsync(browser, WriteProbe, csrf);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
    }

    [Fact]
    public async Task Csrf_token_of_one_session_is_useless_in_another_session()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var first = factory.CreateBrowser();
        using var second = factory.CreateBrowser();
        using var a = await SignInAsync(factory, first, ViewerSubject);
        using var b = await SignInAsync(factory, second, ViewerSubject);
        var firstCsrf = await ReadCsrfAsync(first);

        using var response = await SendWriteAsync(second, WriteProbe, firstCsrf);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData("null")]
    [InlineData("http://app.paquetenvia.test")]
    public async Task Cross_origin_requests_are_not_authenticated_by_the_cookie(string origin)
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);

        using var read = new HttpRequestMessage(HttpMethod.Get, ActiveProbe);
        read.Headers.TryAddWithoutValidation("Origin", origin);
        using var readResponse = await browser.SendAsync(read);
        using var write = new HttpRequestMessage(HttpMethod.Post, WriteProbe);
        write.Headers.TryAddWithoutValidation("Origin", origin);
        write.Headers.TryAddWithoutValidation(AuthCenterDefaults.CsrfHeaderName, csrf);
        using var writeResponse = await browser.SendAsync(write);

        Assert.Equal(HttpStatusCode.Unauthorized, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, writeResponse.StatusCode);
    }

    [Fact]
    public async Task Same_origin_request_with_origin_header_is_accepted()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);

        using var write = new HttpRequestMessage(HttpMethod.Post, WriteProbe);
        write.Headers.TryAddWithoutValidation("Origin", FakeAuthCenterServer.PublicOrigin);
        write.Headers.TryAddWithoutValidation(AuthCenterDefaults.CsrfHeaderName, csrf);
        using var response = await browser.SendAsync(write);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Bearer_credentials_are_not_accepted_when_AuthCenter_is_the_provider()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        browser.DefaultRequestHeaders.Authorization = new("Bearer", "active-viewer");

        using var response = await browser.GetAsync(ActiveProbe);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signalr_negotiate_uses_the_cookie_and_requires_csrf()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);
        const string negotiate = "/__tests/hubs/security/negotiate?negotiateVersion=1";

        using var withoutCsrf = await browser.PostAsync(negotiate, null);
        using var withCsrf = await SendWriteAsync(browser, negotiate, csrf);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withCsrf.StatusCode);
    }

    [Fact]
    public async Task Logout_requires_csrf_revokes_the_refresh_token_and_destroys_the_session()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var _ = await SignInAsync(factory, browser, ViewerSubject);
        var csrf = await ReadCsrfAsync(browser);
        var refreshToken = factory.AuthCenter.LastRefreshToken;

        using var withoutCsrf = await browser.PostAsync(AuthCenterDefaults.LogoutPath, null);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutCsrf.StatusCode);
        Assert.Empty(factory.AuthCenter.RevokedTokens);

        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(refreshToken, Assert.Single(factory.AuthCenter.RevokedTokens));
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=;", StringComparison.Ordinal));
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task Replayed_session_cookie_is_useless_after_logout()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();
        using var callback = await SignInAsync(factory, browser, ViewerSubject);
        var stolenCookie = callback.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];
        var csrf = await ReadCsrfAsync(browser);
        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        using var replay = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthCenterDefaults.SessionPath);
        request.Headers.TryAddWithoutValidation("Cookie", stolenCookie);
        using var response = await replay.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Remote_failure_page_is_generic()
    {
        using var factory = new AuthCenterWebApplicationFactory();
        using var browser = factory.CreateBrowser();

        using var response = await browser.GetAsync(
            "/signin-authcenter?error=access_denied&error_description=secret-detail&state=forged");

        AssertRejectedCallback(response, "IdP error");
        Assert.DoesNotContain("secret-detail", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    public static TheoryData<string, string?> InvalidConfiguration => new()
    {
        { "AuthCenter:Authority", "http://authcenter.test" },
        { "AuthCenter:Authority", null },
        { "AuthCenter:Issuer", null },
        { "AuthCenter:ClientId", null },
        { "AuthCenter:ClientSecret", null },
        { "AuthCenter:ClientSecret", "too-short" },
        { "AuthCenter:PublicOrigin", null },
        { "AuthCenter:PublicOrigin", "http://app.paquetenvia.test" },
        { "AuthCenter:PublicOrigin", "https://app.paquetenvia.test/path" },
        { "AuthCenter:SessionLifetimeMinutes", "0" },
        { "AuthCenter:SessionLifetimeMinutes", "1441" },
    };

    [Theory]
    [MemberData(nameof(InvalidConfiguration))]
    public void Invalid_AuthCenter_configuration_fails_at_startup(string key, string? value)
    {
        using var factory = new AuthCenterWebApplicationFactory(new Dictionary<string, string?> { [key] = value });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private static async Task<HttpResponseMessage> SignInAsync(
        AuthCenterWebApplicationFactory factory,
        HttpClient browser,
        string subject,
        string? returnUrl = null)
    {
        var path = returnUrl is null
            ? AuthCenterDefaults.LoginPath
            : AuthCenterDefaults.LoginPath + "?return_url=" + Uri.EscapeDataString(returnUrl);
        using var login = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, subject);
        return await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State));
    }

    private static async Task<string> ReadCsrfAsync(HttpClient browser)
    {
        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        return session.GetProperty("csrfToken").GetString()!;
    }

    private static Task<HttpResponseMessage> SendWriteAsync(HttpClient browser, string path, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(AuthCenterDefaults.CsrfHeaderName, csrf);
        return browser.SendAsync(request);
    }

    private static void AssertRejectedCallback(HttpResponseMessage callback, string scenario)
    {
        Assert.True(callback.StatusCode == HttpStatusCode.Redirect, scenario);
        Assert.Equal(AuthCenterDefaults.RemoteFailureRedirect, callback.Headers.Location!.OriginalString);
        var cookies = callback.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(cookies, cookie =>
            cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal) &&
            !cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=;", StringComparison.Ordinal));
    }

    private static void AssertNoTokens(AuthCenterWebApplicationFactory factory, string text)
    {
        foreach (var token in new[]
                 {
                     factory.AuthCenter.LastAccessToken,
                     factory.AuthCenter.LastRefreshToken,
                     factory.AuthCenter.LastIdToken,
                 }.Where(token => token.Length > 0))
        {
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
    }
}
