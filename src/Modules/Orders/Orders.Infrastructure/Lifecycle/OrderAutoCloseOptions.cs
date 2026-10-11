using Orders.Application.Lifecycle;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10 execution settings, following the LIF-001 option pattern. The job is off until explicitly
/// enabled on a Worker with <c>ConnectionStrings:PaqueteriaWorker</c>; whether an order may close is decided only by
/// the ORD-002 CLOSED guards, never by these settings.
/// </summary>
public sealed class OrderAutoCloseOptions
{
    public const string SectionName = "Orders:AutoClose";
    public const string WorkerConnectionStringName = "PaqueteriaWorker";

    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public int MaxBatchesPerCycle { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public OrderAutoClosePolicy ToPolicy() => new(BatchSize, MaxBatchesPerCycle);

    internal static bool IsValid(OrderAutoCloseOptions value) =>
        value.PollIntervalSeconds is >= 1 and <= 3_600 &&
        value.BatchSize is >= 1 and <= OrderAutoClosePolicy.MaximumBatchSize &&
        value.MaxBatchesPerCycle is >= 1 and <= OrderAutoClosePolicy.MaximumBatchesPerCycle;
}
