using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Microsoft.Extensions.Options;

namespace Custody.Infrastructure.ProofStorage;

public sealed class S3CompatibleProofObjectStorage : IProofObjectStorage, IDisposable
{
    public const string SessionIdMetadata = "session-id";
    public const string OrderIdMetadata = "order-id";
    public const string OwnerOrganizationIdMetadata = "owner-org-id";
    public const string RequestedByMetadata = "requested-by";
    public const string ProofTypeMetadata = "proof-type";
    public const string Sha256Metadata = "sha256";
    public const string SizeBytesMetadata = "size-bytes";

    private readonly ProofStorageOptions options;
    private readonly AmazonS3Client storageClient;
    private readonly AmazonS3Client presignClient;

    public S3CompatibleProofObjectStorage(IOptions<ProofStorageOptions> options)
    {
        this.options = options.Value;
        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "ProofStorage:S3Compatible requires AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY.");
        }

        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        storageClient = new AmazonS3Client(credentials, CreateConfig(this.options.ServiceUrl));
        presignClient = new AmazonS3Client(credentials, CreateConfig(this.options.PublicPresignUrl));
    }

    public bool IsEnabled => true;

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await storageClient.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.Bucket,
                Prefix = options.QuarantinePrefix,
                MaxKeys = 1,
            }, cancellationToken);
            try
            {
                var acl = await storageClient.GetBucketAclAsync(
                    new GetBucketAclRequest { BucketName = options.Bucket },
                    cancellationToken);
                if (acl.Grants.Any(grant =>
                        grant.Grantee.URI?.Contains(
                            "AllUsers",
                            StringComparison.OrdinalIgnoreCase) == true ||
                        grant.Grantee.URI?.Contains(
                            "AuthenticatedUsers",
                            StringComparison.OrdinalIgnoreCase) == true))
                {
                    return false;
                }
            }
            catch (AmazonS3Exception exception)
                when (exception.ErrorCode == "AccessControlListNotSupported")
            {
                // Bucket-owner-enforced S3 disables ACLs and therefore cannot
                // contain a public ACL grant.
            }

            var signingProbe = await presignClient.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = options.Bucket,
                Key = $"{options.FinalPrefix}readiness",
                Verb = HttpVerb.GET,
                Protocol = new Uri(options.PublicPresignUrl).Scheme == Uri.UriSchemeHttp
                    ? Protocol.HTTP
                    : Protocol.HTTPS,
                Expires = DateTime.UtcNow.AddMinutes(1),
            });
            return Uri.TryCreate(signingProbe, UriKind.Absolute, out var signedUri) &&
                signedUri.GetLeftPart(UriPartial.Authority) ==
                new Uri(options.PublicPresignUrl).GetLeftPart(UriPartial.Authority);
        }
        catch (AmazonS3Exception)
        {
            return false;
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        var objectKey = ProofObjectKeys.Quarantine(ownerOrganizationId, orderId, sessionId);
        var requiredHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = contentType,
            [$"x-amz-meta-{SessionIdMetadata}"] = sessionId.ToString("D"),
            [$"x-amz-meta-{OrderIdMetadata}"] = orderId.ToString("D"),
            [$"x-amz-meta-{OwnerOrganizationIdMetadata}"] = ownerOrganizationId.ToString("D"),
            [$"x-amz-meta-{RequestedByMetadata}"] = requestedBy.ToString("D"),
            [$"x-amz-meta-{ProofTypeMetadata}"] = proofType.ToContractValue(),
            [$"x-amz-meta-{SizeBytesMetadata}"] = sizeBytes.ToString(CultureInfo.InvariantCulture),
        };
        if (sha256 is not null)
        {
            requiredHeaders[$"x-amz-meta-{Sha256Metadata}"] =
                Convert.ToHexString(sha256).ToLowerInvariant();
        }
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Protocol = new Uri(options.PublicPresignUrl).Scheme == Uri.UriSchemeHttp
                ? Protocol.HTTP
                : Protocol.HTTPS,
            Expires = expiresAt.UtcDateTime,
            ContentType = contentType,
        };
        foreach (var header in requiredHeaders.Where(value => value.Key != "Content-Type"))
        {
            request.Headers[header.Key] = header.Value;
        }

        var url = await presignClient.GetPreSignedURLAsync(request);
        return new ProofUploadGrant(objectKey, url, requiredHeaders, expiresAt);
    }

    public async IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            var response = await storageClient.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.Bucket,
                Prefix = options.QuarantinePrefix,
                ContinuationToken = continuationToken,
            }, cancellationToken);
            foreach (var item in response.S3Objects)
            {
                var descriptor = await HeadAsync(item.Key, cancellationToken);
                if (descriptor is not null)
                {
                    yield return descriptor;
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    public async Task<Stream> OpenReadAsync(
        string objectKey,
        string? versionId,
        CancellationToken cancellationToken)
    {
        using var response = await storageClient.GetObjectAsync(new GetObjectRequest
        {
            BucketName = options.Bucket,
            Key = objectKey,
            VersionId = versionId,
        }, cancellationToken);
        var content = new MemoryStream(
            checked((int)Math.Min(response.ContentLength, options.MaximumBytes + 1)));
        await response.ResponseStream.CopyToAsync(content, cancellationToken);
        content.Position = 0;
        return content;
    }

    public async Task PromoteAsync(ValidatedProofObject proof, CancellationToken cancellationToken)
    {
        var existing = await HeadAsync(proof.FinalObjectKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.SizeBytes == proof.SizeBytes &&
                MetadataEquals(existing.Metadata, Sha256Metadata, Convert.ToHexString(proof.Sha256).ToLowerInvariant()) &&
                MetadataEquals(existing.Metadata, SessionIdMetadata, proof.SessionId.ToString("D")))
            {
                return;
            }

            throw new ProofConflictException("FINAL_OBJECT_COLLISION");
        }

        var request = new CopyObjectRequest
        {
            SourceBucket = options.Bucket,
            SourceKey = proof.QuarantineObjectKey,
            SourceVersionId = proof.VersionId,
            DestinationBucket = options.Bucket,
            DestinationKey = proof.FinalObjectKey,
            ETagToMatch = proof.ETag,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = proof.ContentType,
        };
        request.Metadata[SessionIdMetadata] = proof.SessionId.ToString("D");
        request.Metadata[OrderIdMetadata] = proof.OrderId.ToString("D");
        request.Metadata[OwnerOrganizationIdMetadata] = proof.OwnerOrganizationId.ToString("D");
        request.Metadata[ProofTypeMetadata] = proof.ProofType;
        request.Metadata[Sha256Metadata] = Convert.ToHexString(proof.Sha256).ToLowerInvariant();
        request.Metadata[SizeBytesMetadata] = proof.SizeBytes.ToString(CultureInfo.InvariantCulture);
        await storageClient.CopyObjectAsync(request, cancellationToken);
    }

    public async Task<ProofObjectDescriptor?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await storageClient.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.Bucket,
                Key = objectKey,
            }, cancellationToken);
            var metadata = response.Metadata.Keys.ToDictionary(
                NormalizeMetadataName,
                key => response.Metadata[key],
                StringComparer.OrdinalIgnoreCase);
            return new ProofObjectDescriptor(
                objectKey,
                response.ContentLength,
                response.Headers.ContentType ?? string.Empty,
                response.ETag,
                response.VersionId,
                metadata);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteQuarantineAsync(
        string objectKey,
        string? versionId,
        CancellationToken cancellationToken) =>
        storageClient.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = options.Bucket,
            Key = objectKey,
            VersionId = versionId,
        }, cancellationToken);

    public async Task<string> CreateInternalDownloadUrlAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await presignClient.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Protocol = new Uri(options.PublicPresignUrl).Scheme == Uri.UriSchemeHttp
                ? Protocol.HTTP
                : Protocol.HTTPS,
            Expires = DateTime.UtcNow.AddMinutes(options.DownloadUrlLifetimeMinutes),
        });
    }

    public void Dispose()
    {
        storageClient.Dispose();
        presignClient.Dispose();
    }

    private AmazonS3Config CreateConfig(string serviceUrl) => new()
    {
        ServiceURL = serviceUrl,
        UseHttp = new Uri(serviceUrl).Scheme == Uri.UriSchemeHttp,
        ForcePathStyle = options.ForcePathStyle,
        AuthenticationRegion = options.Region,
    };

    private static string NormalizeMetadataName(string name) =>
        name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
            ? name["x-amz-meta-".Length..]
            : name;

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, string> metadata,
        string name,
        string expected) =>
        metadata.TryGetValue(name, out var actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);
}
