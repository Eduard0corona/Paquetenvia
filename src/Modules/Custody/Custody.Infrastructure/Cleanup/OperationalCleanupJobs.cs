using System.Diagnostics.Metrics;
using Custody.Application.Cleanup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application.Scheduling;

namespace Custody.Infrastructure.Cleanup;

/// <summary>
/// OPS-003 metrics. Dimensions are limited to <c>job</c> (idempotency_keys|proof_upload_sessions|bff_sessions),
/// <c>mode</c> (dry_run|apply), <c>outcome</c> (drained|capped|failed) and a bounded
/// <c>error_class</c>; no tenant, key, session or user identifier is ever recorded.
/// </summary>
public sealed class OperationalCleanupTelemetry : IDisposable
{
    public const string MeterName = "Paquetenvia.Operations.Cleanup";
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _rows;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _duration;

    public OperationalCleanupTelemetry()
    {
        _cycles = Meter.CreateCounter<long>("operations.cleanup.cycles");
        _rows = Meter.CreateCounter<long>("operations.cleanup.rows");
        _failures = Meter.CreateCounter<long>("operations.cleanup.failures");
        _duration = Meter.CreateHistogram<double>("operations.cleanup.cycle_duration", "ms");
    }

    internal Meter Meter { get; } = new(MeterName);

    public void CycleCompleted(string job, bool dryRun, string outcome, long rows, TimeSpan duration)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("mode", dryRun ? "dry_run" : "apply"),
            new KeyValuePair<string, object?>("outcome", outcome),
        };
        _cycles.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
        if (rows > 0)
        {
            _rows.Add(rows, tags[0], tags[1]);
        }
    }

    public void Failed(string job, bool dryRun, string errorClass) =>
        _failures.Add(
            1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("mode", dryRun ? "dry_run" : "apply"),
            new KeyValuePair<string, object?>("error_class", errorClass));

    public void Dispose() => Meter.Dispose();
}

/// <summary>Shared cycle, telemetry and logging for the OPS-003 <see cref="IScheduledJob"/>s.</summary>
public abstract class OperationalCleanupJob(
    OperationalCleanupTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger logger) : IScheduledJob
{
    public abstract string Name { get; }

    public abstract TimeSpan Interval { get; }

    /// <summary>The metric and log dimension; fixed per job and never user data.</summary>
    protected abstract string Dimension { get; }

    protected abstract OperationalCleanupPolicy Policy { get; }

    protected TimeProvider Clock => timeProvider;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var policy = Policy;
        var started = timeProvider.GetTimestamp();
        OperationalCleanupCycleResult result;
        try
        {
            result = await OperationalCleanupCycle.RunAsync(policy, RunBatchAsync, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorClass = ErrorClass(exception);
            telemetry.Failed(Dimension, policy.DryRun, errorClass);
            telemetry.CycleCompleted(Dimension, policy.DryRun, "failed", 0, timeProvider.GetElapsedTime(started));
            logger.LogError(
                "Operational cleanup {Job} cycle failed. dry_run={DryRun} error_class={ErrorClass}",
                Dimension,
                policy.DryRun,
                errorClass);
            throw;
        }

        var outcome = result.Drained ? "drained" : "capped";
        telemetry.CycleCompleted(Dimension, policy.DryRun, outcome, result.Affected, timeProvider.GetElapsedTime(started));
        if (result.Affected > 0 || !result.Drained)
        {
            logger.LogInformation(
                "Operational cleanup {Job} cycle {Outcome}. dry_run={DryRun} batches={Batches} rows={Rows}",
                Dimension,
                outcome,
                policy.DryRun,
                result.Batches,
                result.Affected);
        }
    }

    protected abstract Task<int> RunBatchAsync(int batchSize, CancellationToken cancellationToken);

    // Bounded error classes: an SQLSTATE or a coarse transport class, never an exception message.
    private static string ErrorClass(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState,
        NpgsqlException { IsTransient: true } => "transient",
        NpgsqlException => "npgsql",
        TimeoutException => "timeout",
        InvalidOperationException => "invalid_operation",
        _ => "unexpected",
    };
}

