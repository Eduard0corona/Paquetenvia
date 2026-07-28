namespace Paqueteria.IntegrationTests.Operations;

[Collection(Ops001DeliverySimulationCollection.Name)]
public sealed class Ops001DeliverySimulationTests(
    Ops001DeliverySimulationFixture fixture)
{
    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public async Task Two_consecutive_runs_deliver_twenty_orders_with_worker_recovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var runner = new Ops001ScenarioRunner(fixture);

        var first = await runner.RunAsync(
            Ops001ScenarioData.Create(1),
            publishReport: false,
            timeout.Token);
        var second = await runner.RunAsync(
            Ops001ScenarioData.Create(2),
            publishReport: true,
            timeout.Token);

        first.EnsureAccepted();
        second.EnsureAccepted();
        Assert.Equal(first.OrdersDelivered, second.OrdersDelivered);
        Assert.Equal(first.PickupProofsCompleted, second.PickupProofsCompleted);
        Assert.Equal(first.DeliveryProofsCompleted, second.DeliveryProofsCompleted);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public async Task Cancellation_disposes_all_owned_resources()
    {
        var disposed = false;
        await using (var resources = new Ops001AsyncResourceScope())
        {
            resources.Own(new CancellationProbe(() => disposed = true));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Delay(TimeSpan.FromSeconds(1), cancelled.Token));
            Assert.Equal(1, resources.Count);
        }

        Assert.True(disposed);
    }

    private sealed class CancellationProbe(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
