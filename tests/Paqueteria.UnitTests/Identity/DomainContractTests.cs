using Identity.Application.Bootstrap;
using Identity.Domain;
using DomainOrganizationRole = Identity.Domain.OrganizationRole;
using BootstrapOrganizationRole = Identity.Application.Bootstrap.OrganizationRole;

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
    public void Bootstrap_roles_match_domain_roles()
    {
        Assert.Equal(
            Enum.GetValues<DomainOrganizationRole>().Select(value => value.ToContractValue()),
            Enum.GetValues<BootstrapOrganizationRole>().Select(value => value.ToContractValue()));
    }

    [Fact]
    public void Bootstrap_context_exposes_only_active_status()
    {
        Assert.Equal(["ACTIVE"],
            Enum.GetValues<IdentityContextStatus>().Select(value => value.ToContractValue()));
    }
}
