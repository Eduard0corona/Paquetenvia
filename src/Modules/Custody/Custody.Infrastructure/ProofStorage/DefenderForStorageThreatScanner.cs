using System.Globalization;
using Custody.Application.ProofUploads;
using Microsoft.Extensions.Options;

namespace Custody.Infrastructure.ProofStorage;

/// <summary>
/// ADP-001-POD-BLOB-DEFENDER: reads the Microsoft Defender for Storage malware-scanning verdict that
/// Defender writes as blob index tags on the quarantine blob. Only an explicit "No threats found"
/// recorded after the blob's last modification is safe. A missing or stale verdict is
/// <see cref="ProofThreatScanResult.Pending"/> (the object stays in quarantine and the session is
/// untouched); "Malicious" rejects the session with <c>THREAT_DETECTED</c>; any other result
/// (not scanned, scan error) rejects with <c>THREAT_SCAN_FAILED</c>. Rejected objects are never
/// promoted or deleted: they stay quarantined.
/// </summary>
/// <remarks>
/// Index tags are not tamper-resistant against principals that can write them. The upload SAS never
/// includes the tag permission and neither workload holds <c>blobs/tags/write</c> (see
/// docs/development/adp-001-production-adapters.md).
/// </remarks>
public sealed class DefenderForStorageThreatScanner(
    IProofBlobGateway gateway,
    IOptions<ProofStorageOptions> options) : IProofThreatScanner
{
    public const string ThreatDetectedCode = "THREAT_DETECTED";
    public const string ScanFailedCode = "THREAT_SCAN_FAILED";

    public bool IsEnabled => true;

    /// <summary>The content-only overload cannot see the verdict; it never reports safe.</summary>
    public ValueTask<ProofThreatScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProofThreatScanResult.Pending);

    public async ValueTask<ProofThreatScanResult> ScanAsync(
        ProofObjectDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken) =>
        await ReadVerdictAsync(descriptor, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> IsVerdictPendingAsync(
        ProofObjectDescriptor descriptor,
        CancellationToken cancellationToken) =>
        (await ReadVerdictAsync(descriptor, cancellationToken).ConfigureAwait(false)).IsPending;

    public async ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.CanReadTagsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<ProofThreatScanResult> ReadVerdictAsync(
        ProofObjectDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var settings = options.Value.DefenderForStorage;
        var tags = await gateway.GetTagsAsync(descriptor.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (tags is null ||
            !tags.TryGetValue(settings.ScanResultTagName, out var result) ||
            string.IsNullOrWhiteSpace(result))
        {
            return ProofThreatScanResult.Pending;
        }

        // A verdict older than the content it would vouch for belongs to an earlier upload.
        if (descriptor.LastModified is not { } lastModified ||
            !tags.TryGetValue(settings.ScanTimeTagName, out var scannedAtValue) ||
            !TryParseScanTime(scannedAtValue, out var scannedAt) ||
            scannedAt < lastModified.ToUniversalTime().AddSeconds(-1))
        {
            return ProofThreatScanResult.Pending;
        }

        if (string.Equals(result, settings.NoThreatsFoundValue, StringComparison.Ordinal))
        {
            return ProofThreatScanResult.Safe;
        }

        return string.Equals(result, settings.MaliciousValue, StringComparison.Ordinal)
            ? new ProofThreatScanResult(false, ThreatDetectedCode)
            : new ProofThreatScanResult(false, ScanFailedCode);
    }

    internal static bool TryParseScanTime(string? value, out DateTimeOffset scannedAt) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out scannedAt);
}
