using Identity.Application.Bootstrap;
using Identity.Domain;

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
    public void Organization_roles_have_one_authoritative_contract_vocabulary()
    {
        Assert.Equal(
            ["PLATFORM_ADMIN", "DISPATCHER", "FINANCE", "ALLY_ADMIN", "ALLY_OPERATOR", "BUSINESS_ADMIN", "BUSINESS_OPERATOR", "DRIVER", "VIEWER"],
            Enum.GetValues<OrganizationRole>().Select(value => value.ToContractValue()));
    }

    [Fact]
    public void Bootstrap_context_exposes_only_active_status()
    {
        Assert.Equal(["ACTIVE"],
            Enum.GetValues<IdentityContextStatus>().Select(value => value.ToContractValue()));
    }
}
