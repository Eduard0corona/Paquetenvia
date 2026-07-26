using System.Net;
using System.Security.Cryptography;
using Custody.Application.ProofUploads;
using Custody.Domain;

namespace Paqueteria.IntegrationTests.Custody;

[Collection(SecureProofUploadCollection.Name)]
[Trait("Category", "SecureProofUpload")]
public sealed class MinioProofStorageTests(MinioProofStorageFixture fixture)
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [Fact]
    public async Task Presigned_put_targets_private_quarantine_and_requires_signed_metadata()
    {
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var session = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var sha = SHA256.HashData(Png);
        var grant = await fixture.Storage.CreateUploadGrantAsync(
            owner,
            order,
            session,
            requester,
            ProofType.PickupPhoto,
            "image/png",
            Png.Length,
            sha,
            DateTimeOffset.UtcNow.AddMinutes(10),
            default);

        Assert.Equal(ProofObjectKeys.Quarantine(owner, order, session), grant.ObjectKey);
        Assert.StartsWith(fixture.Endpoint, grant.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("/proofs/", grant.Url, StringComparison.Ordinal);
        Assert.Equal("image/png", grant.RequiredHeaders["Content-Type"]);
        Assert.Equal(session.ToString("D"), grant.RequiredHeaders["x-amz-meta-session-id"]);
        Assert.Equal(requester.ToString("D"), grant.RequiredHeaders["x-amz-meta-requested-by"]);
        Assert.Equal(
            Convert.ToHexString(sha).ToLowerInvariant(),
            grant.RequiredHeaders["x-amz-meta-sha256"]);

        var wrongContentTypeHeaders = grant.RequiredHeaders.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.OrdinalIgnoreCase);
        wrongContentTypeHeaders["Content-Type"] = "text/plain";
        using (var wrongContentType = await PutAsync(
                   grant with { RequiredHeaders = wrongContentTypeHeaders },
                   Png))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongContentType.StatusCode);
        }

        var wrongMetadataHeaders = grant.RequiredHeaders.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.OrdinalIgnoreCase);
        wrongMetadataHeaders["x-amz-meta-owner-org-id"] = Guid.NewGuid().ToString("D");
        using (var wrongMetadata = await PutAsync(
                   grant with { RequiredHeaders = wrongMetadataHeaders },
                   Png))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongMetadata.StatusCode);
        }

        using (var finalPrefix = await PutAsync(
                   grant with
                   {
                       Url = grant.Url.Replace(
                           "/quarantine/",
                           "/proofs/",
                           StringComparison.Ordinal),
                   },
                   Png))
        {
            Assert.Equal(HttpStatusCode.Forbidden, finalPrefix.StatusCode);
        }

        using var response = await PutAsync(grant, Png);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await fixture.Storage.HeadAsync(grant.ObjectKey, default);
        Assert.NotNull(stored);
        Assert.Equal(Png.Length, stored.SizeBytes);
        Assert.Equal("image/png", stored.ContentType);

        using var anonymous = new HttpClient();
        using var anonymousResponse = await anonymous.GetAsync(
            $"{fixture.Endpoint}/{MinioProofStorageFixture.Bucket}/{grant.ObjectKey}");
        Assert.Equal(HttpStatusCode.Forbidden, anonymousResponse.StatusCode);
    }

    [Fact]
    public async Task Conditional_promotion_is_idempotent_and_publishes_only_trusted_metadata()
    {
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var session = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var sha = SHA256.HashData(Png);
        var grant = await fixture.Storage.CreateUploadGrantAsync(
            owner,
            order,
            session,
            requester,
            ProofType.DeliveryPhoto,
            "image/png",
            Png.Length,
            sha,
            DateTimeOffset.UtcNow.AddMinutes(10),
            default);
        using (var response = await PutAsync(grant, Png))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var quarantine = Assert.IsType<ProofObjectDescriptor>(
            await fixture.Storage.HeadAsync(grant.ObjectKey, default));
        var finalKey = ProofObjectKeys.Final(owner, order, session);
        var validated = new ValidatedProofObject(
            session,
            order,
            owner,
            "DELIVERY_PHOTO",
            grant.ObjectKey,
            finalKey,
            Png.Length,
            "image/png",
            sha,
            quarantine.ETag,
            quarantine.VersionId);
        await fixture.Storage.PromoteAsync(validated, default);
        await fixture.Storage.PromoteAsync(validated, default);

        var promoted = Assert.IsType<ProofObjectDescriptor>(
            await fixture.Storage.HeadAsync(finalKey, default));
        Assert.Equal(Png.Length, promoted.SizeBytes);
        Assert.Equal(session.ToString("D"), promoted.Metadata["session-id"]);
        Assert.Equal(order.ToString("D"), promoted.Metadata["order-id"]);
        Assert.Equal(owner.ToString("D"), promoted.Metadata["owner-org-id"]);
        Assert.Equal("DELIVERY_PHOTO", promoted.Metadata["proof-type"]);
        Assert.Equal(Convert.ToHexString(sha).ToLowerInvariant(), promoted.Metadata["sha256"]);
        Assert.Equal(Png.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            promoted.Metadata["size-bytes"]);
        Assert.Equal(6, promoted.Metadata.Count);

        var downloadUrl = await fixture.Storage.CreateInternalDownloadUrlAsync(finalKey, default);
        using var client = new HttpClient();
        using var write = await client.PutAsync(downloadUrl, new ByteArrayContent(Png));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        using var delete = await client.DeleteAsync(downloadUrl);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Conditional_promotion_rejects_an_overwritten_source_object()
    {
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var session = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var sha = SHA256.HashData(Png);
        var grant = await fixture.Storage.CreateUploadGrantAsync(
            owner,
            order,
            session,
            requester,
            ProofType.PickupPhoto,
            "image/png",
            Png.Length,
            sha,
            DateTimeOffset.UtcNow.AddMinutes(10),
            default);
        using (var first = await PutAsync(grant, Png))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var validatedVersion = Assert.IsType<ProofObjectDescriptor>(
            await fixture.Storage.HeadAsync(grant.ObjectKey, default));
        var replacement = Png.ToArray();
        replacement[^1] = 0x0b;
        using (var second = await PutAsync(grant, replacement))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        var error = await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() =>
            fixture.Storage.PromoteAsync(
                new ValidatedProofObject(
                    session,
                    order,
                    owner,
                    "PICKUP_PHOTO",
                    grant.ObjectKey,
                    ProofObjectKeys.Final(owner, order, session),
                    Png.Length,
                    "image/png",
                    sha,
                    validatedVersion.ETag,
                    validatedVersion.VersionId),
                default));
        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Null(await fixture.Storage.HeadAsync(
            ProofObjectKeys.Final(owner, order, session),
            default));
    }

    private static async Task<HttpResponseMessage> PutAsync(
        ProofUploadGrant grant,
        byte[] content)
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Put, grant.Url)
        {
            Content = new ByteArrayContent(content),
        };
        foreach (var header in grant.RequiredHeaders)
        {
            if (header.Key == "Content-Type")
            {
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(header.Value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return await client.SendAsync(request);
    }
}
