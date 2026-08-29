using Drivers.Application.Eligibility;
using Routing.Application.Routes;
using Routing.Domain;

namespace Paqueteria.UnitTests.Routing;

public sealed class ManualRoutePolicyTests
{
    [Fact]
    public void Rte001_links_only_owned_active_assignment_for_the_route_driver()
    {
        var driver = Guid.NewGuid();
        Assert.True(ManualRoutePolicy.CanLinkAssignment("OWN", "ACCEPTED", driver, driver, null));
        Assert.True(ManualRoutePolicy.CanLinkAssignment("OWN", "ACTIVE", driver, driver, null));
        Assert.False(ManualRoutePolicy.CanLinkAssignment("EXTERNAL", "ACCEPTED", driver, driver, null));
        Assert.False(ManualRoutePolicy.CanLinkAssignment("OWN", "COMPLETED", driver, driver, null));
        Assert.False(ManualRoutePolicy.CanLinkAssignment("OWN", "ACTIVE", driver, Guid.NewGuid(), null));
        Assert.False(ManualRoutePolicy.CanLinkAssignment("OWN", "ACTIVE", driver, driver, Guid.NewGuid()));
    }

    [Fact]
    public void Capacity_aggregator_uses_real_packages_and_rejects_incomplete_dimensions()
    {
        Assert.True(RoutingCapacityAggregator.TryAggregate(
            [(1_000, 300, 200, 100), (2_000, 400, 250, 150)], out var capacity));
        Assert.Equal(new DriverCapacityRequirement(2, 3_000, 2_000, 400, 250, 150), capacity);
        Assert.False(RoutingCapacityAggregator.TryAggregate([], out _));
        Assert.True(RoutingCapacityAggregator.TryAggregate([(1_000, 300, null, 100)], out var partial));
        Assert.NotNull(partial);
        Assert.Null(partial.MaximumWidthMillimeters);
    }

    [Fact]
    public void Canonical_reorder_hash_is_order_sensitive_and_stable()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var route = Guid.NewGuid();
        var command = new ReorderRouteStopsCommand(
            actor, organization, "key", route, 3, [first, second], true, null);
        var same = new ReorderRouteStopsCommand(
            actor, organization, "key", route, 3, [first, second], true, null);
        var reversed = new ReorderRouteStopsCommand(
            actor, organization, "key", route, 3, [second, first], true, null);
        Assert.Equal(RoutingCanonicalizer.Reorder(command), RoutingCanonicalizer.Reorder(same));
        Assert.NotEqual(RoutingCanonicalizer.Reorder(command), RoutingCanonicalizer.Reorder(reversed));
    }

    [Theory]
    [InlineData("DISPATCHER", false, true)]
    [InlineData("PLATFORM_ADMIN", true, true)]
    [InlineData("PLATFORM_ADMIN", false, false)]
    [InlineData("DRIVER", true, false)]
    public void Mutation_capability_is_fail_closed(string role, bool mfa, bool expected)
    {
        Assert.Equal(expected, RoutingAuthorizationPolicy.CanMutate(new(role, true, true, mfa)));
    }
}
