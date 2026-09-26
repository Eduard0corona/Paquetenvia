using Orders.Application.Lifecycle;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>
/// LIF-001 execution settings only. The claim-window duration stays <c>Orders:ClaimWindowHours</c>;
/// the job is off until explicitly enabled on a Worker with <c>ConnectionStrings:PaqueteriaWorker</c>.
/// </summary>
public sealed class ClaimWindowFinalizationOptions
{
    public const string SectionName = "Orders:ClaimWindowFinalization";
    public const string WorkerConnectionStringName = "PaqueteriaWorker";

    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public int MaxBatchesPerCycle { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public ClaimWindowFinalizationPolicy ToPolicy() => new(BatchSize, MaxBatchesPerCycle);

    internal static bool IsValid(ClaimWindowFinalizationOptions value) =>
        value.PollIntervalSeconds is >= 1 and <= 3_600 &&
        value.BatchSize is >= 1 and <= ClaimWindowFinalizationPolicy.MaximumBatchSize &&
        value.MaxBatchesPerCycle is >= 1 and <= ClaimWindowFinalizationPolicy.MaximumBatchesPerCycle;
}
