using System.Collections.Frozen;
using Identity.Application.Authentication;
using Identity.Application.Bootstrap;

namespace Identity.Infrastructure.Mock;

public static class MockIdentityProfiles
{
    public const string ActiveViewer = "active-viewer";
    public const string ActivePlatformAdminMfa = "active-platform-admin-mfa";
    public const string ActivePlatformAdminNoMfa = "active-platform-admin-no-mfa";
    public const string ActiveMultiOrganization = "active-multi-org";
    public const string SuspendedUser = "suspended-user";
    public const string DisabledUser = "disabled-user";
    public const string SuspendedMembership = "suspended-membership";
    public const string RevokedMembership = "revoked-membership";
    public const string ActiveWithoutMemberships = "active-without-memberships";
    public const string UnknownSubject = "unknown-subject";

    public static readonly Guid ViewerOrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OperationsOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ForeignOrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal static FrozenDictionary<string, ExternalIdentity> AuthenticationProfiles { get; } =
        new Dictionary<string, ExternalIdentity>(StringComparer.Ordinal)
        {
            [ActiveViewer] = External("mock-subject-active-viewer", false),
            [ActivePlatformAdminMfa] = External("mock-subject-platform-admin-mfa", true),
            [ActivePlatformAdminNoMfa] = External("mock-subject-platform-admin-no-mfa", false),
            [ActiveMultiOrganization] = External("mock-subject-multi-org", true),
            [SuspendedUser] = External("mock-subject-suspended", true),
            [DisabledUser] = External("mock-subject-disabled", true),
            [SuspendedMembership] = External("mock-subject-suspended-membership", true),
            [RevokedMembership] = External("mock-subject-revoked-membership", true),
            [ActiveWithoutMemberships] = External("mock-subject-no-memberships", false),
            [UnknownSubject] = External("mock-subject-not-provisioned", false),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static FrozenDictionary<string, ResolvedIdentityContext> AuthorizationProfiles { get; } =
        new Dictionary<string, ResolvedIdentityContext>(StringComparer.Ordinal)
        {
            ["mock-subject-active-viewer"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1",
                Membership(ViewerOrganizationId, OrganizationRole.Viewer, true)),
            ["mock-subject-platform-admin-mfa"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2",
                Membership(ViewerOrganizationId, OrganizationRole.PlatformAdmin, true)),
            ["mock-subject-platform-admin-no-mfa"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3",
                Membership(ViewerOrganizationId, OrganizationRole.PlatformAdmin, true)),
            ["mock-subject-multi-org"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4",
                Membership(ViewerOrganizationId, OrganizationRole.Viewer, true),
                Membership(OperationsOrganizationId, OrganizationRole.Dispatcher, false)),
            ["mock-subject-suspended-membership"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7"),
            ["mock-subject-revoked-membership"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8"),
            ["mock-subject-no-memberships"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa9"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static ExternalIdentity External(string subject, bool mfaSatisfied) =>
        new(subject, mfaSatisfied);

    private static ResolvedIdentityContext Context(
        string userId,
        params IdentityContextMembership[] memberships) =>
        new(Guid.Parse(userId), IdentityContextStatus.Active, memberships);

    private static IdentityContextMembership Membership(
        Guid organizationId,
        OrganizationRole role,
        bool isDefault) =>
        new(organizationId, role, isDefault);
}
