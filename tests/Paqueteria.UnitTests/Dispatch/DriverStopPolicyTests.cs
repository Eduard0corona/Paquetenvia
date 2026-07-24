using Dispatch.Domain;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class DriverStopPolicyTests
{
    [Theory]
    [InlineData("ASSIGNED", false, "PICKUP", true)]
    [InlineData("AT_PICKUP", false, "PICKUP", true)]
    [InlineData("PICKED_UP", false, "DELIVERY", false)]
    [InlineData("IN_TRANSIT", false, "DELIVERY", false)]
    [InlineData("DELIVERING", false, "DELIVERY", false)]
    [InlineData("RETURNING", false, "RETURN", true)]
    [InlineData("FAILED_ATTEMPT", false, "PICKUP", true)]
    [InlineData("FAILED_ATTEMPT", true, "DELIVERY", false)]
    [InlineData("RESCHEDULED", false, "PICKUP", true)]
    [InlineData("RESCHEDULED", true, "DELIVERY", false)]
    public void Operational_states_project_the_direct_stop(
        string status,
        bool custody,
        string type,
        bool origin)
    {
        var projection = DriverStopPolicy.Project(status, custody);

        Assert.True(projection.Included);
        Assert.Equal(type, projection.StopType.ToContractValue());
        Assert.Equal(origin, projection.UseOriginAddress);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CONFIRMED")]
    [InlineData("READY_FOR_PICKUP")]
    [InlineData("RETURNED")]
    [InlineData("DELIVERED")]
    [InlineData("CLOSED")]
    [InlineData("CLAIM_OPEN")]
    [InlineData("CLAIM_RESOLVED")]
    [InlineData("CANCELLED")]
    [InlineData("UNKNOWN")]
    public void Non_operational_states_are_excluded(string status)
    {
        Assert.False(DriverStopPolicy.Project(status, true).Included);
    }
}
