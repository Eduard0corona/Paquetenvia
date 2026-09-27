using System.Security.Claims;
using Identity.Endpoints.Authorization;
using Identity.Endpoints.AuthCenter;
using Identity.Endpoints.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Paqueteria.UnitTests.Identity;

public sealed class AuthCenterLogoutAndStepUpUnitTests
{
    private static readonly AuthCenterOptions Settings = new()
    {
        Authority = "https://authcenter.test",
        Issuer = "https://authcenter.test",
        ClientId = "paquetenvia-web-testing",
        ClientSecret = new string('s', 40),
        PublicOrigin = "https://app.paquetenvia.test/",
        SessionLifetimeMinutes = 480,
    };

    [Fact]
    public void End_session_url_carries_only_the_id_token_hint_and_the_exact_post_logout_redirect_uri()
    {
        var url = AuthCenterEndSession.BuildUrl("https://authcenter.test/oauth/logout", "header.payload.signature", Settings);

        var uri = new Uri(url!);
        Assert.Equal("https://authcenter.test/oauth/logout", uri.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(2, query.Count);
        Assert.Equal("header.payload.signature", query["id_token_hint"].ToString());
        Assert.Equal("https://app.paquetenvia.test/login", query["post_logout_redirect_uri"].ToString());
    }

    [Theory]
    [InlineData("https://authcenter.test/oauth/logout", null)]
    [InlineData("https://authcenter.test/oauth/logout", "")]
    [InlineData(null, "id-token")]
    [InlineData("", "id-token")]
    [InlineData("http://authcenter.test/oauth/logout", "id-token")]
    [InlineData("https://evil.test/oauth/logout", "id-token")]
    [InlineData("https://authcenter.test:8443/oauth/logout", "id-token")]
    [InlineData("https://user:pass@authcenter.test/oauth/logout", "id-token")]
    [InlineData("https://authcenter.test/oauth/logout#fragment", "id-token")]
    [InlineData("/oauth/logout", "id-token")]
    public void End_session_url_is_null_without_an_id_token_or_a_usable_AuthCenter_endpoint(string? endpoint, string? idToken)
    {
        Assert.Null(AuthCenterEndSession.BuildUrl(endpoint, idToken, Settings));
    }

    [Theory]
    [InlineData("urn:authcenter:acr:mfa", new[] { "pwd", "otp", "mfa" }, true)]
    [InlineData("urn:authcenter:acr:phr", new[] { "pop", "mfa" }, true)]
    [InlineData("urn:authcenter:acr:1fa", new[] { "pwd", "otp", "mfa" }, false)]
    [InlineData("urn:authcenter:acr:mfa", new[] { "pwd" }, false)]
    [InlineData("urn:authcenter:acr:mfa", new string[0], false)]
    [InlineData(null, new[] { "pwd", "mfa" }, false)]
    [InlineData("URN:AUTHCENTER:ACR:MFA", new[] { "mfa" }, false)]
    public void Step_up_requires_an_mfa_class_acr_and_mfa_in_amr(string? acr, string[] amr, bool expected)
    {
        var claims = amr.Select(method => new Claim("amr", method)).ToList();
        if (acr is not null)
        {
            claims.Add(new Claim("acr", acr));
        }

        Assert.Equal(expected, AuthCenterOpenIdConnectEvents.SatisfiesMfa(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
    }

    [Fact]
    public void Two_acr_values_do_not_satisfy_the_step_up()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("acr", "urn:authcenter:acr:mfa"), new Claim("acr", "urn:authcenter:acr:1fa"), new Claim("amr", "mfa")],
            "test"));

        Assert.False(AuthCenterOpenIdConnectEvents.SatisfiesMfa(principal));
    }

    [Fact]
    public void Only_a_denial_whose_sole_unmet_requirement_is_mfa_is_distinguishable()
    {
        Assert.True(IdentityProblemDetails.IsOnlyMissingMfa(AuthorizationFailure.Failed([new RequireMfaRequirement()])));
        Assert.False(IdentityProblemDetails.IsOnlyMissingMfa(AuthorizationFailure.Failed(
            [new RequireMfaRequirement(), new ActiveIdentityRequirement()])));
        Assert.False(IdentityProblemDetails.IsOnlyMissingMfa(AuthorizationFailure.Failed([new ActiveIdentityRequirement()])));
        Assert.False(IdentityProblemDetails.IsOnlyMissingMfa(AuthorizationFailure.ExplicitFail()));
        Assert.False(IdentityProblemDetails.IsOnlyMissingMfa(null));
    }

    [Fact]
    public async Task Termination_store_ends_by_sid_and_by_subject_only_before_the_logout_moment()
    {
        var store = NewStore();
        // Moments are kept to the millisecond, like the sign-in moment stored in the ticket.
        var loggedOutAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await store.EndSessionAsync("sid-1", CancellationToken.None);
        await store.EndSubjectSessionsAsync("subject-2", loggedOutAt, CancellationToken.None);

        Assert.True(await store.IsEndedAsync("sid-1", "subject-1", loggedOutAt, CancellationToken.None));
        Assert.False(await store.IsEndedAsync("sid-other", "subject-1", loggedOutAt, CancellationToken.None));
        Assert.True(await store.IsEndedAsync("sid-3", "subject-2", loggedOutAt.AddMilliseconds(-1), CancellationToken.None));
        Assert.True(await store.IsEndedAsync(null, "subject-2", loggedOutAt, CancellationToken.None));
        Assert.True(await store.IsEndedAsync(null, "subject-2", signedInAt: null, CancellationToken.None));
        Assert.False(await store.IsEndedAsync(null, "subject-2", loggedOutAt.AddMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Termination_store_keeps_the_latest_subject_logout_moment()
    {
        var store = NewStore();
        var later = DateTimeOffset.UtcNow;

        await store.EndSubjectSessionsAsync("subject", later, CancellationToken.None);
        await store.EndSubjectSessionsAsync("subject", later.AddMinutes(-1), CancellationToken.None);

        Assert.True(await store.IsEndedAsync(null, "subject", later.AddMilliseconds(-1), CancellationToken.None));
    }

    [Fact]
    public async Task Termination_store_accepts_each_logout_token_id_once()
    {
        var store = NewStore();
        var retainUntil = DateTimeOffset.UtcNow.AddMinutes(7);

        Assert.True(await store.TryRegisterLogoutTokenAsync("jti-1", retainUntil, CancellationToken.None));
        Assert.False(await store.TryRegisterLogoutTokenAsync("jti-1", retainUntil, CancellationToken.None));
        Assert.True(await store.TryRegisterLogoutTokenAsync("jti-2", retainUntil, CancellationToken.None));
    }

    private static DistributedCacheAuthCenterSessionTerminationStore NewStore() => new(
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
        Microsoft.Extensions.Options.Options.Create(Settings));
}
