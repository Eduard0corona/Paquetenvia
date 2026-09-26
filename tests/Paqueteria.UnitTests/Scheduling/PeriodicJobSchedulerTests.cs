using Microsoft.Extensions.Logging.Abstractions;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.UnitTests.Scheduling;

public sealed class PeriodicJobSchedulerTests
{
    private static readonly PeriodicJobScheduler Scheduler =
        new(TimeProvider.System, NullLogger<PeriodicJobScheduler>.Instance);

    [Fact]
    public async Task Runs_each_cycle_after_the_interval_until_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var job = new CountingJob(TimeSpan.FromMilliseconds(1), (run, _) =>
        {
            if (run == 3)
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        });

        await Scheduler.RunAsync(job, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, job.Runs);
    }

    [Fact]
    public async Task A_failed_cycle_does_not_end_the_schedule()
    {
        using var cancellation = new CancellationTokenSource();
        var job = new CountingJob(TimeSpan.FromMilliseconds(1), (run, _) =>
        {
            if (run == 1)
            {
                throw new InvalidOperationException("synthetic cycle failure");
            }

            cancellation.Cancel();
            return Task.CompletedTask;
        });

        await Scheduler.RunAsync(job, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, job.Runs);
    }

    [Fact]
    public async Task Cancellation_during_the_wait_returns_without_another_cycle()
    {
        using var cancellation = new CancellationTokenSource();
        var job = new CountingJob(TimeSpan.FromHours(1), (_, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        await Scheduler.RunAsync(job, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task Cancellation_inside_a_cycle_ends_cleanly()
    {
        using var cancellation = new CancellationTokenSource();
        var job = new CountingJob(TimeSpan.FromMilliseconds(1), (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Scheduler.RunAsync(job, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task An_already_cancelled_token_never_runs_the_job()
    {
        var job = new CountingJob(TimeSpan.FromSeconds(1), (_, _) => Task.CompletedTask);

        await Scheduler.RunAsync(job, new CancellationToken(canceled: true));

        Assert.Equal(0, job.Runs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_601)]
    public async Task Unbounded_intervals_are_rejected(int seconds)
    {
        var job = new CountingJob(TimeSpan.FromSeconds(seconds), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Scheduler.RunAsync(job, CancellationToken.None));
        Assert.Equal(0, job.Runs);
    }

    private sealed class CountingJob(TimeSpan interval, Func<int, CancellationToken, Task> cycle) : IScheduledJob
    {
        public int Runs { get; private set; }
        public string Name => "synthetic.scheduler-test";
        public TimeSpan Interval => interval;

        public Task RunOnceAsync(CancellationToken cancellationToken) => cycle(++Runs, cancellationToken);
    }
}
