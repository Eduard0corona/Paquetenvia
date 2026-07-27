using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Reporting.Application.Operations;

namespace Reporting.Infrastructure.Operations;

internal sealed class OperationsDashboardTelemetry(
    ILogger<OperationsDashboardTelemetry> logger) : IOperationsDashboardTelemetry
{
    private static readonly Meter Meter = new("Paqueteria.OperationsDashboard");
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("dashboard_lookup_completed");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("dashboard_lookup_failed");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("dashboard_authorization_rejected");
    private static readonly Counter<long> Invalid = Meter.CreateCounter<long>("dashboard_contract_invalid");
    private static readonly Counter<long> AssignmentInconsistent =
        Meter.CreateCounter<long>("dashboard_active_assignment_inconsistent");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("dashboard_lookup_duration_ms");

    public IDisposable MeasureLookup() => new Measurement(Duration);

    public void LookupCompleted() => Completed.Add(1);

    public void LookupFailed(string category)
    {
        Failed.Add(1, new KeyValuePair<string, object?>("category", SafeCategory(category)));
        logger.LogError("dashboard_lookup_failed category={Category}", SafeCategory(category));
    }

    public void AuthorizationRejected() => Rejected.Add(1);

    public void ContractInvalid(string category)
    {
        Invalid.Add(1, new KeyValuePair<string, object?>("category", SafeCategory(category)));
        logger.LogError("dashboard_contract_invalid category={Category}", SafeCategory(category));
    }

    public void ActiveAssignmentInconsistent()
    {
        AssignmentInconsistent.Add(1);
        logger.LogError("dashboard_active_assignment_inconsistent");
    }

    private static string SafeCategory(string category) => category switch
    {
        "provider" or "database" or "contract" or "location" or "assignment" => category,
        _ => "unknown",
    };

    private sealed class Measurement(Histogram<double> histogram) : IDisposable
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        public void Dispose() => histogram.Record(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
    }
}
