using Identity.Domain;
using Identity.Application.Authentication;

namespace Paqueteria.UnitTests.Identity;

public sealed class DomainContractTests
{
    [Fact]
    public void Identity_statuses_match_the_normative_contract()
    {
        var values = Enum.GetValues<IdentityStatus>().Select(value => value.ToContractValue());

        Assert.Equal(["ACTIVE", "SUSPENDED", "DISABLED"], values);
    }

    [Fact]
    public void Membership_statuses_match_the_normative_contract()
    {
        var values = Enum.GetValues<MembershipStatus>().Select(value => value.ToContractValue());

        Assert.Equal(["ACTIVE", "SUSPENDED", "REVOKED"], values);
    }

    [Fact]
    public void Organization_roles_match_the_normative_contract()
    {
        var values = Enum.GetValues<OrganizationRole>().Select(value => value.ToContractValue());

        Assert.Equal(
            [
                "PLATFORM_ADMIN",
                "DISPATCHER",
                "FINANCE",
                "ALLY_ADMIN",
                "ALLY_OPERATOR",
                "BUSINESS_ADMIN",
                "BUSINESS_OPERATOR",
                "DRIVER",
                "VIEWER",
            ],
            values);
    }

    [Fact]
    public void Normalized_transport_values_cannot_drift_from_domain_values()
    {
        Assert.Equal(
            Enum.GetValues<IdentityStatus>().Select(value => value.ToContractValue()),
            Enum.GetValues<NormalizedIdentityStatus>().Select(value => value.ToContractValue()));
        Assert.Equal(
            Enum.GetValues<MembershipStatus>().Select(value => value.ToContractValue()),
            Enum.GetValues<NormalizedMembershipStatus>().Select(value => value.ToContractValue()));
        Assert.Equal(
            Enum.GetValues<OrganizationRole>().Select(value => value.ToContractValue()),
            Enum.GetValues<NormalizedOrganizationRole>().Select(value => value.ToContractValue()));
    }
}
