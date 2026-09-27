using Pricing.Application.Quotes;
using Pricing.Domain;

namespace Paqueteria.UnitTests.Pricing;

/// <summary>AI05-INPUT-LIMITS: a quote carries 1 to 20 packages, enforced by the domain policy.</summary>
public sealed class PackageCountLimitTests
{
    [Fact]
    public void The_endpoint_limit_is_the_domain_limit()
    {
        Assert.Equal(20, PricingPackagePolicy.MaximumPackages);
        Assert.Equal(PricingPackagePolicy.MaximumPackages, QuoteInputLimits.MaximumPackages);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(19, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    [InlineData(500, false)]
    public void Package_count_is_bounded_on_both_sides(int count, bool expected)
    {
        var packages = Enumerable.Range(0, count)
            .Select(index => new PricingPackage($"Caja {index}", 500, 10_000, null, null, null))
            .ToArray();

        Assert.Equal(expected, PricingPackagePolicy.IsValid(packages));
    }

    [Fact]
    public void A_missing_package_list_is_rejected()
    {
        Assert.False(PricingPackagePolicy.IsValid(null));
    }
}
