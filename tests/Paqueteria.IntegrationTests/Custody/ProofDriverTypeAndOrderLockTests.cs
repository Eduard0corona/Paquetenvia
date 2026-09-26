using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Custody;

/// <summary>
/// POD-001 against real PostgreSQL with an in-memory object store double:
/// EXTERNAL drivers holding a matching EXTERNAL assignment can capture pickup and
/// delivery proofs, driver/assignment type mismatches stay forbidden, and finalization
/// holds FOR SHARE on the order so a concurrent cancellation cannot leave a CANCELLED
/// order with custody evidence.
/// </summary>
[Trait("Category", "SecureProofUpload")]
public sealed class ProofDriverTypeAndOrderLockTests :
    IClassFixture<PostgreSqlSecurityWebApplicationFactory>
{
    private static readonly Guid OrganizationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OwnDriverUserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");
    private static readonly Guid OwnDriverId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd11");
    private static readonly Guid ExternalDriverUserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaae21");
    private static readonly Guid ExternalDriverId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddde021");
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private readonly PostgreSqlSecurityWebApplicationFactory postgres;

    public ProofDriverTypeAndOrderLockTests(PostgreSqlSecurityWebApplicationFactory postgres)
    {
        this.postgres = postgres;
    }

    [Fact]
    public async Task External_driver_with_external_assignment_captures_pickup_and_delivery_proofs()
    {
        await SeedExternalDriverAsync();
        await ReplaceAssignmentAsync(ExternalDriverId, "EXTERNAL");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);
        var before = await CountProofsAsync();

        await SetOrderStatusAsync("AT_PICKUP");
        var pickup = await CaptureAsync(provider, storage, ExternalDriverUserId, "PICKUP_PHOTO");
        Assert.Equal("PICKUP_PHOTO", pickup.ProofType);

        await SetOrderStatusAsync("DELIVERING");
        var delivery = await CaptureAsync(provider, storage, ExternalDriverUserId, "DELIVERY_PHOTO");
        Assert.Equal("DELIVERY_PHOTO", delivery.ProofType);

        Assert.Equal(before + 2, await CountProofsAsync());
    }

    [Theory]
    [InlineData("external-driver-own-assignment")]
    [InlineData("own-driver-external-assignment")]
    public async Task Driver_and_assignment_type_mismatch_stays_forbidden(string scenario)
    {
        await SeedExternalDriverAsync();
        var (actor, driver, assignmentType) = scenario switch
        {
            "external-driver-own-assignment" => (ExternalDriverUserId, ExternalDriverId, "OWN"),
            _ => (OwnDriverUserId, OwnDriverId, "EXTERNAL"),
        };
        await ReplaceAssignmentAsync(driver, assignmentType);
        await SetOrderStatusAsync("AT_PICKUP");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);

        await Assert.ThrowsAsync<ProofForbiddenException>(() =>
            CreateSessionAsync(provider, actor, "PICKUP_PHOTO"));
    }

    [Fact]
    public async Task Finalization_waits_for_concurrent_cancellation_and_rejects_the_cancelled_order()
    {
        await ReplaceAssignmentAsync(OwnDriverId, "OWN");
        await SetOrderStatusAsync("AT_PICKUP");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);
        var upload = await CreateSessionAsync(provider, OwnDriverUserId, "PICKUP_PHOTO");
        await MarkSessionReadyAsync(upload.Id);
        storage.PromoteForTest(upload.Id);
        var before = await CountProofsAsync();

        await using var cancellation = new NpgsqlConnection(postgres.AdminConnectionString);
        await cancellation.OpenAsync();
        await using var transaction = await cancellation.BeginTransactionAsync();
        await using (var cancel = new NpgsqlCommand(
                         """
                         SELECT status FROM orders.orders WHERE id=@order FOR UPDATE;
                         UPDATE orders.orders SET status='CANCELLED',version=version+1 WHERE id=@order;
                         """,
                         cancellation,
                         transaction))
        {
            cancel.Parameters.AddWithValue("order", OrderId);
            await cancel.ExecuteNonQueryAsync();
        }

        var finalization = FinalizeAsync(provider, OwnDriverUserId, upload.Id, "PICKUP_PHOTO");
        var blocked = await WaitUntilBlockedOrCompletedAsync(finalization);
        await transaction.CommitAsync();

        Assert.True(blocked, "Finalization must wait on the order row lock held by the cancellation.");
        var conflict = await Assert.ThrowsAsync<ProofConflictException>(() => finalization);
        Assert.Equal("ORDER_STATE_NOT_ALLOWED", conflict.Code);
        Assert.Equal(before, await CountProofsAsync());
        Assert.Equal("READY", await ReadSessionStatusAsync(upload.Id));
    }

    private async Task<bool> WaitUntilBlockedOrCompletedAsync(Task finalization)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        await using var monitor = new NpgsqlConnection(postgres.AdminConnectionString);
        await monitor.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            if (finalization.IsCompleted)
            {
                return false;
            }

            await using var probe = new NpgsqlCommand(
                """
                SELECT EXISTS (
                  SELECT 1 FROM pg_stat_activity a
                  WHERE a.datname=current_database()
                    AND a.pid<>pg_backend_pid()
                    AND a.wait_event_type='Lock')
                """,
                monitor);
            if ((bool)(await probe.ExecuteScalarAsync())!)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private async Task<ProofResult> CaptureAsync(
        ServiceProvider provider,
        InMemoryProofObjectStorage storage,
        Guid actor,
        string proofType)
    {
        var upload = await CreateSessionAsync(provider, actor, proofType);
        await MarkSessionReadyAsync(upload.Id);
        storage.PromoteForTest(upload.Id);
        return await FinalizeAsync(provider, actor, upload.Id, proofType);
    }

    private static async Task<ProofUploadSessionResult> CreateSessionAsync(
        ServiceProvider provider,
        Guid actor,
        string proofType)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofUploadSessionService>()
            .CreateAsync(
                new CreateProofUploadSessionCommand(
                    actor,
                    OrganizationId,
                    false,
                    $"pod-driver-type-{Guid.NewGuid():N}",
                    OrderId,
                    proofType,
                    "image/png",
                    Png.Length,
                    SHA256.HashData(Png),
                    "pod-driver-type"),
                default);
    }

    private static async Task<ProofResult> FinalizeAsync(
        ServiceProvider provider,
        Guid actor,
        Guid sessionId,
        string proofType)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofFinalizationService>()
            .FinalizeAsync(
                new FinalizeProofCommand(
                    actor,
                    OrganizationId,
                    false,
                    $"pod-driver-type-finalize-{Guid.NewGuid():N}",
                    OrderId,
                    sessionId,
                    proofType,
                    SHA256.HashData(Png),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    24.8,
                    -107.4,
                    null,
                    "pod-driver-type"),
                default);
    }

    private ServiceProvider BuildProvider(InMemoryProofObjectStorage storage)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Paqueteria"] = postgres.ApplicationConnectionString,
                ["ProofStorage:Provider"] = "S3Compatible",
                ["ProofStorage:ThreatScanner"] = "Synthetic",
                ["ProofStorage:ServiceUrl"] = "http://127.0.0.1:9",
                ["ProofStorage:PublicPresignUrl"] = "http://127.0.0.1:9",
                ["ProofStorage:Region"] = "us-east-1",
                ["ProofStorage:Bucket"] = "pod-driver-type",
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
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddCustodyInfrastructure(configuration, new TestHostEnvironment());
        services.RemoveAll<IProofObjectStorage>();
        services.AddSingleton<IProofObjectStorage>(storage);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task SeedExternalDriverAsync()
    {
        await ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@user,'mock-subject-pod-external-driver','ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
            SELECT gen_random_uuid(),@user,@organization,'DRIVER','ACTIVE',true
            WHERE NOT EXISTS (
              SELECT 1 FROM organizations.organization_memberships
              WHERE user_id=@user AND organization_id=@organization);
            INSERT INTO drivers.driver_profiles(id,user_id,org_id,driver_type,vehicle_type,status,home_city_id)
            VALUES (@driver,@user,@organization,'EXTERNAL','MOTORCYCLE','ACTIVE',
                    '33333333-3333-3333-3333-333333333333')
            ON CONFLICT (id) DO NOTHING;
            """,
            ("user", ExternalDriverUserId),
            ("organization", OrganizationId),
            ("driver", ExternalDriverId));
    }

    private Task ReplaceAssignmentAsync(Guid driverId, string assignmentType) =>
        ExecuteAdminAsync(
            """
            DELETE FROM dispatch.assignments WHERE order_id=@order;
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (gen_random_uuid(),@order,@organization,NULL,@driver,NULL,
                    @type,'ACCEPTED',0,clock_timestamp(),clock_timestamp());
            """,
            ("order", OrderId),
            ("organization", OrganizationId),
            ("driver", driverId),
            ("type", assignmentType));

    private Task SetOrderStatusAsync(string status) =>
        ExecuteAdminAsync(
            "UPDATE orders.orders SET status=@status WHERE id=@order;",
            ("status", status),
            ("order", OrderId));

    private Task MarkSessionReadyAsync(Guid sessionId) =>
        ExecuteAdminAsync(
            "UPDATE custody.proof_upload_sessions SET status='READY',updated_at=clock_timestamp() WHERE id=@session;",
            ("session", sessionId));

    private async Task<long> CountProofsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM custody.proofs WHERE order_id=@order;",
            connection);
        command.Parameters.AddWithValue("order", OrderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadSessionStatusAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM custody.proof_upload_sessions WHERE id=@session;",
            connection);
        command.Parameters.AddWithValue("session", sessionId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAdminAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(ProofDriverTypeAndOrderLockTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Records upload grants and exposes the promoted final object with trusted metadata.</summary>
    private sealed class InMemoryProofObjectStorage : IProofObjectStorage
    {
        private readonly ConcurrentDictionary<Guid, ProofObjectDescriptor> grants = new();
        private readonly ConcurrentDictionary<string, ProofObjectDescriptor> finals = new();

        public bool IsEnabled => true;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProofUploadGrant> CreateUploadGrantAsync(
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
            var finalKey = ProofObjectKeys.Final(ownerOrganizationId, orderId, sessionId);
            grants[sessionId] = new ProofObjectDescriptor(
                finalKey,
                sizeBytes,
                contentType,
                "etag",
                null,
                new Dictionary<string, string>
                {
                    [S3CompatibleProofObjectStorage.SessionIdMetadata] = sessionId.ToString("D"),
                    [S3CompatibleProofObjectStorage.OrderIdMetadata] = orderId.ToString("D"),
                    [S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata] = ownerOrganizationId.ToString("D"),
                    [S3CompatibleProofObjectStorage.RequestedByMetadata] = requestedBy.ToString("D"),
                    [S3CompatibleProofObjectStorage.ProofTypeMetadata] = ProofContract.ToContractValue(proofType),
                    [S3CompatibleProofObjectStorage.Sha256Metadata] =
                        Convert.ToHexString(sha256 ?? []).ToLowerInvariant(),
                    [S3CompatibleProofObjectStorage.SizeBytesMetadata] =
                        sizeBytes.ToString(CultureInfo.InvariantCulture),
                });
            return Task.FromResult(new ProofUploadGrant(
                $"quarantine/{sessionId:D}",
                $"http://127.0.0.1:9/quarantine/{sessionId:D}",
                new Dictionary<string, string> { ["Content-Type"] = contentType },
                expiresAt));
        }

        public void PromoteForTest(Guid sessionId)
        {
            var descriptor = grants[sessionId];
            finals[descriptor.ObjectKey] = descriptor;
        }

        public Task<ProofObjectDescriptor?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult(finals.TryGetValue(objectKey, out var descriptor) ? descriptor : null);

        public async IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Stream> OpenReadAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PromoteAsync(ValidatedProofObject proof, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteQuarantineAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> CreateInternalDownloadUrlAsync(string objectKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
