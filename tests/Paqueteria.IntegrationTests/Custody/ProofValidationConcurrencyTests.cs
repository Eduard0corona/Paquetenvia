using System.Net;
using Custody.Application.ProofUploads;
using Custody.Infrastructure;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Custody;

/// <summary>
/// POD-001 (project owner, 2026-11-02: "PR aparte primero (Recommended)"): one validation pass with several quarantined
/// photos at a concurrency above one validates every photo, because each object runs in its own DI scope (its own
/// Worker tenant transaction context and Custody DbContext). Real PostgreSQL (the Worker login, NOBYPASSRLS) and the
/// S3-compatible storage double. A scanner decorator holds each photo of this test at the verdict check until all of
/// them got there, so they all claim their session at the same moment: before the fix the second claim on the shared
/// DbContext failed and the pass threw.
/// </summary>
[Collection(SecureProofUploadCollection.Name)]
[Trait("Category", "SecureProofUpload")]
public sealed class ProofValidationConcurrencyTests :
    IClassFixture<PostgreSqlSecurityWebApplicationFactory>
{
    private const int Photos = 3;

    private static readonly Guid ActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
    private static readonly Guid OrganizationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private readonly PostgreSqlSecurityWebApplicationFactory postgres;
    private readonly MinioProofStorageFixture minio;

    public ProofValidationConcurrencyTests(
        PostgreSqlSecurityWebApplicationFactory postgres,
        MinioProofStorageFixture minio)
    {
        this.postgres = postgres;
        this.minio = minio;
    }

    [Fact]
    public async Task One_pass_validates_every_quarantined_photo_at_once_each_in_its_own_scope()
    {
        await using var apiProvider = BuildProvider(postgres.ApplicationConnectionString);
        var uploads = new List<ProofUploadSessionResult>();
        for (var photo = 0; photo < Photos; photo++)
        {
            await using var scope = apiProvider.CreateAsyncScope();
            var upload = await scope.ServiceProvider
                .GetRequiredService<IProofUploadSessionService>()
                .CreateAsync(
                    new CreateProofUploadSessionCommand(
                        ActorId,
                        OrganizationId,
                        true,
                        $"pod001-scope-{Guid.NewGuid():N}",
                        OrderId,
                        "DELIVERY_PHOTO",
                        "image/png",
                        Png.Length,
                        null,
                        "proof-validation-scope"),
                    default);
            using var response = await PutAsync(upload, Png);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            uploads.Add(upload);
        }

        var together = new SimultaneousVerdictCheck(uploads.Select(upload => upload.ObjectKey));
        await using (var workerProvider = BuildProvider(
                         postgres.WorkerConnectionString,
                         maximumConcurrency: Photos,
                         together))
        await using (var scope = workerProvider.CreateAsyncScope())
        {
            // One pass. Before the fix it threw ProofStorageUnavailableException and left photos CREATED or UPLOADED.
            await scope.ServiceProvider
                .GetRequiredService<IProofValidationProcessor>()
                .ProcessAvailableAsync(default);
        }

        Assert.Equal(Photos, together.Arrived);
        foreach (var upload in uploads)
        {
            Assert.Equal("READY", await ReadSessionStatusAsync(upload.Id));
            Assert.Equal(1, await CountReadyAuditRowsAsync(upload.Id));
            Assert.Null(await minio.Storage.HeadAsync(upload.ObjectKey, default));
        }
    }

    private ServiceProvider BuildProvider(
        string connectionString,
        int maximumConcurrency = 2,
        SimultaneousVerdictCheck? together = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Paqueteria"] = connectionString,
            ["ProofStorage:Provider"] = "S3Compatible",
            ["ProofStorage:ThreatScanner"] = "Synthetic",
            ["ProofStorage:ServiceUrl"] = minio.Endpoint,
            ["ProofStorage:PublicPresignUrl"] = minio.Endpoint,
            ["ProofStorage:Region"] = "us-east-1",
            ["ProofStorage:Bucket"] = MinioProofStorageFixture.Bucket,
            ["ProofStorage:ForcePathStyle"] = "true",
            ["ProofStorage:UploadUrlLifetimeMinutes"] = "15",
            ["ProofStorage:DownloadUrlLifetimeMinutes"] = "5",
            ["ProofStorage:SessionLifetimeMinutes"] = "30",
            ["ProofStorage:ProcessingIntervalSeconds"] = "1",
            ["ProofStorage:StaleValidationSeconds"] = "60",
            ["ProofStorage:MaximumConcurrency"] = maximumConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ProofStorage:MaximumBytes"] = "1048576",
            ["ProofStorage:MaximumTextBytes"] = "4096",
            ["ProofStorage:QuarantinePrefix"] = "quarantine/",
            ["ProofStorage:FinalPrefix"] = "proofs/",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTelemetryOrganizationPseudonym(configuration, new TestHostEnvironment());
        services.AddCustodyInfrastructure(configuration, new TestHostEnvironment());
        if (together is not null)
        {
            // The Synthetic scanner, held at the verdict check until every photo of this test reached it.
            services.AddSingleton<IProofThreatScanner>(serviceProvider =>
                together.Wrap(serviceProvider.GetRequiredService<StrictSyntheticThreatScanner>()));
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task<string> ReadSessionStatusAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(postgres.ApplicationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ApplyAppContextAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            "SELECT status FROM custody.proof_upload_sessions WHERE id=@session;",
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountReadyAuditRowsAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(postgres.ApplicationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ApplyAppContextAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::integer FROM platform.audit_logs
            WHERE entity_id=@session AND action='custody.proof_upload_session.ready' AND org_id=@org
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("org", OrganizationId);
        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    private static async Task ApplyAppContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT set_config('app.current_user_id',@user::uuid::text,true);
            SELECT set_config('app.current_org_ids',@organizations::uuid[]::text,true);
            SET LOCAL ROLE paqueteria_app;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user", NpgsqlDbType.Uuid, ActorId);
        command.Parameters.AddWithValue(
            "organizations",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            new[] { OrganizationId });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> PutAsync(ProofUploadSessionResult upload, byte[] content)
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        foreach (var header in upload.RequiredHeaders)
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(ProofValidationConcurrencyTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// Releases the photos of this test from the verdict check only once all of them reached it, so their claims run at
    /// the same moment. Objects left in the shared bucket by other classes of the collection pass straight through.
    /// </summary>
    private sealed class SimultaneousVerdictCheck(IEnumerable<string> objectKeys)
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
        private readonly HashSet<string> keys = new(objectKeys, StringComparer.Ordinal);
        private readonly TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;

        internal int Arrived => Volatile.Read(ref arrived);

        internal IProofThreatScanner Wrap(IProofThreatScanner inner) => new Scanner(inner, this);

        private async ValueTask ArriveAsync(string objectKey, CancellationToken cancellationToken)
        {
            if (!keys.Contains(objectKey))
            {
                return;
            }

            if (Interlocked.Increment(ref arrived) == keys.Count)
            {
                allArrived.TrySetResult();
            }

            await allArrived.Task.WaitAsync(Limit, cancellationToken);
        }

        private sealed class Scanner(IProofThreatScanner inner, SimultaneousVerdictCheck check) : IProofThreatScanner
        {
            public bool IsEnabled => inner.IsEnabled;

            public ValueTask<ProofThreatScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) =>
                inner.ScanAsync(content, cancellationToken);

            public ValueTask<ProofThreatScanResult> ScanAsync(
                ProofObjectDescriptor descriptor,
                Stream content,
                CancellationToken cancellationToken) =>
                inner.ScanAsync(descriptor, content, cancellationToken);

            public async ValueTask<bool> IsVerdictPendingAsync(
                ProofObjectDescriptor descriptor,
                CancellationToken cancellationToken)
            {
                await check.ArriveAsync(descriptor.ObjectKey, cancellationToken);
                return await inner.IsVerdictPendingAsync(descriptor, cancellationToken);
            }

            public ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
                inner.CheckHealthAsync(cancellationToken);
        }
    }
}
