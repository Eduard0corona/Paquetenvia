using Custody.Application.ProofUploads;

namespace Custody.Infrastructure.ProofStorage;

public sealed class DisabledProofThreatScanner : IProofThreatScanner
{
    public bool IsEnabled => false;

    public ValueTask<ProofThreatScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProofThreatScanResult(false, "SCANNER_UNAVAILABLE"));
}
