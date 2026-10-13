using Custody.Application.ProofUploads;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Observability;

namespace Custody.Infrastructure.Proofs;

public sealed class ProofValidationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ProofStorageOptions> options,
    ILogger<ProofValidationWorker> logger) : BackgroundService
{
    /// <summary>Fixed name of the proof validation cycle (OBS-003 activity display name).</summary>
    public const string JobName = "custody.proof-validation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider == ProofStorageProvider.Disabled ||
            options.Value.ThreatScanner == ProofThreatScannerProvider.Disabled)
        {
            logger.LogWarning("Secure proof validation is disabled and fails closed.");
            return;
        }

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var failed = false;
            // OBS-003: each validation cycle is a new root trace whose id is in the cycle's log lines.
            using (TraceCorrelation.StartJobCycle(JobName))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IProofValidationProcessor>();
                    await processor.ProcessAvailableAsync(stoppingToken);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    failed = true;
                    consecutiveFailures = Math.Min(consecutiveFailures + 1, 6);
                    logger.LogError("Secure proof validation iteration failed.");
                }
            }

            var baseDelaySeconds = failed
                ? Math.Min(
                    options.Value.ProcessingIntervalSeconds * (1 << consecutiveFailures),
                    300)
                : options.Value.ProcessingIntervalSeconds;
            var jitterMilliseconds = Random.Shared.Next(0, 501);
            await Task.Delay(
                TimeSpan.FromSeconds(baseDelaySeconds) +
                TimeSpan.FromMilliseconds(jitterMilliseconds),
                stoppingToken);
        }
    }
}
