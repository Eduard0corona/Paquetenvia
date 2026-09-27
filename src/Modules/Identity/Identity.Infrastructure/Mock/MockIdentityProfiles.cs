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
    public const string ActiveDispatcher = "active-dispatcher";
    public const string LocalDispatcherMfa = "local-dispatcher-mfa";
    public const string ActiveDriver = "active-driver";
    public const string ExternalDriver = "external-driver";
    public const string SecondaryDriver = "secondary-driver";
    public const string SuspendedUser = "suspended-user";
    public const string DisabledUser = "disabled-user";
    public const string SuspendedMembership = "suspended-membership";
    public const string RevokedMembership = "revoked-membership";
    public const string ActiveWithoutMemberships = "active-without-memberships";
    public const string UnknownSubject = "unknown-subject";
    public const string ActiveFinance = "active-finance";
    public const string ActiveFinanceMfa = "active-finance-mfa";
    public const string ActiveDispatcherPlatformAdminNoMfa = "active-dispatcher-platform-admin-no-mfa";
    public const string ActiveAllyAdmin = "active-ally-admin";
    public const string ActiveAllyOperator = "active-ally-operator";
    public const string ActiveBusinessAdmin = "active-business-admin";
    public const string ActiveBusinessOperator = "active-business-operator";

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
            [ActiveDispatcher] = External("mock-subject-active-dispatcher", false),
            [LocalDispatcherMfa] = External("local-subject-dispatcher-mfa", true),
            [ActiveDriver] = External("mock-subject-active-driver", false),
            [ExternalDriver] = External("mock-subject-external-driver", false),
            [SecondaryDriver] = External("mock-subject-secondary-driver", false),
            [SuspendedUser] = External("mock-subject-suspended", true),
            [DisabledUser] = External("mock-subject-disabled", true),
            [SuspendedMembership] = External("mock-subject-suspended-membership", true),
            [RevokedMembership] = External("mock-subject-revoked-membership", true),
            [ActiveWithoutMemberships] = External("mock-subject-no-memberships", false),
            [UnknownSubject] = External("mock-subject-not-provisioned", false),
            [ActiveFinance] = External("mock-subject-active-finance", false),
            [ActiveFinanceMfa] = External("mock-subject-active-finance-mfa", true),
            [ActiveDispatcherPlatformAdminNoMfa] = External("mock-subject-dispatcher-platform-admin-no-mfa", false),
            [ActiveAllyAdmin] = External("mock-subject-active-ally-admin", false),
            [ActiveAllyOperator] = External("mock-subject-active-ally-operator", false),
            [ActiveBusinessAdmin] = External("mock-subject-active-business-admin", false),
            [ActiveBusinessOperator] = External("mock-subject-active-business-operator", false),
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
            ["mock-subject-active-dispatcher"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10",
                Membership(ViewerOrganizationId, OrganizationRole.Dispatcher, true)),
            ["local-subject-dispatcher-mfa"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa20",
                Membership(ViewerOrganizationId, OrganizationRole.Dispatcher, true)),
            ["mock-subject-active-driver"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11",
                Membership(ViewerOrganizationId, OrganizationRole.Driver, true)),
            ["mock-subject-external-driver"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa13",
                Membership(ViewerOrganizationId, OrganizationRole.Driver, true)),
            ["mock-subject-secondary-driver"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa12",
                Membership(ViewerOrganizationId, OrganizationRole.Driver, true)),
            ["mock-subject-suspended-membership"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7"),
            ["mock-subject-revoked-membership"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8"),
            ["mock-subject-no-memberships"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa9"),
            ["mock-subject-active-finance"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa30",
                Membership(ViewerOrganizationId, OrganizationRole.Finance, true)),
            ["mock-subject-active-finance-mfa"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa31",
                Membership(ViewerOrganizationId, OrganizationRole.Finance, true)),
            ["mock-subject-dispatcher-platform-admin-no-mfa"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa32",
                Membership(ViewerOrganizationId, OrganizationRole.PlatformAdmin, true),
                Membership(ViewerOrganizationId, OrganizationRole.Dispatcher, false)),
            ["mock-subject-active-ally-admin"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa33",
                Membership(ViewerOrganizationId, OrganizationRole.AllyAdmin, true)),
            ["mock-subject-active-ally-operator"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa34",
                Membership(ViewerOrganizationId, OrganizationRole.AllyOperator, true)),
            ["mock-subject-active-business-admin"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa35",
                Membership(ViewerOrganizationId, OrganizationRole.BusinessAdmin, true)),
            ["mock-subject-active-business-operator"] = Context(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa36",
                Membership(ViewerOrganizationId, OrganizationRole.BusinessOperator, true)),
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
