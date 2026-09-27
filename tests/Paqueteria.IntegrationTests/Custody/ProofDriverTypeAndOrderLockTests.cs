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
    private static readonly Guid SecondExternalDriverUserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaae22");
    private static readonly Guid SecondExternalDriverId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddde022");
    private static readonly Guid OperatorOrganizationId =
        PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId;
    private static readonly Guid OperatorExternalDriverUserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaae23");
    private static readonly Guid OperatorExternalDriverId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddde023");
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
    public async Task Authorized_external_driver_downloads_its_own_proof()
    {
        await SeedExternalDriverAsync();
        await ReplaceAssignmentAsync(ExternalDriverId, "EXTERNAL");
        await SetOrderStatusAsync("AT_PICKUP");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);
        var proof = await CaptureAsync(provider, storage, ExternalDriverUserId, "PICKUP_PHOTO");

        var download = await DownloadAsync(provider, ExternalDriverUserId, OrganizationId, proof.Id);

        Assert.Equal(proof.Id, download.ProofId);
        Assert.Equal(InMemoryProofObjectStorage.DownloadUrl(await ReadProofObjectKeyAsync(proof.Id)), download.DownloadUrl);

        // Once the assignment is no longer ACCEPTED/ACTIVE the same driver loses download access.
        await ExecuteAdminAsync(
            "UPDATE dispatch.assignments SET status='COMPLETED' WHERE order_id=@order;",
            ("order", OrderId));
        await Assert.ThrowsAsync<ProofForbiddenException>(() =>
            DownloadAsync(provider, ExternalDriverUserId, OrganizationId, proof.Id));
    }

    [Fact]
    public async Task External_driver_holding_another_drivers_external_assignment_is_forbidden()
    {
        await SeedExternalDriverAsync();
        await SeedDriverAsync(
            SecondExternalDriverUserId,
            SecondExternalDriverId,
            OrganizationId,
            "mock-subject-pod-second-external-driver");
        await ReplaceAssignmentAsync(SecondExternalDriverId, "EXTERNAL");
        await SetOrderStatusAsync("AT_PICKUP");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);

        await Assert.ThrowsAsync<ProofForbiddenException>(() =>
            CreateSessionAsync(provider, ExternalDriverUserId, "PICKUP_PHOTO"));

        // The assigned external driver of the same organization is the one who may capture.
        var upload = await CreateSessionAsync(provider, SecondExternalDriverUserId, "PICKUP_PHOTO");
        Assert.NotEqual(Guid.Empty, upload.Id);
    }

    [Fact]
    public async Task External_driver_of_another_organization_does_not_see_the_order()
    {
        await SeedDriverAsync(
            OperatorExternalDriverUserId,
            OperatorExternalDriverId,
            OperatorOrganizationId,
            "mock-subject-pod-operator-external-driver");

        // Even with an EXTERNAL assignment naming it, a driver of an organization that is neither
        // the owner nor the operator of the order gets the uniform RLS 404, not a 403.
        await ReplaceAssignmentAsync(OperatorExternalDriverId, "EXTERNAL");
        await SetOrderStatusAsync("AT_PICKUP");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);

        await Assert.ThrowsAsync<ProofNotFoundException>(() =>
            CreateSessionAsync(provider, OperatorExternalDriverUserId, "PICKUP_PHOTO", OperatorOrganizationId));
    }

    [Fact]
    public async Task External_driver_of_the_operator_organization_captures_and_downloads_proofs()
    {
        await SeedDriverAsync(
            OperatorExternalDriverUserId,
            OperatorExternalDriverId,
            OperatorOrganizationId,
            "mock-subject-pod-operator-external-driver");
        await SetOrderOperatorAsync(OperatorOrganizationId);
        try
        {
            await ReplaceAssignmentAsync(OperatorExternalDriverId, "EXTERNAL", OperatorOrganizationId);
            await SetOrderStatusAsync("AT_PICKUP");
            var storage = new InMemoryProofObjectStorage();
            await using var provider = BuildProvider(storage);
            var before = await CountProofsAsync();

            var proof = await CaptureAsync(
                provider,
                storage,
                OperatorExternalDriverUserId,
                "PICKUP_PHOTO",
                OperatorOrganizationId);

            Assert.Equal(before + 1, await CountProofsAsync());
            var download = await DownloadAsync(
                provider,
                OperatorExternalDriverUserId,
                OperatorOrganizationId,
                proof.Id);
            Assert.Equal(proof.Id, download.ProofId);

            // An assignment that does not mirror the order's operator is not tenant-consistent.
            await ReplaceAssignmentAsync(OperatorExternalDriverId, "EXTERNAL");
            await Assert.ThrowsAsync<ProofForbiddenException>(() =>
                CreateSessionAsync(provider, OperatorExternalDriverUserId, "PICKUP_PHOTO", OperatorOrganizationId));
        }
        finally
        {
            await SetOrderOperatorAsync(null);
        }
    }

    [Fact]
    public async Task Finalization_rereads_authorization_after_waiting_for_the_order_lock()
    {
        await SeedExternalDriverAsync();
        await ReplaceAssignmentAsync(ExternalDriverId, "EXTERNAL");
        await SetOrderStatusAsync("AT_PICKUP");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);
        var upload = await CreateSessionAsync(provider, ExternalDriverUserId, "PICKUP_PHOTO");
        await MarkSessionReadyAsync(upload.Id);
        storage.PromoteForTest(upload.Id);
        var before = await CountProofsAsync();

        // The concurrent transition keeps the order status but cancels the assignment while it
        // holds the order row. The authorization read runs as a new statement after the lock
        // is granted, so it sees the cancelled assignment instead of the pre-wait snapshot.
        await using var transition = new NpgsqlConnection(postgres.AdminConnectionString);
        await transition.OpenAsync();
        await using var transaction = await transition.BeginTransactionAsync();
        await using (var cancel = new NpgsqlCommand(
                         """
                         SELECT status FROM orders.orders WHERE id=@order FOR UPDATE;
                         UPDATE dispatch.assignments SET status='CANCELLED' WHERE order_id=@order;
                         UPDATE orders.orders SET version=version+1 WHERE id=@order;
                         """,
                         transition,
                         transaction))
        {
            cancel.Parameters.AddWithValue("order", OrderId);
            await cancel.ExecuteNonQueryAsync();
        }

        var finalization = FinalizeAsync(provider, ExternalDriverUserId, upload.Id, "PICKUP_PHOTO");
        var blocked = await WaitUntilBlockedByAsync(transition.ProcessID, finalization);
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<ProofForbiddenException>(() => finalization);
        Assert.Equal(before, await CountProofsAsync());
        Assert.Equal("READY", await ReadSessionStatusAsync(upload.Id));
        Assert.True(blocked, "Finalization must wait on the order row lock held by the transition.");
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
        var blocked = await WaitUntilBlockedByAsync(cancellation.ProcessID, finalization);
        await transaction.CommitAsync();

        // The key assertions: the cancelled order yields 409 ORDER_STATE_NOT_ALLOWED and no proof
        // row exists. The blocked check only confirms the interleaving under test really happened.
        var conflict = await Assert.ThrowsAsync<ProofConflictException>(() => finalization);
        Assert.Equal("ORDER_STATE_NOT_ALLOWED", conflict.Code);
        Assert.Equal(before, await CountProofsAsync());
        Assert.Equal("READY", await ReadSessionStatusAsync(upload.Id));
        Assert.True(blocked, "Finalization must wait on the order row lock held by the cancellation.");
    }

    [Fact]
    public async Task Finalization_records_the_proof_after_a_status_change_it_waited_for()
    {
        await ReplaceAssignmentAsync(OwnDriverId, "OWN");
        await SetOrderStatusAsync("DELIVERING");
        var storage = new InMemoryProofObjectStorage();
        await using var provider = BuildProvider(storage);
        var upload = await CreateSessionAsync(provider, OwnDriverUserId, "DELIVERY_PHOTO");
        await MarkSessionReadyAsync(upload.Id);
        storage.PromoteForTest(upload.Id);

        // A transition into DELIVERING holds the order FOR UPDATE and records its status change
        // ahead of this replica's clock (another replica's clock, or a time ORD-002 already moved
        // past an incident). The proof commits after it, so it must carry the later created_at,
        // or ORD-002-ATTEMPT-BOUNDARY would drop it from the attempt that change started.
        await using var transition = new NpgsqlConnection(postgres.AdminConnectionString);
        await transition.OpenAsync();
        await using var transaction = await transition.BeginTransactionAsync();
        await using (var lockOrder = new NpgsqlCommand(
                         """
                         SELECT status FROM orders.orders WHERE id=@order FOR UPDATE;
                         UPDATE orders.orders SET version=version+1 WHERE id=@order;
                         """,
                         transition,
                         transaction))
        {
            lockOrder.Parameters.AddWithValue("order", OrderId);
            await lockOrder.ExecuteNonQueryAsync();
        }

        DateTimeOffset statusChangeAt;
        await using (var change = new NpgsqlCommand(
                         """
                         INSERT INTO orders.order_events(
                           id,order_id,owner_org_id,aggregate_version,event_type,payload,actor_id,occurred_at)
                         SELECT gen_random_uuid(),o.id,o.owner_org_id,
                           (SELECT coalesce(max(e.aggregate_version),0)+1 FROM orders.order_events e WHERE e.order_id=o.id),
                           'ORDER_STATUS_CHANGED',
                           jsonb_build_object('previous_status','FAILED_ATTEMPT','new_status','DELIVERING'),
                           NULL,clock_timestamp()+interval '5 seconds'
                         FROM orders.orders o WHERE o.id=@order
                         RETURNING occurred_at;
                         """,
                         transition,
                         transaction))
        {
            change.Parameters.AddWithValue("order", OrderId);
            statusChangeAt = new DateTimeOffset((DateTime)(await change.ExecuteScalarAsync())!, TimeSpan.Zero);
        }

        var finalization = FinalizeAsync(provider, OwnDriverUserId, upload.Id, "DELIVERY_PHOTO");
        var blocked = await WaitUntilBlockedByAsync(transition.ProcessID, finalization);
        await transaction.CommitAsync();

        var proof = await finalization;
        var createdAt = await ReadProofCreatedAtAsync(proof.Id);
        Assert.True(
            createdAt > statusChangeAt,
            $"custody.proofs.created_at {createdAt:O} must follow the status change {statusChangeAt:O} it waited for.");
        Assert.True(blocked, "Finalization must wait on the order row lock held by the transition.");
    }

    private async Task<DateTimeOffset> ReadProofCreatedAtAsync(Guid proofId)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT created_at FROM custody.proofs WHERE id=@proof;",
            connection);
        command.Parameters.AddWithValue("proof", proofId);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!, TimeSpan.Zero);
    }

    /// <summary>
    /// Waits until a backend is blocked by <paramref name="blockerPid"/>, the test's own
    /// transaction holding FOR UPDATE on the order; while it is open only the finalization's
    /// FOR SHARE can wait on it. Filtering by <c>pg_blocking_pids</c> keeps unrelated lock waits
    /// in the same database from satisfying the check (the statement text is not used: it is
    /// truncated in <c>pg_stat_activity</c>). The outcome assertions (409/403 and the proof count) are what
    /// prove the contract; this only confirms the interleaving.
    /// </summary>
    private async Task<bool> WaitUntilBlockedByAsync(int blockerPid, Task finalization)
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
                    AND a.wait_event_type='Lock'
                    AND @blocker = ANY(pg_blocking_pids(a.pid)))
                """,
                monitor);
            probe.Parameters.AddWithValue("blocker", blockerPid);
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
        string proofType,
        Guid? organization = null)
    {
        var upload = await CreateSessionAsync(provider, actor, proofType, organization);
        await MarkSessionReadyAsync(upload.Id);
        storage.PromoteForTest(upload.Id);
        return await FinalizeAsync(provider, actor, upload.Id, proofType, organization);
    }

    private static async Task<ProofDownloadResult> DownloadAsync(
        ServiceProvider provider,
        Guid actor,
        Guid organization,
        Guid proofId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofDownloadService>()
            .GetInternalDownloadAsync(
                new GetProofDownloadCommand(actor, organization, false, proofId, "pod-driver-type"),
                default);
    }

    private static async Task<ProofUploadSessionResult> CreateSessionAsync(
        ServiceProvider provider,
        Guid actor,
        string proofType,
        Guid? organization = null)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofUploadSessionService>()
            .CreateAsync(
                new CreateProofUploadSessionCommand(
                    actor,
                    organization ?? OrganizationId,
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
        string proofType,
        Guid? organization = null)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofFinalizationService>()
            .FinalizeAsync(
                new FinalizeProofCommand(
                    actor,
                    organization ?? OrganizationId,
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

    private Task SeedExternalDriverAsync() =>
        SeedDriverAsync(ExternalDriverUserId, ExternalDriverId, OrganizationId, "mock-subject-pod-external-driver");

    private async Task SeedDriverAsync(Guid userId, Guid driverId, Guid organizationId, string subject)
    {
        await ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@user,@subject,'ACTIVE')
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
            ("user", userId),
            ("organization", organizationId),
            ("driver", driverId),
            ("subject", subject));
    }

    private Task ReplaceAssignmentAsync(Guid driverId, string assignmentType, Guid? operatorOrganizationId = null) =>
        ExecuteAdminAsync(
            """
            DELETE FROM dispatch.assignments WHERE order_id=@order;
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (gen_random_uuid(),@order,@organization,@operator,@driver,NULL,
                    @type,'ACCEPTED',0,clock_timestamp(),clock_timestamp());
            """,
            ("order", OrderId),
            ("organization", OrganizationId),
            ("driver", driverId),
            ("type", assignmentType),
            ("operator", Nullable(operatorOrganizationId)));

    private Task SetOrderOperatorAsync(Guid? operatorOrganizationId) =>
        ExecuteAdminAsync(
            "UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;",
            ("operator", Nullable(operatorOrganizationId)),
            ("order", OrderId));

    private static NpgsqlParameter Nullable(Guid? value) => new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)value ?? DBNull.Value };

    private async Task<string> ReadProofObjectKeyAsync(Guid proofId)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT object_key FROM custody.proofs WHERE id=@proof;",
            connection);
        command.Parameters.AddWithValue("proof", proofId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

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
            if (value is NpgsqlParameter typed)
            {
                typed.ParameterName = name;
                command.Parameters.Add(typed);
            }
            else
            {
                command.Parameters.AddWithValue(name, value);
            }
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

        public static string DownloadUrl(string objectKey) => $"http://127.0.0.1:9/{objectKey}?signed=test";

        public Task<string> CreateInternalDownloadUrlAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult(DownloadUrl(objectKey));
    }
}
