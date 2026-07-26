using System.Text;
using Custody.Application.ProofUploads;

namespace Custody.Infrastructure.ProofStorage;

public sealed class StrictSyntheticThreatScanner : IProofThreatScanner
{
    private static readonly byte[] EicarMarker = Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

    public bool IsEnabled => true;

    public async ValueTask<ProofThreatScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)).IndexOf(EicarMarker) >= 0
            ? new ProofThreatScanResult(false, "THREAT_DETECTED")
            : ProofThreatScanResult.Safe;
    }
}
