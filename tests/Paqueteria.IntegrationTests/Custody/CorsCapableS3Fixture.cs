using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Paqueteria.IntegrationTests.Custody;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverSignedUploadCorsCollection :
    ICollectionFixture<CorsCapableS3Fixture>
{
    public const string Name = "Driver signed upload CORS";
}

public sealed class CorsCapableS3Fixture : IAsyncLifetime
{
    public const string Image =
        "quay.io/ceph/demo:main-009c5a81-squid-centos-stream9-x86_64@sha256:8213def103f190c4b88a4f16cea5f81c303e1c5da2f86a479f9f9cc95159db1e";
    public const string AccessKey = "drv002corsaccess";
    public const string SecretKey = "drv002-cors-secret-change-me";
    public const string Bucket = "drv002-browser-cors";
    public const int BrowserCorsMaxAgeSeconds = 300;

    public static readonly IReadOnlyList<string> BrowserCorsAllowedHeaders =
    [
        "content-type",
        "x-amz-meta-session-id",
        "x-amz-meta-order-id",
        "x-amz-meta-owner-org-id",
        "x-amz-meta-requested-by",
        "x-amz-meta-proof-type",
        "x-amz-meta-size-bytes",
        "x-amz-meta-sha256",
    ];

    private IContainer? container;
    private string? previousAccessKey;
    private string? previousSecretKey;

