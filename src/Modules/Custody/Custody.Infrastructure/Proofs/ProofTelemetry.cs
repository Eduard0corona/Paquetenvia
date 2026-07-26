using System.Diagnostics;
using System.Diagnostics.Metrics;
using Custody.Application.ProofUploads;
using Microsoft.Extensions.Options;

namespace Custody.Infrastructure.Proofs;

public sealed class ProofTelemetry : IProofTelemetry, IDisposable
{
    private readonly string provider;
    private readonly Meter meter = new("Paquetenvia.Custody");
    private readonly Counter<long> sessions;
    private readonly Counter<long> objectsDiscovered;
    private readonly Histogram<double> processingDuration;
    private readonly Counter<long> validationOutcomes;
    private readonly Counter<long> staleRecoveries;
    private readonly Counter<long> finalizations;
    private readonly Counter<long> storageFailures;

    public ProofTelemetry(IOptions<ProofStorage.ProofStorageOptions> options)
    {
        provider = options.Value.Provider.ToString();
        sessions = meter.CreateCounter<long>("custody.proof.sessions");
        objectsDiscovered = meter.CreateCounter<long>("custody.proof.objects_discovered");
        processingDuration = meter.CreateHistogram<double>(
            "custody.proof.processing_duration",
            "ms");
        validationOutcomes = meter.CreateCounter<long>("custody.proof.validation_outcomes");
        staleRecoveries = meter.CreateCounter<long>("custody.proof.stale_recoveries");
        finalizations = meter.CreateCounter<long>("custody.proof.finalizations");
        storageFailures = meter.CreateCounter<long>("custody.proof.storage_failures");
    }

    public void SessionCompleted(string proofType) =>
        sessions.Add(1, Tags(("provider", provider), ("proof_type", proofType)));

    public void ObjectDiscovered() =>
        objectsDiscovered.Add(1, Tags(("provider", provider)));

    public void ProcessingCompleted(
        string proofType,
        string outcome,
        string reasonCode,
        double milliseconds)
    {
        var tags = Tags(
            ("provider", provider),
            ("proof_type", proofType),
            ("outcome", outcome),
            ("reason_code", reasonCode));
        processingDuration.Record(milliseconds, tags);
        validationOutcomes.Add(1, tags);
    }

    public void StaleValidationRecovered(string proofType) =>
        staleRecoveries.Add(1, Tags(("provider", provider), ("proof_type", proofType)));

    public void FinalizationCompleted(string proofType) =>
        finalizations.Add(1, Tags(("provider", provider), ("proof_type", proofType)));

    public void StorageFailure(string reasonCode) =>
        storageFailures.Add(
            1,
            Tags(("provider", provider), ("reason_code", reasonCode)));

    public void Dispose() => meter.Dispose();

    private static TagList Tags(params (string Name, object? Value)[] values)
    {
        var tags = new TagList();
        foreach (var (name, value) in values)
        {
            tags.Add(name, value);
        }

        return tags;
    }
}
