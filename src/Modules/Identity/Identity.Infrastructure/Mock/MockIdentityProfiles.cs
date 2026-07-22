using System.Collections.Frozen;
using Identity.Application.Authentication;

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

    public static readonly Guid ViewerOrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OperationsOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ForeignOrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal static FrozenDictionary<string, NormalizedIdentity> All { get; } =
        new Dictionary<string, NormalizedIdentity>(StringComparer.Ordinal)
        {
            [ActiveViewer] = Identity(
                "mock-subject-active-viewer",
                NormalizedIdentityStatus.Active,
                false,
                Membership(ViewerOrganizationId, NormalizedOrganizationRole.Viewer)),
            [ActivePlatformAdminMfa] = Identity(
                "mock-subject-platform-admin-mfa",
                NormalizedIdentityStatus.Active,
                true,
                Membership(ViewerOrganizationId, NormalizedOrganizationRole.PlatformAdmin)),
            [ActivePlatformAdminNoMfa] = Identity(
                "mock-subject-platform-admin-no-mfa",
                NormalizedIdentityStatus.Active,
                false,
                Membership(ViewerOrganizationId, NormalizedOrganizationRole.PlatformAdmin)),
            [ActiveMultiOrganization] = Identity(
                "mock-subject-multi-org",
                NormalizedIdentityStatus.Active,
                true,
                Membership(ViewerOrganizationId, NormalizedOrganizationRole.Viewer),
                Membership(OperationsOrganizationId, NormalizedOrganizationRole.Dispatcher)),
            [SuspendedUser] = Identity(
                "mock-subject-suspended",
                NormalizedIdentityStatus.Suspended,
                true,
                Membership(ViewerOrganizationId, NormalizedOrganizationRole.Viewer)),
            [DisabledUser] = Identity(
                "mock-subject-disabled",
                NormalizedIdentityStatus.Disabled,
                true,
                Membership(ViewerOrganizationId, NormalizedOrganizationRole.Viewer)),
            [SuspendedMembership] = Identity(
                "mock-subject-suspended-membership",
                NormalizedIdentityStatus.Active,
                true,
                new NormalizedOrganizationMembership(
                    ViewerOrganizationId,
                    NormalizedOrganizationRole.Viewer,
                    NormalizedMembershipStatus.Suspended)),
            [RevokedMembership] = Identity(
                "mock-subject-revoked-membership",
                NormalizedIdentityStatus.Active,
                true,
                new NormalizedOrganizationMembership(
                    ViewerOrganizationId,
                    NormalizedOrganizationRole.Viewer,
                    NormalizedMembershipStatus.Revoked)),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static NormalizedIdentity Identity(
        string subject,
        NormalizedIdentityStatus status,
        bool mfaSatisfied,
        params NormalizedOrganizationMembership[] memberships) =>
        new(subject, status, mfaSatisfied, memberships);

    private static NormalizedOrganizationMembership Membership(
        Guid organizationId,
        NormalizedOrganizationRole role) =>
        new(organizationId, role, NormalizedMembershipStatus.Active);
}
