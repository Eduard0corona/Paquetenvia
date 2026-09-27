using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Identity.Endpoints.AuthCenter;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// AUTH-OPEN-REGISTRATION and AUTH-EMAIL-VERIFIED-REQUIRED over the real BFF callback and PostgreSQL:
/// a verified first sign-in creates exactly one user with no organization, an unverified one creates
/// nothing and is refused specifically, and an existing subject is never modified. Every test uses its
/// own subject, so the shared database needs no cleanup.
/// </summary>
public sealed class AuthCenterOpenRegistrationTests(AuthCenterPostgreSqlWebApplicationFactory factory)
    : IClassFixture<AuthCenterPostgreSqlWebApplicationFactory>
{
    [Theory]
    [InlineData("false")]
    [InlineData("string-true")]
    [InlineData("string-yes")]
    [InlineData("omitted")]
    public async Task Unverified_email_is_refused_specifically_and_creates_no_user(string variant)
    {
        var subject = NewSubject();
        factory.AuthCenter.Behavior = variant switch
        {
            "false" => new TokenBehavior { EmailVerified = false },
            // The JSON string "true" is not the JSON boolean true.
            "string-true" => new TokenBehavior { EmailVerified = "true" },
            "string-yes" => new TokenBehavior { EmailVerified = "yes" },
            _ => new TokenBehavior { OmitEmailVerified = true },
        };
        try
        {
            using var browser = factory.CreateBrowser();

            using var callback = await SignInAsync(browser, subject);

            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.Equal(AuthCenterDefaults.EmailNotVerifiedRedirect, callback.Headers.Location!.OriginalString);
            Assert.DoesNotContain(
                callback.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
                cookie => cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal));
            Assert.Equal(0L, await UserCountAsync(subject));
            using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }
        finally
        {
            factory.AuthCenter.Behavior = new TokenBehavior();
        }
    }

    [Fact]
    public async Task Verified_first_sign_in_creates_one_user_without_organizations_and_a_second_sign_in_reuses_it()
    {
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();

        using (var callback = await SignInAsync(browser, subject))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.NotEqual(AuthCenterDefaults.EmailNotVerifiedRedirect, callback.Headers.Location!.OriginalString);
        }

        Assert.Equal(1L, await UserCountAsync(subject));
        var userId = await factory.AdminScalarAsync<Guid>(
            "SELECT id FROM identity.users WHERE identity_subject=@subject", ("subject", subject));
        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        Assert.True(session.GetProperty("authorized").GetBoolean());

        using var contexts = await browser.GetAsync("/api/v1/me/organization-contexts");
        Assert.Equal(HttpStatusCode.OK, contexts.StatusCode);
        Assert.Equal(0, (await contexts.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

        using var second = factory.CreateBrowser();
        using (var callback = await SignInAsync(second, subject))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        }

        Assert.Equal(1L, await UserCountAsync(subject));
        Assert.Equal(userId, await factory.AdminScalarAsync<Guid>(
            "SELECT id FROM identity.users WHERE identity_subject=@subject", ("subject", subject)));
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_of_one_subject_create_exactly_one_user()
    {
        var subject = NewSubject();
        var browsers = Enumerable.Range(0, 6).Select(_ => factory.CreateBrowser()).ToArray();
        try
        {
            // The authorization redirects are prepared first so the callbacks themselves race.
            var callbacks = new List<Uri>();
            foreach (var browser in browsers)
            {
                using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
                var grant = factory.AuthCenter.Authorize(login.Headers.Location!, subject);
                callbacks.Add(new Uri(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State), UriKind.RelativeOrAbsolute));
            }

            var responses = await Task.WhenAll(browsers.Select((browser, index) => browser.GetAsync(callbacks[index])));
            try
            {
                Assert.All(responses, response => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode));
                Assert.All(responses, response =>
                    Assert.DoesNotContain("error=", response.Headers.Location!.OriginalString, StringComparison.Ordinal));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            Assert.Equal(1L, await UserCountAsync(subject));
        }
        finally
        {
            foreach (var browser in browsers)
            {
                browser.Dispose();
            }
        }
    }

    [Fact]
    public async Task An_existing_suspended_subject_is_not_reactivated_or_relinked_by_signing_in()
    {
        var subject = NewSubject();
        var userId = Guid.NewGuid();
        await factory.AdminExecuteAsync(
            "INSERT INTO identity.users(id,identity_subject,status) VALUES (@id,@subject,'SUSPENDED')",
            ("id", userId), ("subject", subject));
        using var browser = factory.CreateBrowser();

        using (var callback = await SignInAsync(browser, subject))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        }

        Assert.Equal($"1|SUSPENDED|{userId:D}", await factory.AdminScalarAsync<string>(
            "SELECT count(*) || '|' || max(status) || '|' || max(id::text) FROM identity.users WHERE identity_subject=@subject",
            ("subject", subject)));
        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        Assert.False(session.GetProperty("authorized").GetBoolean());
    }

    [Fact]
    public async Task A_new_user_creates_a_business_through_the_bff_and_operates_it()
    {
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();
        using (await SignInAsync(browser, subject))
        {
        }

        var csrf = (await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath))
            .GetProperty("csrfToken").GetString()!;

        // Without the session-bound CSRF header the write is anonymous.
        using (var withoutCsrf = await PostOnboardingAsync(browser, null, "reg001-bff-key-0000001"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, withoutCsrf.StatusCode);
        }

        using var created = await PostOnboardingAsync(browser, csrf, "reg001-bff-key-0000001");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal("BUSINESS_ADMIN", body.GetProperty("role").GetString());
        var organizationId = body.GetProperty("organization_id").GetString()!;

        var contexts = await browser.GetFromJsonAsync<JsonElement>("/api/v1/me/organization-contexts");
        var context = Assert.Single(contexts.EnumerateArray());
        Assert.Equal(organizationId, context.GetProperty("organization_id").GetString());
        Assert.Equal("BUSINESS_ADMIN", context.GetProperty("role").GetString());
        Assert.True(context.GetProperty("is_default").GetBoolean());
    }

    private static string NewSubject() => $"authcenter-reg001-{Guid.NewGuid():N}";

    private Task<long> UserCountAsync(string subject) => factory.AdminScalarAsync<long>(
        "SELECT count(*) FROM identity.users WHERE identity_subject=@subject", ("subject", subject));

    private async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string subject)
    {
        using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var grant = factory.AuthCenter.Authorize(login.Headers.Location!, subject);
        return await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State));
    }

    private static Task<HttpResponseMessage> PostOnboardingAsync(HttpClient browser, string? csrf, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/onboarding/organizations")
        {
            Content = new StringContent(
                """{"organization_type":"BUSINESS","legal_name":"Negocio BFF SA","display_name":"Negocio BFF"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (csrf is not null)
        {
            request.Headers.TryAddWithoutValidation(AuthCenterDefaults.CsrfHeaderName, csrf);
        }

        return browser.SendAsync(request);
    }
}