/// <summary>OPS-003 idempotency-key purge. Dry-run by default; the 72-hour floor is enforced in SQL.</summary>
public sealed class IdempotencyKeyPurgeJob(
    IOperationalCleanupGateway gateway,
    IOptions<OperationalCleanupOptions> options,
    OperationalCleanupTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<IdempotencyKeyPurgeJob> logger) : OperationalCleanupJob(telemetry, timeProvider, logger)
{
    public const string JobName = "operations.cleanup.idempotency-keys";

    public override string Name => JobName;

    public override TimeSpan Interval => options.Value.IdempotencyKeys.PollInterval;

    protected override string Dimension => "idempotency_keys";

    protected override OperationalCleanupPolicy Policy => options.Value.IdempotencyKeys.ToPolicy();

    // "Expired before now": the function also clamps the cutoff to its own clock and applies the floor.
    protected override Task<int> RunBatchAsync(int batchSize, CancellationToken cancellationToken) =>
        gateway.PurgeExpiredIdempotencyKeysAsync(
            Clock.GetUtcNow(),
            batchSize,
            options.Value.IdempotencyKeys.DryRun,
            cancellationToken);
}

/// <summary>OPS-003 proof upload-session expiry. Quarantined objects follow the bucket retention policy.</summary>
public sealed class ProofUploadSessionExpiryJob(
    IOperationalCleanupGateway gateway,
    IOptions<OperationalCleanupOptions> options,
    OperationalCleanupTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<ProofUploadSessionExpiryJob> logger) : OperationalCleanupJob(telemetry, timeProvider, logger)
{
    public const string JobName = "operations.cleanup.proof-upload-sessions";

    public override string Name => JobName;

    public override TimeSpan Interval => options.Value.ProofUploadSessions.PollInterval;

    protected override string Dimension => "proof_upload_sessions";

    protected override OperationalCleanupPolicy Policy => options.Value.ProofUploadSessions.ToPolicy();

    protected override Task<int> RunBatchAsync(int batchSize, CancellationToken cancellationToken) =>
        gateway.ExpireProofUploadSessionsAsync(batchSize, cancellationToken);
}

/// <summary>
/// BFF-SESSION-TABLE-SHAPE purge through the cleanup role: revoked and expired BFF sessions leave
/// <c>identity.bff_sessions</c>. The database mutation is the idempotency mechanism.
/// </summary>
public sealed class BffSessionPurgeJob(
    IOperationalCleanupGateway gateway,
    IOptions<OperationalCleanupOptions> options,
    OperationalCleanupTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<BffSessionPurgeJob> logger) : OperationalCleanupJob(telemetry, timeProvider, logger)
{
    public const string JobName = "operations.cleanup.bff-sessions";

    public override string Name => JobName;

    public override TimeSpan Interval => options.Value.BffSessions.PollInterval;

    protected override string Dimension => "bff_sessions";

    protected override OperationalCleanupPolicy Policy => options.Value.BffSessions.ToPolicy();

    protected override Task<int> RunBatchAsync(int batchSize, CancellationToken cancellationToken) =>
        gateway.PurgeBffSessionsAsync(batchSize, cancellationToken);
}

/// <summary>
/// Worker host for OPS-003 (ADR-034 scheduling precedent): each enabled job runs on the shared
/// <see cref="IJobScheduler"/>; nothing is scheduled unless enabled, and a failed cycle is retried
/// on the next interval without stopping the Worker.
/// </summary>
public sealed class OperationalCleanupHostedService(
    IJobScheduler scheduler,
    IdempotencyKeyPurgeJob idempotencyKeys,
    ProofUploadSessionExpiryJob proofUploadSessions,
    BffSessionPurgeJob bffSessions,
    IOptions<OperationalCleanupOptions> options,
    ILogger<OperationalCleanupHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var runs = new List<Task>(3);
        if (settings.IdempotencyKeys.Enabled)
        {
            logger.LogInformation(
                "Idempotency-key cleanup scheduled with dry_run={DryRun} poll_interval={PollInterval}.",
                settings.IdempotencyKeys.DryRun,
                settings.IdempotencyKeys.PollInterval);
            runs.Add(scheduler.RunAsync(idempotencyKeys, stoppingToken));
        }

        if (settings.ProofUploadSessions.Enabled)
        {
            logger.LogInformation(
                "Proof upload-session expiry scheduled with poll_interval={PollInterval}.",
                settings.ProofUploadSessions.PollInterval);
            runs.Add(scheduler.RunAsync(proofUploadSessions, stoppingToken));
        }

        if (settings.BffSessions.Enabled)
        {
            logger.LogInformation(
                "BFF session purge scheduled with poll_interval={PollInterval}.",
                settings.BffSessions.PollInterval);
            runs.Add(scheduler.RunAsync(bffSessions, stoppingToken));
        }

        if (runs.Count == 0)
        {
            logger.LogInformation("Operational cleanup is disabled; no cleanup function will be called.");
            return Task.CompletedTask;
        }

        return Task.WhenAll(runs);
    }
}
