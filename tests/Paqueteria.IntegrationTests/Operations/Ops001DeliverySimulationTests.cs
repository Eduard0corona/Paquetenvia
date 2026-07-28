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
    public async Task Cancellation_of_real_runner_releases_owned_resources_and_allows_next_run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token);
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"ops001-cancelled-{Guid.NewGuid():N}.json");
        var checkpoint = new Ops001RunTestCheckpoint(reportPath);
        var runner = new Ops001ScenarioRunner(fixture);
        try
        {
            var cancelledRun = runner.RunAsync(
                Ops001ScenarioData.Create(3),
                publishReport: true,
                cancellation.Token,
                checkpoint);
            await checkpoint.WaitUntilOwnedResourcesStartedAsync(
                TimeSpan.FromMinutes(2),
                timeout.Token);
            Assert.NotEqual(Guid.Empty, checkpoint.TargetOutboxId);
            Assert.Equal(1, checkpoint.ActiveHosts);
            Assert.Equal(1, checkpoint.ActiveObservers);
            Assert.Equal(0, checkpoint.ActiveProofWorkers);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelledRun);

            Assert.True(checkpoint.CleanupWasCompleted);
            Assert.Equal(0, checkpoint.ActiveHosts);
            Assert.Equal(0, checkpoint.ActiveObservers);
            Assert.Equal(0, checkpoint.ActiveProofWorkers);
            Assert.NotEqual("PROCESSING", checkpoint.FinalOutboxStatus);
            Assert.False(File.Exists(reportPath));

            var recovery = await runner.RunAsync(
                Ops001ScenarioData.Create(4),
                publishReport: false,
                timeout.Token);
            recovery.EnsureAccepted();
            Assert.Equal(Ops001ScenarioData.OrderCount, recovery.OrdersDelivered);
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }
}
