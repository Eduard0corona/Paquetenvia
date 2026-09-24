using Paqueteria.Application.Scaling;

namespace Paqueteria.UnitTests.Scaling;

/// <summary>
/// SCL-001: <c>locked_by</c> is bounded at 100 characters, and the replica identity has to survive
/// that bound. Truncating the qualified id would make every replica of a deployment with a long
/// configured worker id report the same value and lose attribution.
/// </summary>
public sealed class InstanceIdentityTests
{
    private const string InstanceA = "paqueteria-worker-7d9f6c4b58-abcde";
    private const string InstanceB = "paqueteria-worker-7d9f6c4b58-fghij";

    [Fact]
    public void Short_worker_id_keeps_the_readable_lane_and_instance()
    {
        var qualified = InstanceIdentity.QualifyWorkerId("ntf001-local", InstanceA);

        Assert.Equal($"ntf001-local:{InstanceA}", qualified);
        Assert.True(qualified.Length <= InstanceIdentity.MaximumWorkerIdLength);
    }

    [Fact]
    public void Worker_id_at_the_maximum_length_still_carries_the_instance()
    {
        var workerId = new string('w', InstanceIdentity.MaximumWorkerIdLength);

        var qualified = InstanceIdentity.QualifyWorkerId(workerId, InstanceA);

        Assert.Equal(InstanceIdentity.MaximumWorkerIdLength, qualified.Length);
        Assert.StartsWith(new string('w', 10), qualified, StringComparison.Ordinal);
        var separator = qualified.LastIndexOf(':');
        Assert.Equal(
            InstanceIdentity.MaximumWorkerIdLength - 1 - InstanceIdentity.InstanceDigestLength,
            separator);
        Assert.Equal(InstanceIdentity.InstanceDigestLength, qualified.Length - separator - 1);
        Assert.Equal(workerId[..separator], qualified[..separator]);
    }

    [Fact]
    public void Two_replicas_sharing_a_long_worker_id_never_report_the_same_identity()
    {
        var workerId = new string('w', InstanceIdentity.MaximumWorkerIdLength);

        var first = InstanceIdentity.QualifyWorkerId(workerId, InstanceA);
        var second = InstanceIdentity.QualifyWorkerId(workerId, InstanceB);

        Assert.NotEqual(first, second);
        Assert.Equal(first.Length, second.Length);
        Assert.True(first.Length <= InstanceIdentity.MaximumWorkerIdLength);
    }

    [Theory]
    [InlineData("ntf001-local")]
    [InlineData("rtm002-local")]
    public void Qualification_is_deterministic_for_the_same_inputs(string workerId)
    {
        Assert.Equal(
            InstanceIdentity.QualifyWorkerId(workerId, InstanceA),
            InstanceIdentity.QualifyWorkerId(workerId, InstanceA));
        Assert.Equal(
            InstanceIdentity.QualifyWorkerId(new string('w', 250), InstanceA),
            InstanceIdentity.QualifyWorkerId(new string('w', 250), InstanceA));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(83)]
    [InlineData(84)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(250)]
    public void Every_configured_length_stays_inside_the_bounded_contract(int length)
    {
        var workerId = new string('w', length);

        foreach (var instance in new[] { InstanceA, InstanceB, "a", new string('i', 250) })
        {
            var qualified = InstanceIdentity.QualifyWorkerId(workerId, instance);
            Assert.True(
                qualified.Length <= InstanceIdentity.MaximumWorkerIdLength,
                $"'{qualified}' exceeds the bounded worker id contract.");
            Assert.Contains(":", qualified, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unsafe_characters_are_sanitized_on_both_segments()
    {
        var qualified = InstanceIdentity.QualifyWorkerId("ntf001 local", "pod/7");

        Assert.Equal("ntf001-local:pod-7", qualified);
    }

    [Fact]
    public void Missing_worker_id_falls_back_to_the_instance_identity()
    {
        Assert.Equal(InstanceA, InstanceIdentity.QualifyWorkerId("   ", InstanceA));
        Assert.Equal(InstanceIdentity.Current, InstanceIdentity.QualifyWorkerId(string.Empty));
    }
}
