using Custody.Domain;

namespace Custody.Application.ProofUploads;

public sealed record CreateProofUploadSessionCommand(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string IdempotencyKey,
    Guid OrderId,
    string ProofType,
    string ContentType,
    long SizeBytes,
    byte[]? Sha256,
    string? RequestId);

public sealed record ProofUploadSessionResult(
    Guid Id,
    Guid OrderId,
    string ObjectKey,
    string UploadUrl,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt,
    string Status);

public sealed record FinalizeProofCommand(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string IdempotencyKey,
    Guid OrderId,
    Guid UploadSessionId,
    string ProofType,
    byte[] Sha256,
    DateTimeOffset CapturedAt,
    double? Latitude,
    double? Longitude,
    string? RecipientName,
    string? RequestId);

public sealed record ProofResult(
    Guid Id,
    Guid OrderId,
    Guid UploadSessionId,
    string ProofType,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CapturedAt,
    double? Latitude,
    double? Longitude,
    DateTimeOffset CreatedAt);

public interface IProofUploadSessionService
{
    Task<ProofUploadSessionResult> CreateAsync(
        CreateProofUploadSessionCommand command,
        CancellationToken cancellationToken);
}

public static class ProofRequestPolicy
{
    public static bool IsSupportedProofType(string? value) =>
        value is "PICKUP_PHOTO" or "DELIVERY_PHOTO" or "SIGNATURE" or "DELIVERY_CODE" or "RETURN_PHOTO";

    public static bool TryParseSha256(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public interface IProofFinalizationService
{
    Task<ProofResult> FinalizeAsync(
        FinalizeProofCommand command,
        CancellationToken cancellationToken);
}

public sealed record GetProofDownloadCommand(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    Guid ProofId,
    string? RequestId = null);

public sealed record ProofDownloadResult(
    Guid ProofId,
    string DownloadUrl);

public interface IProofDownloadService
{
    Task<ProofDownloadResult> GetInternalDownloadAsync(
        GetProofDownloadCommand command,
        CancellationToken cancellationToken);
}

public sealed record ProofUploadGrant(
    string ObjectKey,
    string Url,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt);

public sealed record ProofObjectDescriptor(
    string ObjectKey,
    long SizeBytes,
    string ContentType,
    string ETag,
    string? VersionId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ValidatedProofObject(
    Guid SessionId,
    Guid OrderId,
    Guid OwnerOrganizationId,
    string ProofType,
    string QuarantineObjectKey,
    string FinalObjectKey,
    long SizeBytes,
    string ContentType,
    byte[] Sha256,
    string ETag,
    string? VersionId);

public interface IProofObjectStorage
{
    bool IsEnabled { get; }

    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    Task<ProofUploadGrant> CreateUploadGrantAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid sessionId,
        Guid requestedBy,
        ProofType proofType,
        string contentType,
        long sizeBytes,
        byte[]? sha256,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        string objectKey,
        string? versionId,
        CancellationToken cancellationToken);

    Task PromoteAsync(ValidatedProofObject proof, CancellationToken cancellationToken);

    Task<ProofObjectDescriptor?> HeadAsync(string objectKey, CancellationToken cancellationToken);

    Task DeleteQuarantineAsync(string objectKey, string? versionId, CancellationToken cancellationToken);

    Task<string> CreateInternalDownloadUrlAsync(string objectKey, CancellationToken cancellationToken);
}

public interface IProofThreatScanner
{
    bool IsEnabled { get; }

    ValueTask<ProofThreatScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

public sealed record ProofThreatScanResult(bool IsSafe, string Code)
{
    public static ProofThreatScanResult Safe { get; } = new(true, "SAFE");
}

public interface IProofValidationProcessor
{
    Task ProcessAvailableAsync(CancellationToken cancellationToken);
}

public interface IProofTelemetry
{
    void SessionCompleted(string proofType);
    void ObjectDiscovered();
    void ProcessingCompleted(string proofType, string outcome, string reasonCode, double milliseconds);
    void StaleValidationRecovered(string proofType);
    void FinalizationCompleted(string proofType);
    void StorageFailure(string reasonCode);
}

public abstract class ProofUploadException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class ProofForbiddenException() : ProofUploadException("FORBIDDEN");
public sealed class ProofNotFoundException() : ProofUploadException("NOT_FOUND");
public sealed class ProofConflictException(string code) : ProofUploadException(code);
public sealed class ProofStorageUnavailableException() : ProofUploadException("PROOF_STORAGE_UNAVAILABLE");
