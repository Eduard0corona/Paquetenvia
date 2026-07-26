using System.Net;
using System.Net.Http.Headers;
using Amazon.S3;
using Amazon.S3.Model;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.IntegrationTests.Custody;

[Collection(DriverSignedUploadCorsCollection.Name)]
public sealed class CorsCapableS3FixtureTests(CorsCapableS3Fixture fixture)
{
    [Fact]
    public void Real_browser_pipeline_does_not_disable_origin_security()
    {
        var driverDirectory = Path.Combine(
            RepositoryRootLocator.Find(AppContext.BaseDirectory),
            "tests",
            "Paqueteria.IntegrationTests",
            "Driver");
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(driverDirectory, "*.cs")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
        string[] prohibitedArguments =
        [
            "--disable-web-security",
            "--disable-features=OutOfBlinkCors",
            "--disable-site-isolation-trials",
            "--allow-running-insecure-content",
        ];

        Assert.DoesNotContain(
            prohibitedArguments,
            argument => source.Contains(argument, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "DriverSignedUploadCors")]
    public async Task Rgw_supports_private_bucket_cors_presigned_put_and_exact_metadata()
    {
        var allowedOrigin = new Uri("http://127.0.0.1:43123");
        using var client = fixture.CreateClient();
        var acl = await client.GetBucketAclAsync(
            new GetBucketAclRequest { BucketName = CorsCapableS3Fixture.Bucket });
        Assert.DoesNotContain(
            acl.Grants,
            grant =>
                grant.Grantee.URI?.Contains(
                    "AllUsers",
                    StringComparison.OrdinalIgnoreCase) == true ||
                grant.Grantee.URI?.Contains(
                    "AuthenticatedUsers",
                    StringComparison.OrdinalIgnoreCase) == true);

        await fixture.ConfigureBrowserCorsAsync(allowedOrigin);
        CorsCapableS3Fixture.AssertExactBrowserCorsPolicy(
            Assert.IsType<CORSConfiguration>(
                await fixture.ReadBrowserCorsAsync()),
            allowedOrigin);
        await fixture.ClearBrowserCorsAsync();
        Assert.Null(await fixture.ReadBrowserCorsAsync());

        await fixture.ConfigureBrowserCorsAsync(allowedOrigin);
        var objectKey = $"capacity/{Guid.NewGuid():N}";
        var bytes = "drv-002-rgw-capacity"u8.ToArray();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-meta-session-id"] = Guid.NewGuid().ToString("D"),
            ["x-amz-meta-order-id"] = Guid.NewGuid().ToString("D"),
            ["x-amz-meta-owner-org-id"] = Guid.NewGuid().ToString("D"),
            ["x-amz-meta-requested-by"] = Guid.NewGuid().ToString("D"),
            ["x-amz-meta-proof-type"] = "DELIVERY_PHOTO",
            ["x-amz-meta-size-bytes"] = bytes.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["x-amz-meta-sha256"] =
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant(),
        };
        var presignRequest = new GetPreSignedUrlRequest
        {
            BucketName = CorsCapableS3Fixture.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Protocol = Protocol.HTTP,
            Expires = DateTime.UtcNow.AddMinutes(5),
            ContentType = "image/png",
        };
        foreach (var header in metadata)
        {
            presignRequest.Headers[header.Key] = header.Value;
        }

        var signedUrl = await client.GetPreSignedURLAsync(presignRequest);
        using var http = new HttpClient();
        using var upload = new HttpRequestMessage(HttpMethod.Put, signedUrl)
        {
            Content = new ByteArrayContent(bytes),
        };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        foreach (var header in metadata)
        {
            upload.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var uploadResponse = await http.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);

        using var stored = await client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = CorsCapableS3Fixture.Bucket,
                Key = objectKey,
            });
        using var content = new MemoryStream();
        await stored.ResponseStream.CopyToAsync(content);
        Assert.Equal(bytes, content.ToArray());
        foreach (var expected in metadata)
        {
            Assert.Equal(
                expected.Value,
                ReadMetadata(stored.Metadata, expected.Key));
        }

        Assert.Equal(metadata.Count, stored.Metadata.Keys.Count);
    }

    private static string ReadMetadata(
        MetadataCollection metadata,
        string name)
    {
        var shortName = name["x-amz-meta-".Length..];
        return metadata.Keys
            .Where(key =>
                string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, shortName, StringComparison.OrdinalIgnoreCase))
            .Select(key => metadata[key])
            .Single();
    }
}
