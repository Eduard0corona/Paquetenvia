using System.Net;
using System.Security.Cryptography;
using Custody.Application.ProofUploads;
using Custody.Infrastructure;
using Custody.Infrastructure.Proofs;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Custody;

[Collection(SecureProofUploadCollection.Name)]
[Trait("Category", "SecureProofUpload")]
public sealed class SecureProofUploadPipelineTests :
    IClassFixture<PostgreSqlSecurityWebApplicationFactory>
{
    private static readonly Guid ActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
    private static readonly Guid ViewerId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
    private static readonly Guid OrganizationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private readonly PostgreSqlSecurityWebApplicationFactory postgres;
    private readonly MinioProofStorageFixture minio;

    public SecureProofUploadPipelineTests(
        PostgreSqlSecurityWebApplicationFactory postgres,
        MinioProofStorageFixture minio)
    {
        this.postgres = postgres;
        this.minio = minio;
    }

    [Fact]
    public async Task Api_storage_worker_and_finalization_complete_once_without_order_transition()
    {
        await using var apiProvider = BuildProvider(postgres.ApplicationConnectionString);
        var sha = SHA256.HashData(Png);
        ProofUploadSessionResult upload;
        await using (var scope = apiProvider.CreateAsyncScope())
        {
            upload = await scope.ServiceProvider
                .GetRequiredService<IProofUploadSessionService>()
                .CreateAsync(
                    new CreateProofUploadSessionCommand(
                        ActorId,
                        OrganizationId,
                        true,
                        $"pod001-create-{Guid.NewGuid():N}",
                        OrderId,
                        "DELIVERY_PHOTO",
                        "image/png",
                        Png.Length,
                        null,
                        "secure-proof-pipeline"),
                    default);
        }

        using (var response = await PutAsync(
                   new ProofUploadGrant(
                       upload.ObjectKey,
                       upload.UploadUrl,
                       upload.RequiredHeaders,
                       upload.ExpiresAt),
                   Png))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using (var firstWorkerProvider = BuildProvider(postgres.WorkerConnectionString))
        await using (var secondWorkerProvider = BuildProvider(postgres.WorkerConnectionString))
        await using (var firstScope = firstWorkerProvider.CreateAsyncScope())
        await using (var secondScope = secondWorkerProvider.CreateAsyncScope())
        {
            await Task.WhenAll(
                firstScope.ServiceProvider
                    .GetRequiredService<IProofValidationProcessor>()
                    .ProcessAvailableAsync(default),
                secondScope.ServiceProvider
                    .GetRequiredService<IProofValidationProcessor>()
                    .ProcessAvailableAsync(default));
        }

        ProofResult proof;
        var finalizationCommand = new FinalizeProofCommand(
            ActorId,
            OrganizationId,
            true,
            $"pod001-finalize-{Guid.NewGuid():N}",
            OrderId,
            upload.Id,
            "DELIVERY_PHOTO",
            sha,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            24.8,
            -107.4,
            null,
            "secure-proof-pipeline");
        await using (var scope = apiProvider.CreateAsyncScope())
        {
            proof = await scope.ServiceProvider
                .GetRequiredService<IProofFinalizationService>()
                .FinalizeAsync(finalizationCommand, default);
        }

        Assert.Equal(upload.Id, proof.UploadSessionId);
        Assert.Equal("DELIVERY_PHOTO", proof.ProofType);
        Assert.Equal(Convert.ToHexString(sha).ToLowerInvariant(), proof.Sha256);
        var evidence = await ReadEvidenceAsync(upload.Id, proof.Id);
        Assert.Equal("CONSUMED", evidence.SessionStatus);
        Assert.Equal(1, evidence.ProofCount);
        Assert.Equal("DELIVERING", evidence.OrderStatus);
        Assert.Equal(3, evidence.AuditCount);
        Assert.Equal(2, evidence.IdempotencyCount);

        await using (var replayScope = apiProvider.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider
                .GetRequiredService<IProofFinalizationService>()
                .FinalizeAsync(finalizationCommand, default);
            Assert.Equal(proof.Id, replay.Id);
        }
        var replayEvidence = await ReadEvidenceAsync(upload.Id, proof.Id);
        Assert.Equal(1, replayEvidence.ProofCount);

        await using (var downloadScope = apiProvider.CreateAsyncScope())
        {
            var downloads = downloadScope.ServiceProvider.GetRequiredService<IProofDownloadService>();
            await Assert.ThrowsAsync<ProofForbiddenException>(() =>
                downloads.GetInternalDownloadAsync(
                    new GetProofDownloadCommand(
                        ViewerId,
                        OrganizationId,
                        false,
                        proof.Id),
                    default));
            var download = await downloads.GetInternalDownloadAsync(
                new GetProofDownloadCommand(
                    ActorId,
                    OrganizationId,
                    true,
                    proof.Id),
                default);
            using var client = new HttpClient();
            Assert.Equal(Png, await client.GetByteArrayAsync(download.DownloadUrl));
        }
        var auditEvidence = await ReadAuditEvidenceAsync(upload.Id, proof.Id);
        Assert.Contains("custody.proof.download_url_issued", auditEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(sha).ToLowerInvariant(), auditEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("http", auditEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proofs/", auditEvidence, StringComparison.Ordinal);

        using (var repeatedUpload = await PutAsync(
                   new ProofUploadGrant(
                       upload.ObjectKey,
                       upload.UploadUrl,
                       upload.RequiredHeaders,
                       upload.ExpiresAt),
                   Png))
        {
            Assert.Equal(HttpStatusCode.OK, repeatedUpload.StatusCode);
        }
        await using (var retryWorkerProvider = BuildProvider(postgres.WorkerConnectionString))
        await using (var retryScope = retryWorkerProvider.CreateAsyncScope())
        {
            await retryScope.ServiceProvider
                .GetRequiredService<IProofValidationProcessor>()
                .ProcessAvailableAsync(default);
        }
        Assert.Null(await minio.Storage.HeadAsync(upload.ObjectKey, default));

        await AssertProofIsAppendOnlyAsync(proof.Id);

        var wrongExpectedSha = new byte[32];
        ProofUploadSessionResult rejectedUpload;
        await using (var rejectedScope = apiProvider.CreateAsyncScope())
        {
            rejectedUpload = await rejectedScope.ServiceProvider
                .GetRequiredService<IProofUploadSessionService>()
                .CreateAsync(
                    new CreateProofUploadSessionCommand(
                        ActorId,
                        OrganizationId,
                        true,
                        $"pod001-rejected-{Guid.NewGuid():N}",
                        OrderId,
                        "DELIVERY_PHOTO",
                        "image/png",
                        Png.Length,
                        wrongExpectedSha,
                        "secure-proof-rejection"),
                    default);
        }
        using (var rejectedPut = await PutAsync(
                   new ProofUploadGrant(
                       rejectedUpload.ObjectKey,
                       rejectedUpload.UploadUrl,
                       rejectedUpload.RequiredHeaders,
                       rejectedUpload.ExpiresAt),
                   Png))
        {
            Assert.Equal(HttpStatusCode.OK, rejectedPut.StatusCode);
        }
        await MarkValidationStaleAsync(rejectedUpload.Id);
        await using (var recoveryWorkerProvider = BuildProvider(postgres.WorkerConnectionString))
        await using (var recoveryScope = recoveryWorkerProvider.CreateAsyncScope())
        {
            await recoveryScope.ServiceProvider
                .GetRequiredService<IProofValidationProcessor>()
                .ProcessAvailableAsync(default);
        }
        Assert.Equal("REJECTED", await ReadSessionStatusAsync(rejectedUpload.Id));
        Assert.NotNull(await minio.Storage.HeadAsync(rejectedUpload.ObjectKey, default));
    }

    private ServiceProvider BuildProvider(string connectionString)
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
            ["ProofStorage:MaximumConcurrency"] = "2",
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
        services.AddCustodyInfrastructure(
            configuration,
            new TestHostEnvironment());
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(SecureProofUploadPipelineTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private async Task<(string SessionStatus, int ProofCount, string OrderStatus, int AuditCount, int IdempotencyCount)>
        ReadEvidenceAsync(Guid sessionId, Guid proofId)
    {
        await using var connection = new NpgsqlConnection(postgres.ApplicationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ApplyAppContextAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            SELECT
              (SELECT status FROM custody.proof_upload_sessions WHERE id=@session),
              (SELECT count(*)::integer FROM custody.proofs WHERE id=@proof),
              (SELECT status FROM orders.orders WHERE id=@order),
              (SELECT count(*)::integer FROM platform.audit_logs
               WHERE entity_id IN (@session,@proof)
                 AND action IN (
                   'custody.proof_upload_session.created',
                   'custody.proof_upload_session.ready',
                   'custody.proof.finalized')),
              (SELECT count(*)::integer FROM platform.idempotency_keys
               WHERE owner_org_id=@org AND scope LIKE 'POD-001:%')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("proof", proofId);
        command.Parameters.AddWithValue("order", OrderId);
        command.Parameters.AddWithValue("org", OrganizationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    private async Task AssertProofIsAppendOnlyAsync(Guid proofId)
    {
        await using var connection = new NpgsqlConnection(postgres.ApplicationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ApplyAppContextAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            "UPDATE custody.proofs SET content_type='text/plain' WHERE id=@proof;",
            connection,
            transaction);
        command.Parameters.AddWithValue("proof", proofId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
    }

    private async Task MarkValidationStaleAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(postgres.ApplicationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ApplyAppContextAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            UPDATE custody.proof_upload_sessions
            SET status='VALIDATING',updated_at=now()-interval '2 minutes'
            WHERE id=@session AND status='CREATED';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
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

    private async Task<string> ReadAuditEvidenceAsync(Guid sessionId, Guid proofId)
    {
        await using var connection = new NpgsqlConnection(postgres.ApplicationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ApplyAppContextAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(action || ':' || payload_redacted::text,E'\n' ORDER BY occurred_at)
            FROM platform.audit_logs
            WHERE entity_id IN (@session,@proof);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("proof", proofId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task ApplyAppContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
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
