using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Endpoints.AuthCenter;
using Microsoft.Extensions.DependencyInjection;
using Organizations.Application.Registration;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// REG-002 at the real BFF callback over PostgreSQL: an administrator's pending membership becomes a
/// membership at the first sign-in of a new account and at the next sign-in of an existing one, only for
/// a verified email that matches, and never twice. Each test uses its own subject, email and organization.
/// </summary>
public sealed class AuthCenterPendingMembershipTests(AuthCenterPostgreSqlWebApplicationFactory factory)
    : IClassFixture<AuthCenterPostgreSqlWebApplicationFactory>
{
    [Fact]
    public async Task A_new_account_gets_the_membership_at_its_first_verified_sign_in()
    {
        var (organization, email) = await PendingAsync("BUSINESS_OPERATOR");
        var subject = NewSubject();
        using var browser = factory.CreateBrowser();

        using (var callback = await SignInAsync(browser, subject, email))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.DoesNotContain("error=", callback.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        var context = Assert.Single((await browser.GetFromJsonAsync<JsonElement>("/api/v1/me/organization-contexts")).EnumerateArray());
        Assert.Equal(organization.ToString("D"), context.GetProperty("organization_id").GetString());
        Assert.Equal("BUSINESS_OPERATOR", context.GetProperty("role").GetString());
        Assert.True(context.GetProperty("is_default").GetBoolean());
        Assert.Equal("ACCEPTED", await EntryStatusAsync(organization));
    }

    [Fact]
    public async Task An_existing_account_gets_the_membership_at_its_next_sign_in_and_only_once()
    {
        var subject = NewSubject();
        var email = NewEmail();
        using (var first = factory.CreateBrowser())
        using (await SignInAsync(first, subject, email))
        {
            Assert.Equal(0, (await first.GetFromJsonAsync<JsonElement>("/api/v1/me/organization-contexts")).GetArrayLength());
        }

        var (organization, _) = await PendingAsync("VIEWER", email);
        using var browser = factory.CreateBrowser();
        using (await SignInAsync(browser, subject, email))
        {
        }

        var context = Assert.Single((await browser.GetFromJsonAsync<JsonElement>("/api/v1/me/organization-contexts")).EnumerateArray());
        Assert.Equal(organization.ToString("D"), context.GetProperty("organization_id").GetString());

        using (var again = factory.CreateBrowser())
        using (await SignInAsync(again, subject, email))
        {
        }

        Assert.Equal(1L, await factory.AdminScalarAsync<long>(
            """
            SELECT count(*) FROM organizations.organization_memberships m
            JOIN identity.users u ON u.id=m.user_id
            WHERE u.identity_subject=@subject AND m.organization_id=@organization
            """,
            ("subject", subject), ("organization", organization)));
    }

    [Fact]
    public async Task An_unverified_email_or_another_email_applies_nothing()
    {
        var (organization, email) = await PendingAsync("VIEWER");
        var subject = NewSubject();
        factory.AuthCenter.Behavior = new TokenBehavior { Email = email, EmailVerified = false };
        try
        {
            using var unverified = factory.CreateBrowser();
            using var refused = await SignInAsync(unverified, subject, email);
            Assert.Equal(AuthCenterDefaults.EmailNotVerifiedRedirect, refused.Headers.Location!.OriginalString);
        }
        finally
        {
            factory.AuthCenter.Behavior = new TokenBehavior();
        }

        Assert.Equal("PENDING", await EntryStatusAsync(organization));
        Assert.Equal(0L, await factory.AdminScalarAsync<long>(
            "SELECT count(*) FROM identity.users WHERE identity_subject=@subject", ("subject", subject)));

        using var other = factory.CreateBrowser();
        using (await SignInAsync(other, subject, NewEmail()))
        {
        }

        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/v1/me/organization-contexts")).GetArrayLength());
        Assert.Equal("PENDING", await EntryStatusAsync(organization));
    }

    /// <summary>A BUSINESS organization whose administrator added <paramref name="email"/> through the real service.</summary>
    private async Task<(Guid Organization, string Email)> PendingAsync(string role, string? email = null)
    {
        email ??= NewEmail();
        var organization = Guid.NewGuid();
        var admin = Guid.NewGuid();
        await factory.AdminExecuteAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'R2','R2','BUSINESS');
            INSERT INTO identity.users(id,identity_subject) VALUES (@admin,@subject);
            INSERT INTO organizations.organization_memberships(user_id,organization_id,role) VALUES (@admin,@org,'BUSINESS_ADMIN');
            """,
            ("org", organization), ("admin", admin), ("subject", $"reg002-admin-{admin:N}"));
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPendingMembershipService>().AddAsync(
            new AddPendingMembershipCommand(admin, organization, $"reg002-bff-{Guid.NewGuid():N}", email, role, null),
            CancellationToken.None);
        Assert.Equal(PendingMembershipOutcome.Succeeded, result.Outcome);
        return (organization, email);
    }

    private Task<string> EntryStatusAsync(Guid organization) => factory.AdminScalarAsync<string>(
        "SELECT string_agg(status, ',') FROM organizations.pending_memberships WHERE organization_id=@org",
        ("org", organization));

    private static string NewSubject() => $"authcenter-reg002-{Guid.NewGuid():N}";

    private static string NewEmail() => $"reg002-bff-{Guid.NewGuid():N}@paquetenvia.test";

    private async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string subject, string email)
    {
        var behavior = factory.AuthCenter.Behavior;
        factory.AuthCenter.Behavior = behavior with { Email = email };
        try
        {
            using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var grant = factory.AuthCenter.Authorize(login.Headers.Location!, subject);
            return await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State));
        }
        finally
        {
            factory.AuthCenter.Behavior = behavior;
        }
    }
}
