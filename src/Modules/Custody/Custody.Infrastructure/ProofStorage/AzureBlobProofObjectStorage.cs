using System.Globalization;
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Microsoft.Extensions.Options;

namespace Custody.Infrastructure.ProofStorage;

/// <summary>
/// The exact <c>required_headers</c> a storage provider hands to the client, recomputed so a stored
/// idempotent replay can be verified against the provider that issued it.
/// </summary>
internal interface IProofUploadHeaderShape
{
    IReadOnlyDictionary<string, string> RequiredUploadHeaders(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid sessionId,
        Guid requestedBy,
        string proofType,
        string contentType,
        long sizeBytes,
        byte[]? sha256);
}

/// <summary>
/// ADP-001-POD-BLOB-DEFENDER: proof objects in Azure Blob Storage. The API still never receives the
/// bytes: it issues a user-delegation SAS scoped to the single quarantine blob, with create and
/// write permissions only, HTTPS only and the POD-001 upload lifetime. The Worker keeps the POD-001
/// pipeline (validation, conditional promotion by ETag, final metadata rebuilt by the server).
/// </summary>
/// <remarks>
/// Azure metadata names must be C# identifiers, so the POD-001 logical names (<c>session-id</c>, …)
/// travel without hyphens (<c>x-ms-meta-sessionid</c>) and are mapped back on read. A SAS does not
/// sign request headers: the Worker's database-backed checks (key, requester, size, content type,
/// signed metadata consistency) remain the barrier, exactly as for S3.
/// </remarks>
public sealed class AzureBlobProofObjectStorage : IProofObjectStorage, IProofUploadHeaderShape
{
    private static readonly string[] LogicalMetadataNames =
    [
        S3CompatibleProofObjectStorage.SessionIdMetadata,
        S3CompatibleProofObjectStorage.OrderIdMetadata,
        S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata,
        S3CompatibleProofObjectStorage.RequestedByMetadata,
        S3CompatibleProofObjectStorage.ProofTypeMetadata,
        S3CompatibleProofObjectStorage.Sha256Metadata,
        S3CompatibleProofObjectStorage.SizeBytesMetadata,
    ];

    private readonly IProofBlobGateway _gateway;
    private readonly ProofStorageOptions _options;
    private readonly TimeProvider _time;
    private readonly bool _signsUrls;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private UserDelegationKey? _delegationKey;

    public AzureBlobProofObjectStorage(
        IProofBlobGateway gateway,
        IOptions<ProofStorageOptions> options,
        TimeProvider timeProvider,
        bool signsUrls = true)
    {
        _gateway = gateway;
        _options = options.Value;
        _time = timeProvider;
        _signsUrls = signsUrls;
    }

    public bool IsEnabled => true;

    public static string BlobMetadataName(string logicalName) => logicalName.Replace("-", string.Empty, StringComparison.Ordinal);

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var container = await _gateway.GetContainerStateAsync(cancellationToken).ConfigureAwait(false);
            if (!container.Exists || !container.IsPrivate)
            {
                return false;
            }

            await foreach (var _ in _gateway.ListAsync(_options.QuarantinePrefix, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            if (_signsUrls)
            {
                _ = await DelegationKeyAsync(TimeSpan.FromMinutes(_options.UploadUrlLifetimeMinutes), cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
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

    public IReadOnlyDictionary<string, string> RequiredUploadHeaders(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid sessionId,
        Guid requestedBy,
        string proofType,
        string contentType,
        long sizeBytes,
        byte[]? sha256)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = contentType,
            ["x-ms-blob-type"] = "BlockBlob",
            [MetaHeader(S3CompatibleProofObjectStorage.SessionIdMetadata)] = sessionId.ToString("D"),
            [MetaHeader(S3CompatibleProofObjectStorage.OrderIdMetadata)] = orderId.ToString("D"),
            [MetaHeader(S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata)] = ownerOrganizationId.ToString("D"),
            [MetaHeader(S3CompatibleProofObjectStorage.RequestedByMetadata)] = requestedBy.ToString("D"),
            [MetaHeader(S3CompatibleProofObjectStorage.ProofTypeMetadata)] = proofType,
            [MetaHeader(S3CompatibleProofObjectStorage.SizeBytesMetadata)] = sizeBytes.ToString(CultureInfo.InvariantCulture),
        };
        if (sha256 is not null)
        {
            headers[MetaHeader(S3CompatibleProofObjectStorage.Sha256Metadata)] = Convert.ToHexString(sha256).ToLowerInvariant();
        }

        return headers;
    }

    public async Task<ProofUploadGrant> CreateUploadGrantAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid sessionId,
        Guid requestedBy,
        ProofType proofType,
        string contentType,
        long sizeBytes,
        byte[]? sha256,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var objectKey = ProofObjectKeys.Quarantine(ownerOrganizationId, orderId, sessionId);
        var now = _time.GetUtcNow();
        if (expiresAt <= now || expiresAt > now.AddMinutes(_options.UploadUrlLifetimeMinutes))
        {
            throw new ProofStorageUnavailableException();
        }

        var sas = new BlobSasBuilder(BlobSasPermissions.Create | BlobSasPermissions.Write, expiresAt)
        {
            BlobContainerName = _options.AzureBlob.ContainerName,
            BlobName = objectKey,
            Resource = "b",
            Protocol = SasProtocol.Https,
        };
        var url = await SignAsync(sas, objectKey, expiresAt - now, cancellationToken).ConfigureAwait(false);
        return new ProofUploadGrant(
            objectKey,
            url,
            RequiredUploadHeaders(
                ownerOrganizationId,
                orderId,
                sessionId,
                requestedBy,
                proofType.ToContractValue(),
                contentType,
                sizeBytes,
                sha256),
            expiresAt);
    }

    public async IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in _gateway.ListAsync(_options.QuarantinePrefix, cancellationToken).ConfigureAwait(false))
        {
            if (blob.Name.StartsWith(_options.QuarantinePrefix, StringComparison.Ordinal))
            {
                yield return ToDescriptor(blob);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
        await _gateway.DownloadAsync(objectKey, _options.MaximumBytes, cancellationToken).ConfigureAwait(false);

    public async Task PromoteAsync(ValidatedProofObject proof, CancellationToken cancellationToken)
    {
        var sha256 = Convert.ToHexString(proof.Sha256).ToLowerInvariant();
        var existing = await HeadAsync(proof.FinalObjectKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.SizeBytes == proof.SizeBytes &&
                MetadataEquals(existing.Metadata, S3CompatibleProofObjectStorage.Sha256Metadata, sha256) &&
                MetadataEquals(existing.Metadata, S3CompatibleProofObjectStorage.SessionIdMetadata, proof.SessionId.ToString("D")))
            {
                return;
            }

            throw new ProofConflictException("FINAL_OBJECT_COLLISION");
        }

        // Metadata of the final object is rebuilt by the server; nothing the client sent is copied.
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BlobMetadataName(S3CompatibleProofObjectStorage.SessionIdMetadata)] = proof.SessionId.ToString("D"),
            [BlobMetadataName(S3CompatibleProofObjectStorage.OrderIdMetadata)] = proof.OrderId.ToString("D"),
            [BlobMetadataName(S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata)] = proof.OwnerOrganizationId.ToString("D"),
            [BlobMetadataName(S3CompatibleProofObjectStorage.ProofTypeMetadata)] = proof.ProofType,
            [BlobMetadataName(S3CompatibleProofObjectStorage.Sha256Metadata)] = sha256,
            [BlobMetadataName(S3CompatibleProofObjectStorage.SizeBytesMetadata)] = proof.SizeBytes.ToString(CultureInfo.InvariantCulture),
        };
        await _gateway.CopyAsync(proof.QuarantineObjectKey, proof.ETag, proof.FinalObjectKey, metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProofObjectDescriptor?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var blob = await _gateway.GetPropertiesAsync(objectKey, cancellationToken).ConfigureAwait(false);
        return blob is null ? null : ToDescriptor(blob);
    }

