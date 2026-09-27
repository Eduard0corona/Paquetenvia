using System.Security.Claims;
using Identity.Application.Authentication;
using Identity.Application.Bootstrap;
using Identity.Endpoints.AuthCenter;
using Identity.Endpoints.Security;
using Identity.Endpoints.Session;
using Organizations.Application.Registration;
using Organizations.Endpoints.Authorization;
using Organizations.Infrastructure.Registration;
using Paqueteria.Domain.Tenancy;

namespace Paqueteria.UnitTests.Organizations;

/// <summary>
/// REG-001 rules that need no database: the D5 platform operations, the self-service authorizer,
/// the derived organization id, and the verified-email evidence from the ID token to the session.
/// </summary>
public sealed class SelfServiceRegistrationTests
{
    [Fact]
    public void Ally_decisions_admit_only_platform_admin_with_mfa()
    {
        foreach (var capability in new[]
                 {
                     TenantCapabilities.ListPendingAllyOrganizations, TenantCapabilities.DecideAllyOrganization,
                 })
        {
            var grant = Assert.Single(capability.Grants);
            Assert.Equal(new TenantCapabilityGrant(OrganizationRole.PlatformAdmin, true), grant);
            Assert.Equal(TenantCapabilityDecision.MfaRequired, capability.Evaluate([OrganizationRole.PlatformAdmin], false));
            Assert.Equal(TenantCapabilityDecision.Allowed, capability.Evaluate([OrganizationRole.PlatformAdmin], true));
            foreach (var role in Enum.GetValues<OrganizationRole>().Where(role => role != OrganizationRole.PlatformAdmin))
            {
                Assert.Equal(TenantCapabilityDecision.Forbidden, capability.Evaluate([role], true));
            }
        }
    }

    [Fact]
    public void The_self_service_authorizer_requires_a_verified_email_and_a_linked_user()
    {
        var authorizer = new VerifiedEmailSelfServiceOrganizationAuthorizer();
        var command = new CreateSelfServiceOrganizationCommand(
            Guid.NewGuid(), true, "reg001-unit-key-0001", "BUSINESS", "Negocio", "Negocio", null);

        Assert.True(authorizer.IsAuthorized(command));
        Assert.False(authorizer.IsAuthorized(command with { EmailVerified = false }));
        Assert.False(authorizer.IsAuthorized(command with { UserId = Guid.Empty }));
    }

    [Fact]
    public void The_organization_id_is_derived_from_the_user_and_the_idempotency_key_only()
    {
        var user = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var other = Guid.Parse("22222222-2222-4222-8222-222222222222");

        var id = PostgreSqlSelfServiceRegistrationService.DeriveOrganizationId(user, "reg001-unit-key-0001");

        Assert.Equal(id, PostgreSqlSelfServiceRegistrationService.DeriveOrganizationId(user, "reg001-unit-key-0001"));
        Assert.NotEqual(id, PostgreSqlSelfServiceRegistrationService.DeriveOrganizationId(user, "reg001-unit-key-0002"));
        Assert.NotEqual(id, PostgreSqlSelfServiceRegistrationService.DeriveOrganizationId(other, "reg001-unit-key-0001"));
        Assert.NotEqual(Guid.Empty, id);
    }

    [Theory]
    [InlineData("Negocio", 80, true)]
    [InlineData("", 80, false)]
    [InlineData(" Negocio", 80, false)]
    [InlineData("Negocio\u0007", 80, false)]
    [InlineData("N", 0, false)]
    public void Names_follow_the_AI05_shape(string value, int maximum, bool expected) =>
        Assert.Equal(expected, PostgreSqlSelfServiceRegistrationService.IsValidName(value, maximum));

    [Theory]
    [InlineData(new[] { "true" }, ClaimValueTypes.Boolean, true)]
    [InlineData(new[] { "True" }, ClaimValueTypes.Boolean, true)]
    [InlineData(new[] { "false" }, ClaimValueTypes.Boolean, false)]
    [InlineData(new[] { "true", "true" }, ClaimValueTypes.Boolean, false)]
    [InlineData(new string[0], ClaimValueTypes.Boolean, false)]
    // A JSON string is not the JSON boolean true, whatever it says.
    [InlineData(new[] { "true" }, ClaimValueTypes.String, false)]
    [InlineData(new[] { "True" }, ClaimValueTypes.String, false)]
    [InlineData(new[] { "yes" }, ClaimValueTypes.String, false)]
    public void Only_a_single_json_boolean_true_email_verified_claim_counts_as_verified(
        string[] values, string valueType, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            values.Select(value => new Claim("email_verified", value, valueType)), "test"));

        Assert.Equal(expected, AuthCenterOpenIdConnectEvents.HasVerifiedEmail(principal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verified_email_evidence_flows_from_the_external_identity_to_the_session(bool verified)
    {
        var resolution = IdentityContextResolution.Resolved(
            new ResolvedIdentityContext(Guid.NewGuid(), IdentityContextStatus.Active, []));
        Assert.True(IdentityClaimsPrincipalFactory.TryCreate(
            new ExternalIdentity("reg001-unit-subject", false, verified), resolution, out var source));

        var principal = await new PaquetenviaClaimsTransformation().TransformAsync(source!);
        var session = AuthenticatedSession.FromPrincipal(principal);

        Assert.True(session.IsAuthenticated);
        Assert.Equal(verified, session.EmailVerified);
        Assert.Empty(session.ActiveMemberships);
    }

    [Fact]
    public async Task A_forged_email_verified_claim_from_another_issuer_is_ignored()
    {
        var resolution = IdentityContextResolution.Resolved(
            new ResolvedIdentityContext(Guid.NewGuid(), IdentityContextStatus.Active, []));
        Assert.True(IdentityClaimsPrincipalFactory.TryCreate(
            new ExternalIdentity("reg001-unit-subject", false, false), resolution, out var source));
        var forged = new ClaimsPrincipal(source!.Identities.Append(new ClaimsIdentity(
            [new Claim(IdentityClaimTypes.EmailVerified, "true")], "forged")));

        var session = AuthenticatedSession.FromPrincipal(
            await new PaquetenviaClaimsTransformation().TransformAsync(forged));

        Assert.False(session.EmailVerified);
    }
}
