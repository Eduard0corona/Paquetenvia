using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Custody.Infrastructure.ProofStorage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;

namespace Paqueteria.IntegrationTests.Custody;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecureProofUploadCollection : ICollectionFixture<MinioProofStorageFixture>
{
    public const string Name = "SecureProofUpload";
}

public sealed class MinioProofStorageFixture : IAsyncLifetime
{
    public const string Image =
        "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e";
    public const string AccessKey = "pod001-test-access";
    public const string SecretKey = "pod001-test-secret-change-me";
    public const string Bucket = "pod001-secure-proofs";

    private IContainer? container;
    private string? previousAccessKey;
    private string? previousSecretKey;

    public string Endpoint { get; private set; } = string.Empty;
    public S3CompatibleProofObjectStorage Storage { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        container = new ContainerBuilder(Image)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithCommand("server", "/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(9000).ForPath("/minio/health/ready")))
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        Endpoint = $"http://127.0.0.1:{container.GetMappedPublicPort(9000)}";

        previousAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        previousSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", AccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", SecretKey);
        var config = new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            config);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        Storage = new S3CompatibleProofObjectStorage(Options.Create(new ProofStorageOptions
        {
            Provider = ProofStorageProvider.S3Compatible,
            ServiceUrl = Endpoint,
            PublicPresignUrl = Endpoint,
            Region = "us-east-1",
            Bucket = Bucket,
            ForcePathStyle = true,
            MaximumBytes = 1024 * 1024,
            QuarantinePrefix = "quarantine/",
            FinalPrefix = "proofs/",
        }));
    }

    public async Task DisposeAsync()
    {
        Storage?.Dispose();
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousAccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecretKey);
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