    public string Endpoint { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        container = new ContainerBuilder(Image)
            .WithEnvironment("MON_IP", "127.0.0.1")
            .WithEnvironment("CEPH_PUBLIC_NETWORK", "0.0.0.0/0")
            .WithEnvironment("DEMO_DAEMONS", "osd,rgw")
            .WithEnvironment("CEPH_DEMO_UID", "drv002")
            .WithEnvironment("CEPH_DEMO_ACCESS_KEY", AccessKey)
            .WithEnvironment("CEPH_DEMO_SECRET_KEY", SecretKey)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                ["s3cmd", "ls"],
                strategy => strategy
                    .WithInterval(TimeSpan.FromSeconds(6))
                    .WithTimeout(TimeSpan.FromMinutes(10))))
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        Endpoint = $"http://127.0.0.1:{container.GetMappedPublicPort(8080)}";

        previousAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        previousSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", AccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", SecretKey);
        try
        {
            using var client = CreateClient();
            await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        }
        catch
        {
            RestoreAwsEnvironment();
            await container.DisposeAsync();
            throw;
        }
    }

    public AmazonS3Client CreateClient() =>
        new(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = Endpoint,
                UseHttp = true,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
            });

    public IReadOnlyDictionary<string, string?> CreateProofStorageConfiguration() =>
        new Dictionary<string, string?>
        {
            ["ProofStorage:Provider"] = "S3Compatible",
            ["ProofStorage:ThreatScanner"] = "Synthetic",
            ["ProofStorage:ServiceUrl"] = Endpoint,
            ["ProofStorage:PublicPresignUrl"] = Endpoint,
            ["ProofStorage:Region"] = "us-east-1",
            ["ProofStorage:Bucket"] = Bucket,
            ["ProofStorage:ForcePathStyle"] = "true",
            ["ProofStorage:UploadUrlLifetimeMinutes"] = "15",
            ["ProofStorage:DownloadUrlLifetimeMinutes"] = "5",
            ["ProofStorage:SessionLifetimeMinutes"] = "30",
            ["ProofStorage:ProcessingIntervalSeconds"] = "1",
            ["ProofStorage:StaleValidationSeconds"] = "60",
            ["ProofStorage:MaximumConcurrency"] = "2",
            ["ProofStorage:MaximumBytes"] = "1048576",
            ["ProofStorage:MaximumTextBytes"] = "4096",
            ["ProofStorage:QuarantinePrefix"] = "quarantine/",
            ["ProofStorage:FinalPrefix"] = "proofs/",
        };

    public async Task ConfigureBrowserCorsAsync(
        Uri allowedOrigin,
        CancellationToken cancellationToken = default)
    {
        var origin = ReadExactOrigin(allowedOrigin);
        using var client = CreateClient();
        await client.PutCORSConfigurationAsync(
            Bucket,
            new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = [origin],
                        AllowedMethods = ["PUT"],
                        AllowedHeaders = [.. BrowserCorsAllowedHeaders],
                        MaxAgeSeconds = BrowserCorsMaxAgeSeconds,
                    },
                ],
            },
            cancellationToken);
    }

    public async Task<CORSConfiguration?> ReadBrowserCorsAsync(
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        try
        {
            var response = await client.GetCORSConfigurationAsync(
                Bucket,
                cancellationToken);
            return response.Configuration;
        }
        catch (AmazonS3Exception exception)
            when (exception.StatusCode == HttpStatusCode.NotFound ||
                  exception.ErrorCode == "NoSuchCORSConfiguration")
        {
            return null;
        }
    }

    public async Task ClearBrowserCorsAsync(
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        await client.DeleteCORSConfigurationAsync(Bucket, cancellationToken);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(Endpoint))
            {
                using var client = CreateClient();
                await DeleteAllObjectsAsync(client);
                await TryClearCorsAsync(client);
                await client.DeleteBucketAsync(
                    new DeleteBucketRequest { BucketName = Bucket });
            }
        }
        finally
        {
            RestoreAwsEnvironment();
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    private void RestoreAwsEnvironment()
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousAccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecretKey);
    }

    public static void AssertExactBrowserCorsPolicy(
        CORSConfiguration configuration,
        Uri allowedOrigin)
    {
        var rule = Assert.Single(configuration.Rules);
        Assert.Equal(
            allowedOrigin.GetLeftPart(UriPartial.Authority),
            Assert.Single(rule.AllowedOrigins));
        Assert.Equal("PUT", Assert.Single(rule.AllowedMethods));
        Assert.Equal(
            BrowserCorsAllowedHeaders.Order(StringComparer.OrdinalIgnoreCase),
            rule.AllowedHeaders.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(BrowserCorsMaxAgeSeconds, rule.MaxAgeSeconds);
        Assert.True(rule.ExposeHeaders is null or { Count: 0 });
        Assert.DoesNotContain(
            rule.AllowedOrigins.Concat(rule.AllowedHeaders),
            value => value.Contains('*', StringComparison.Ordinal));
        Assert.DoesNotContain(
            rule.AllowedMethods,
            method => method is "GET" or "POST" or "DELETE");
    }

    private static string ReadExactOrigin(Uri allowedOrigin)
    {
        if (!allowedOrigin.IsAbsoluteUri ||
            (allowedOrigin.Scheme != Uri.UriSchemeHttp &&
             allowedOrigin.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(allowedOrigin.UserInfo) ||
            allowedOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(allowedOrigin.Query) ||
            !string.IsNullOrEmpty(allowedOrigin.Fragment))
        {
            throw new ArgumentException(
                "An exact HTTP or HTTPS browser origin is required.",
                nameof(allowedOrigin));
        }

        return allowedOrigin.GetLeftPart(UriPartial.Authority);
    }

    private async Task DeleteAllObjectsAsync(
        AmazonS3Client client,
        CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = Bucket,
                    ContinuationToken = continuationToken,
                },
                cancellationToken);
            foreach (var item in response.S3Objects ?? [])
            {
                await client.DeleteObjectAsync(
                    new DeleteObjectRequest
                    {
                        BucketName = Bucket,
                        Key = item.Key,
                    },
                    cancellationToken);
            }

            continuationToken =
                response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    private async Task TryClearCorsAsync(
        AmazonS3Client client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await client.DeleteCORSConfigurationAsync(Bucket, cancellationToken);
        }
        catch (AmazonS3Exception exception)
            when (exception.StatusCode == HttpStatusCode.NotFound ||
                  exception.ErrorCode == "NoSuchCORSConfiguration")
        {
        }
    }
}