    public Task DeleteQuarantineAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
        objectKey.StartsWith(_options.QuarantinePrefix, StringComparison.Ordinal)
            ? _gateway.DeleteAsync(objectKey, cancellationToken)
            : throw new ProofConflictException("NOT_A_QUARANTINE_OBJECT");

    public async Task<string> CreateInternalDownloadUrlAsync(string objectKey, CancellationToken cancellationToken)
    {
        var lifetime = TimeSpan.FromMinutes(_options.DownloadUrlLifetimeMinutes);
        var expiresAt = _time.GetUtcNow().Add(lifetime);
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresAt)
        {
            BlobContainerName = _options.AzureBlob.ContainerName,
            BlobName = objectKey,
            Resource = "b",
            Protocol = SasProtocol.Https,
        };
        return await SignAsync(sas, objectKey, lifetime, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SignAsync(
        BlobSasBuilder sas,
        string objectKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (!_signsUrls)
        {
            throw new ProofStorageUnavailableException();
        }

        var key = await DelegationKeyAsync(lifetime, cancellationToken).ConfigureAwait(false);
        var parameters = sas.ToSasQueryParameters(key, _gateway.AccountName);
        return new UriBuilder(_gateway.BlobUri(objectKey)) { Query = parameters.ToString() }.Uri.AbsoluteUri;
    }

    /// <summary>
    /// A cached user delegation key that stays valid for at least <paramref name="lifetime"/> plus a
    /// margin, so no SAS outlives the key that signed it.
    /// </summary>
    private async Task<UserDelegationKey> DelegationKeyAsync(TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var margin = TimeSpan.FromMinutes(5);
        var now = _time.GetUtcNow();
        if (_delegationKey is { } cached && cached.SignedExpiresOn - now > lifetime + margin)
        {
            return cached;
        }

        await _keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (_delegationKey is { } fresh && fresh.SignedExpiresOn - now > lifetime + margin)
            {
                return fresh;
            }

            var key = await _gateway.GetUserDelegationKeyAsync(
                    now.AddMinutes(-5),
                    now.AddMinutes(_options.AzureBlob.UserDelegationKeyLifetimeMinutes),
                    cancellationToken)
                .ConfigureAwait(false);
            if (key.SignedExpiresOn - now <= lifetime)
            {
                throw new ProofStorageUnavailableException();
            }

            _delegationKey = key;
            return key;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static ProofObjectDescriptor ToDescriptor(ProofBlob blob)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var logical in LogicalMetadataNames)
        {
            if (blob.Metadata.TryGetValue(BlobMetadataName(logical), out var value))
            {
                metadata[logical] = value;
            }
        }

        return new ProofObjectDescriptor(
            blob.Name,
            blob.SizeBytes,
            blob.ContentType,
            blob.ETag,
            null,
            metadata,
            blob.LastModified);
    }

    private static string MetaHeader(string logicalName) => "x-ms-meta-" + BlobMetadataName(logicalName);

    private static bool MetadataEquals(IReadOnlyDictionary<string, string> metadata, string name, string expected) =>
        metadata.TryGetValue(name, out var actual) && string.Equals(actual, expected, StringComparison.Ordinal);
}
