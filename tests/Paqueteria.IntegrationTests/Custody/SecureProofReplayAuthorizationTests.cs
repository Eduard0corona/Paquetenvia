using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure;
using Custody.Infrastructure.Proofs;
using Custody.Infrastructure.ProofStorage;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Custody;

[Collection(SecureProofUploadCollection.Name)]
[Trait("Category", "SecureProofUpload")]
public sealed class SecureProofReplayAuthorizationTests :
    IClassFixture<PostgreSqlSecurityWebApplicationFactory>
{
    private static readonly Guid OriginalDispatcherId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa14");
    private static readonly Guid OtherDispatcherId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4");
    private static readonly Guid ViewerId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
    private static readonly Guid PlatformAdminMfaId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
    private static readonly Guid PlatformAdminNoMfaId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3");
    private static readonly Guid ActiveDriverUserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");
    private static readonly Guid ActiveDriverId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd11");
    private static readonly Guid SecondaryDriverId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd12");
    private static readonly Guid OrganizationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ForeignOrganizationId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrderId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid AlternateOrderId =
        Guid.Parse("66666666-6666-6666-6666-666666666667");
    private static readonly Guid AlternateQuoteId =
        Guid.Parse("55555555-5555-5555-5555-555555555556");
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private const string UploadBody =
        """{"proof_type":"DELIVERY_PHOTO","content_type":"image/png","size_bytes":8}""";
    private const string DifferentUploadBody =
        """{"proof_type":"DELIVERY_PHOTO","content_type":"image/png","size_bytes":9}""";
    private const string UploadIdempotencyScope =
        "POD-001:CREATE_PROOF_UPLOAD_SESSION";

    private readonly PostgreSqlSecurityWebApplicationFactory postgres;
    private readonly MinioProofStorageFixture minio;

    public SecureProofReplayAuthorizationTests(
        PostgreSqlSecurityWebApplicationFactory postgres,
        MinioProofStorageFixture minio)
    {
        this.postgres = postgres;
        this.minio = minio;
    }

    [Fact]
    public async Task Http_replay_authorizes_current_actor_before_exposing_the_stored_grant()
    {
        await PrepareFixturesAsync();
        await ClearAssignmentsAsync();
        var storage = new CountingProofObjectStorage(minio.Storage);
        await using var application = CreateProofWebApplication(storage);
        using var client = application.CreateClient();
        var initialCounts = await ReadTableCountsAsync();
        var key = $"pod001-http-replay-{Guid.NewGuid():N}";

        using var create = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveDispatcher,
            OrganizationId,
            UploadBody);
        using var created = await client.SendAsync(create);
        var originalBody = await created.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var originalJson = JsonDocument.Parse(originalBody);
        var sessionId = originalJson.RootElement.GetProperty("id").GetGuid();
        var originalSessionSnapshot = await ReadSessionSnapshotAsync(sessionId);

        using var viewer = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveViewer,
            OrganizationId,
            UploadBody);
        using var viewerResponse = await client.SendAsync(viewer);
        await AssertNoGrantAsync(viewerResponse, HttpStatusCode.Forbidden, sessionId);

        var adminKey = $"pod001-http-admin-{Guid.NewGuid():N}";
        using var adminCreate = UploadRequest(
            OrderId,
            adminKey,
            MockIdentityProfiles.ActivePlatformAdminMfa,
            OrganizationId,
            UploadBody);
        using var adminCreated = await client.SendAsync(adminCreate);
        Assert.Equal(HttpStatusCode.Created, adminCreated.StatusCode);
        using var adminJson = JsonDocument.Parse(await adminCreated.Content.ReadAsByteArrayAsync());
        using var adminNoMfa = UploadRequest(
            OrderId,
            adminKey,
            MockIdentityProfiles.ActivePlatformAdminNoMfa,
            OrganizationId,
            UploadBody);
        using var adminNoMfaResponse = await client.SendAsync(adminNoMfa);
        await AssertNoGrantAsync(
            adminNoMfaResponse,
            HttpStatusCode.Forbidden,
            adminJson.RootElement.GetProperty("id").GetGuid());

        using var dispatcherReplay = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveMultiOrganization,
            OrganizationId,
            UploadBody);
        using var dispatcherReplayResponse = await client.SendAsync(dispatcherReplay);
        Assert.Equal(HttpStatusCode.Created, dispatcherReplayResponse.StatusCode);
        AssertEquivalentUploadResponse(
            originalBody,
            await dispatcherReplayResponse.Content.ReadAsByteArrayAsync());

        using var driverWithoutAssignment = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveDriver,
            OrganizationId,
            UploadBody);
        using var driverWithoutAssignmentResponse = await client.SendAsync(driverWithoutAssignment);
        await AssertNoGrantAsync(
            driverWithoutAssignmentResponse,
            HttpStatusCode.Forbidden,
            sessionId);

        await ReplaceAssignmentAsync(
            OrderId,
            ActiveDriverId,
            OrganizationId,
            null,
            "ACCEPTED");
        using var acceptedDriver = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveDriver,
            OrganizationId,
            UploadBody);
        using var acceptedDriverResponse = await client.SendAsync(acceptedDriver);
        Assert.Equal(HttpStatusCode.Created, acceptedDriverResponse.StatusCode);
        AssertEquivalentUploadResponse(
            originalBody,
            await acceptedDriverResponse.Content.ReadAsByteArrayAsync());

        await SetCurrentAssignmentStatusAsync("ACTIVE");
        using var activeDriver = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveDriver,
            OrganizationId,
            UploadBody);
        using var activeDriverResponse = await client.SendAsync(activeDriver);
        Assert.Equal(HttpStatusCode.Created, activeDriverResponse.StatusCode);
        AssertEquivalentUploadResponse(
            originalBody,
            await activeDriverResponse.Content.ReadAsByteArrayAsync());

        await SetCurrentAssignmentStatusAsync("CANCELLED");
        using var cancelledDriver = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveDriver,
            OrganizationId,
            UploadBody);
        using var cancelledDriverResponse = await client.SendAsync(cancelledDriver);
        await AssertNoGrantAsync(
            cancelledDriverResponse,
            HttpStatusCode.Forbidden,
            sessionId);

        using var unauthorizedDifferentHash = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveViewer,
            OrganizationId,
            DifferentUploadBody);
        using var unauthorizedDifferentHashResponse =
            await client.SendAsync(unauthorizedDifferentHash);
        await AssertNoGrantAsync(
            unauthorizedDifferentHashResponse,
            HttpStatusCode.Forbidden,
            sessionId);

        using var authorizedDifferentHash = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveMultiOrganization,
            OrganizationId,
            DifferentUploadBody);
        using var authorizedDifferentHashResponse =
            await client.SendAsync(authorizedDifferentHash);
        Assert.Equal(HttpStatusCode.Conflict, authorizedDifferentHashResponse.StatusCode);
        Assert.Contains(
            "IDEMPOTENCY_CONFLICT",
            await authorizedDifferentHashResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var crossTenant = UploadRequest(
            OrderId,
            key,
            MockIdentityProfiles.ActiveMultiOrganization,
            ForeignOrganizationId,
            UploadBody);
        using var crossTenantResponse = await client.SendAsync(crossTenant);
        await AssertNoGrantAsync(crossTenantResponse, HttpStatusCode.NotFound, sessionId);

        var finalCounts = await ReadTableCountsAsync();
        Assert.Equal(2, finalCounts.Sessions - initialCounts.Sessions);
        Assert.Equal(2, finalCounts.IdempotencyKeys - initialCounts.IdempotencyKeys);
        Assert.Equal(2, finalCounts.Audits - initialCounts.Audits);
        Assert.Equal(2, storage.CreateUploadGrantCalls);
        Assert.Equal(
            originalSessionSnapshot,
            await ReadSessionSnapshotAsync(sessionId));

        var grant = GrantFrom(originalJson.RootElement);
        using var put = await PutAsync(grant, Png);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task PostgreSql_and_Minio_replay_has_zero_effects_and_requires_current_assignment()
    {
        await PrepareFixturesAsync();
        await ClearAssignmentsAsync();
        var storage = new CountingProofObjectStorage(minio.Storage);
        await using var provider = BuildProvider(
            postgres.ApplicationConnectionString,
            storage);
        var initialCounts = await ReadTableCountsAsync();
        var key = $"pod001-real-replay-{Guid.NewGuid():N}";
        var originalCommand = UploadCommand(
            OriginalDispatcherId,
            OrganizationId,
            false,
            key);
        ProofUploadSessionResult original;
        await using (var scope = provider.CreateAsyncScope())
        {
            original = await scope.ServiceProvider
                .GetRequiredService<IProofUploadSessionService>()
                .CreateAsync(originalCommand, default);
        }
        var createdCounts = await ReadTableCountsAsync();
        var createdSessionSnapshot = await ReadSessionSnapshotAsync(original.Id);
        Assert.Equal(1, createdCounts.Sessions - initialCounts.Sessions);
        Assert.Equal(1, createdCounts.IdempotencyKeys - initialCounts.IdempotencyKeys);
        Assert.Equal(1, createdCounts.Audits - initialCounts.Audits);

        await AssertForbiddenAsync(provider, originalCommand with { ActorId = ViewerId });
        await AssertForbiddenAsync(provider, originalCommand with
        {
            ActorId = PlatformAdminNoMfaId,
            MfaSatisfied = false,
        });

        var dispatcherReplay = await CreateAsync(
            provider,
            originalCommand with { ActorId = OtherDispatcherId });
        AssertEquivalentUploadResult(original, dispatcherReplay);

        await AssertForbiddenAsync(
            provider,
            originalCommand with { ActorId = ActiveDriverUserId });
        await ReplaceAssignmentAsync(
            OrderId,
            ActiveDriverId,
            OrganizationId,
            null,
            "ACCEPTED");
        AssertEquivalentUploadResult(
            original,
            await CreateAsync(
                provider,
                originalCommand with { ActorId = ActiveDriverUserId }));
        await SetCurrentAssignmentStatusAsync("ACTIVE");
        AssertEquivalentUploadResult(
            original,
            await CreateAsync(
                provider,
                originalCommand with { ActorId = ActiveDriverUserId }));
        await SetCurrentAssignmentStatusAsync("CANCELLED");
        await AssertForbiddenAsync(
            provider,
            originalCommand with { ActorId = ActiveDriverUserId });
        await SetCurrentAssignmentStatusAsync("COMPLETED");
        await AssertForbiddenAsync(
            provider,
            originalCommand with { ActorId = ActiveDriverUserId });

        await ReplaceAssignmentAsync(
            OrderId,
            SecondaryDriverId,
            OrganizationId,
            null,
            "ACTIVE");
        await AssertForbiddenAsync(
            provider,
            originalCommand with { ActorId = ActiveDriverUserId });
        await ReplaceAssignmentAsync(
            AlternateOrderId,
            ActiveDriverId,
            OrganizationId,
            null,
            "ACTIVE");
        await AssertForbiddenAsync(
            provider,
            originalCommand with { ActorId = ActiveDriverUserId });
        await ReplaceAssignmentAsync(
            OrderId,
            ActiveDriverId,
            ForeignOrganizationId,
            null,
            "ACTIVE");
        await AssertForbiddenAsync(
            provider,
            originalCommand with { ActorId = ActiveDriverUserId });

        var unauthorizedConflict = originalCommand with
        {
            ActorId = ViewerId,
            SizeBytes = Png.Length + 1,
        };
        await AssertForbiddenAsync(provider, unauthorizedConflict);
        var conflict = await Assert.ThrowsAsync<ProofConflictException>(() =>
            CreateAsync(
                provider,
                originalCommand with
                {
                    ActorId = OtherDispatcherId,
                    SizeBytes = Png.Length + 1,
                }));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        await Assert.ThrowsAsync<ProofNotFoundException>(() =>
            CreateAsync(
                provider,
                originalCommand with
                {
                    ActorId = OtherDispatcherId,
                    OrganizationId = ForeignOrganizationId,
                }));

        Assert.Equal(createdCounts, await ReadTableCountsAsync());
        Assert.Equal(
            createdSessionSnapshot,
            await ReadSessionSnapshotAsync(original.Id));
        Assert.Equal(1, storage.CreateUploadGrantCalls);
        using var put = await PutAsync(
            new ProofUploadGrant(
                original.ObjectKey,
                original.UploadUrl,
                original.RequiredHeaders,
                original.ExpiresAt),
            Png);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    public static TheoryData<string> CorruptReplayEvidence => new()
    {
        "response-status",
        "resource-id",
        "response-order",
        "response-object-key",
        "missing-session",
        "session-order",
        "session-owner",
        "session-content-type",
        "session-maximum-bytes",
        "empty-url",
        "missing-required-headers",
        "corrupt-required-header",
        "extra-response-property",
    };

    [Theory]
    [MemberData(nameof(CorruptReplayEvidence))]
    public async Task Replay_fails_closed_when_persisted_evidence_is_inconsistent(
        string corruption)
    {
        await PrepareFixturesAsync();
        var storage = new CountingProofObjectStorage(minio.Storage);
        await using var provider = BuildProvider(
            postgres.ApplicationConnectionString,
            storage);
        var command = UploadCommand(
            OriginalDispatcherId,
            OrganizationId,
            false,
            $"pod001-corrupt-{corruption}-{Guid.NewGuid():N}");
        var original = await CreateAsync(provider, command);
        var createdCounts = await ReadTableCountsAsync();
        await CorruptReplayAsync(corruption, original, command.IdempotencyKey);

        var exception = await Assert.ThrowsAsync<ProofConflictException>(() =>
            CreateAsync(provider, command with { ActorId = OtherDispatcherId }));
        Assert.Equal("IDEMPOTENCY_CORRUPT", exception.Code);
        Assert.Equal(1, storage.CreateUploadGrantCalls);
        var finalCounts = await ReadTableCountsAsync();
        Assert.Equal(createdCounts.IdempotencyKeys, finalCounts.IdempotencyKeys);
        Assert.Equal(createdCounts.Audits, finalCounts.Audits);
        Assert.Equal(createdCounts.Proofs, finalCounts.Proofs);
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("UPLOADED")]
    [InlineData("VALIDATING")]
    [InlineData("READY")]
    [InlineData("REJECTED")]
    [InlineData("EXPIRED")]
    [InlineData("CONSUMED")]
    public async Task Replay_preserves_historical_result_for_every_session_state(
        string sessionStatus)
    {
        await PrepareFixturesAsync();
        var storage = new CountingProofObjectStorage(minio.Storage);
        await using var provider = BuildProvider(
            postgres.ApplicationConnectionString,
            storage);
        var command = UploadCommand(
            OriginalDispatcherId,
            OrganizationId,
            false,
            $"pod001-lifecycle-{sessionStatus}-{Guid.NewGuid():N}");
        var original = await CreateAsync(provider, command);
        await SetHistoricalStateAsync(original.Id, sessionStatus, "CLOSED");
        try
        {
            var replay = await CreateAsync(
                provider,
                command with { ActorId = OtherDispatcherId });
            AssertEquivalentUploadResult(original, replay);
            Assert.Equal(1, storage.CreateUploadGrantCalls);
        }
        finally
        {
            await SetHistoricalStateAsync(original.Id, sessionStatus, "DELIVERING");
        }
    }

    [Fact]
    public async Task Finalization_replay_keeps_current_authorization_and_zero_effects()
    {
        await PrepareFixturesAsync();
        await ClearAssignmentsAsync();
        await ClearQuarantineAsync();
        var storage = new CountingProofObjectStorage(minio.Storage);
        await using var apiProvider = BuildProvider(
            postgres.ApplicationConnectionString,
            storage);
        var sha = SHA256.HashData(Png);
        var upload = await CreateAsync(
            apiProvider,
            UploadCommand(
                OriginalDispatcherId,
                OrganizationId,
                false,
                $"pod001-finalization-upload-{Guid.NewGuid():N}",
                sha));
        using (var put = await PutAsync(
                   new ProofUploadGrant(
                       upload.ObjectKey,
                       upload.UploadUrl,
                       upload.RequiredHeaders,
                       upload.ExpiresAt),
                   Png))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        await using (var workerProvider = BuildProvider(
                         postgres.WorkerConnectionString,
                         storage))
        await using (var workerScope = workerProvider.CreateAsyncScope())
        {
            await workerScope.ServiceProvider
                .GetRequiredService<IProofValidationProcessor>()
                .ProcessAvailableAsync(default);
        }

        var finalization = new FinalizeProofCommand(
            OriginalDispatcherId,
            OrganizationId,
            false,
            $"pod001-finalization-replay-{Guid.NewGuid():N}",
            OrderId,
            upload.Id,
            "DELIVERY_PHOTO",
            sha,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null,
            null,
            null,
            "pod001-finalization-replay");
        var proof = await FinalizeAsync(apiProvider, finalization);
        var finalizedCounts = await ReadTableCountsAsync();
        var finalizedSessionSnapshot = await ReadSessionSnapshotAsync(upload.Id);

        await Assert.ThrowsAsync<ProofForbiddenException>(() =>
            FinalizeAsync(apiProvider, finalization with { ActorId = ViewerId }));
        await Assert.ThrowsAsync<ProofForbiddenException>(() =>
            FinalizeAsync(
                apiProvider,
                finalization with
                {
                    ActorId = PlatformAdminNoMfaId,
                    MfaSatisfied = false,
                }));
        var dispatcherReplay = await FinalizeAsync(
            apiProvider,
            finalization with { ActorId = OtherDispatcherId });
        Assert.Equal(proof, dispatcherReplay);
        Assert.Equal(finalizedCounts, await ReadTableCountsAsync());
        Assert.Equal(
            finalizedSessionSnapshot,
            await ReadSessionSnapshotAsync(upload.Id));
        Assert.Equal(1, storage.CreateUploadGrantCalls);
    }

    private WebApplicationFactory<Program> CreateProofWebApplication(
        CountingProofObjectStorage storage) =>
        postgres.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(ProofSettings()));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProofObjectStorage>();
                services.AddSingleton<IProofObjectStorage>(storage);
            });
        });

    private ServiceProvider BuildProvider(
        string connectionString,
        CountingProofObjectStorage storage)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ProofSettings(connectionString))
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

    private Dictionary<string, string?> ProofSettings(string? connectionString = null)
    {
        var settings = new Dictionary<string, string?>
        {
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
        if (connectionString is not null)
        {
            settings["ConnectionStrings:Paqueteria"] = connectionString;
        }

        return settings;
    }

    private async Task PrepareFixturesAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (
              'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa14',
              'mock-subject-active-dispatcher',
              'ACTIVE')
            ON CONFLICT (id) DO UPDATE
            SET identity_subject=EXCLUDED.identity_subject,status='ACTIVE';
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            SELECT
              gen_random_uuid(),
              'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa14',
              '11111111-1111-1111-1111-111111111111',
              'DISPATCHER','ACTIVE',true
            WHERE NOT EXISTS (
              SELECT 1
              FROM organizations.organization_memberships
              WHERE user_id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa14'
                AND organization_id='11111111-1111-1111-1111-111111111111');
            UPDATE organizations.organization_memberships
            SET role='DISPATCHER',status='ACTIVE'
            WHERE user_id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4'
              AND organization_id='11111111-1111-1111-1111-111111111111';
            INSERT INTO pricing.quotes(
              id,owner_org_id,client_account_id,city_id,service_area_id,
              origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,rule_ids,
              request_snapshot_redacted,package_snapshot,pii_snapshot_ciphertext,
              pii_key_version,breakdown,input_hash,financial_override,status,expires_at,
              consumed_at,created_at)
            SELECT
              @alternate_quote,owner_org_id,client_account_id,city_id,service_area_id,
              origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,rule_ids,
              request_snapshot_redacted,package_snapshot,pii_snapshot_ciphertext,
              pii_key_version,breakdown,input_hash,financial_override,status,expires_at,
              consumed_at,created_at
            FROM pricing.quotes
            WHERE id='55555555-5555-5555-5555-555555555555'
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,operator_org_id,client_account_id,
              city_id,service_area_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,
              subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,financial_override,cod_expected_cents,version,
              claim_window_ends_at,finalized_at,archived_at,created_at,updated_at)
            SELECT
              @alternate_order,'ORD_pod001replayalternate',@alternate_quote,
              owner_org_id,operator_org_id,client_account_id,city_id,service_area_id,
              origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,
              tax_cents,total_cents,minimum_total_cents_snapshot,currency,
              pricing_policy_version,package_snapshot,financial_override,
              cod_expected_cents,version,claim_window_ends_at,finalized_at,archived_at,
              created_at,updated_at
            FROM orders.orders
            WHERE id=@order
            ON CONFLICT (id) DO NOTHING;
            UPDATE orders.orders
            SET status='DELIVERING'
            WHERE id IN (@order,@alternate_order);
            """,
            connection);
        command.Parameters.AddWithValue("alternate_quote", AlternateQuoteId);
        command.Parameters.AddWithValue("alternate_order", AlternateOrderId);
        command.Parameters.AddWithValue("order", OrderId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetHistoricalStateAsync(
        Guid sessionId,
        string sessionStatus,
        string orderStatus)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE custody.proof_upload_sessions
            SET status=@session_status,updated_at=clock_timestamp()
            WHERE id=@session;
            UPDATE orders.orders
            SET status=@order_status
            WHERE id=@order;
            """,
            connection);
        command.Parameters.AddWithValue("session_status", sessionStatus);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("order_status", orderStatus);
        command.Parameters.AddWithValue("order", OrderId);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private async Task ClearAssignmentsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM dispatch.assignments WHERE order_id IN (@order,@alternate_order);",
            connection);
        command.Parameters.AddWithValue("order", OrderId);
        command.Parameters.AddWithValue("alternate_order", AlternateOrderId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ClearQuarantineAsync()
    {
        await foreach (var descriptor in minio.Storage.ListQuarantineAsync(default))
        {
            await minio.Storage.DeleteQuarantineAsync(
                descriptor.ObjectKey,
                descriptor.VersionId,
                default);
        }
    }

    private async Task ReplaceAssignmentAsync(
        Guid orderId,
        Guid driverId,
        Guid ownerOrganizationId,
        Guid? operatorOrganizationId,
        string status)
    {
        await ClearAssignmentsAsync();
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (
              gen_random_uuid(),@order,@owner,@operator,@driver,NULL,'OWN',@status,0,
              CASE WHEN @status IN ('ACCEPTED','ACTIVE')
                   THEN clock_timestamp() ELSE NULL END,
              clock_timestamp());
            """,
            connection);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("owner", ownerOrganizationId);
        command.Parameters.AddWithValue(
            "operator",
            operatorOrganizationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("driver", driverId);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetCurrentAssignmentStatusAsync(string status)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
            SET status=@status,
                accepted_at=CASE WHEN @status IN ('ACCEPTED','ACTIVE')
                  THEN COALESCE(accepted_at,clock_timestamp()) ELSE NULL END
            WHERE order_id=@order;
            """,
            connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("order", OrderId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task CorruptReplayAsync(
        string corruption,
        ProofUploadSessionResult result,
        string idempotencyKey)
    {
        var sql = corruption switch
        {
            "response-status" => """
                UPDATE platform.idempotency_keys
                SET response_status=200
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "resource-id" => """
                UPDATE platform.idempotency_keys
                SET resource_id=gen_random_uuid()
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "response-order" => """
                UPDATE platform.idempotency_keys
                SET response_body=jsonb_set(
                  response_body,'{orderId}',to_jsonb(@alternate_order::text))
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "response-object-key" => """
                UPDATE platform.idempotency_keys
                SET response_body=jsonb_set(
                  response_body,'{objectKey}',to_jsonb('quarantine/corrupt'::text))
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "missing-session" => """
                DELETE FROM custody.proof_upload_sessions WHERE id=@session;
                """,
            "session-order" => """
                UPDATE custody.proof_upload_sessions
                SET order_id=@alternate_order
                WHERE id=@session;
                """,
            "session-owner" => """
                UPDATE custody.proof_upload_sessions
                SET owner_org_id=@foreign_owner
                WHERE id=@session;
                """,
            "session-content-type" => """
                UPDATE custody.proof_upload_sessions
                SET expected_content_type='image/jpeg'
                WHERE id=@session;
                """,
            "session-maximum-bytes" => """
                UPDATE custody.proof_upload_sessions
                SET maximum_bytes=maximum_bytes+1
                WHERE id=@session;
                """,
            "empty-url" => """
                UPDATE platform.idempotency_keys
                SET response_body=jsonb_set(
                  response_body,'{uploadUrl}',to_jsonb(''::text))
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "missing-required-headers" => """
                UPDATE platform.idempotency_keys
                SET response_body=response_body-'requiredHeaders'
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "corrupt-required-header" => """
                UPDATE platform.idempotency_keys
                SET response_body=jsonb_set(
                  response_body,
                  '{requiredHeaders,Content-Type}',
                  to_jsonb('image/jpeg'::text))
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            "extra-response-property" => """
                UPDATE platform.idempotency_keys
                SET response_body=response_body || '{"unexpected":"corrupt"}'::jsonb
                WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null),
        };
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("owner", OrganizationId);
        command.Parameters.AddWithValue(
            "scope",
            UploadIdempotencyScope);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("session", result.Id);
        command.Parameters.AddWithValue("alternate_order", AlternateOrderId);
        command.Parameters.AddWithValue("foreign_owner", ForeignOrganizationId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<TableCounts> ReadTableCountsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
              (SELECT count(*) FROM custody.proof_upload_sessions),
              (SELECT count(*) FROM platform.idempotency_keys),
              (SELECT count(*) FROM platform.audit_logs WHERE action LIKE 'custody.%'),
              (SELECT count(*) FROM custody.proofs);
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private async Task<string> ReadSessionSnapshotAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT to_jsonb(session_row)::text
            FROM custody.proof_upload_sessions session_row
            WHERE id=@session;
            """,
            connection);
        command.Parameters.AddWithValue("session", sessionId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static CreateProofUploadSessionCommand UploadCommand(
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        string key,
        byte[]? sha256 = null) =>
        new(
            actorId,
            organizationId,
            mfaSatisfied,
            key,
            OrderId,
            "DELIVERY_PHOTO",
            "image/png",
            Png.Length,
            sha256,
            "pod001-replay-authorization");

    private static async Task<ProofUploadSessionResult> CreateAsync(
        IServiceProvider provider,
        CreateProofUploadSessionCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofUploadSessionService>()
            .CreateAsync(command, default);
    }

    private static async Task<ProofResult> FinalizeAsync(
        IServiceProvider provider,
        FinalizeProofCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IProofFinalizationService>()
            .FinalizeAsync(command, default);
    }

    private static async Task AssertForbiddenAsync(
        IServiceProvider provider,
        CreateProofUploadSessionCommand command) =>
        await Assert.ThrowsAsync<ProofForbiddenException>(() =>
            CreateAsync(provider, command));

    private static HttpRequestMessage UploadRequest(
        Guid orderId,
        string key,
        string profile,
        Guid organizationId,
        string body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/orders/{orderId:D}/proof-upload-sessions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", organizationId.ToString("D"));
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task AssertNoGrantAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        Guid sessionId)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var value in new[]
                 {
                     "upload_url",
                     "object_key",
                     "required_headers",
                     "expires_at",
                     sessionId.ToString("D"),
                     "IDEMPOTENCY_CONFLICT",
                     "IDEMPOTENCY_IN_PROGRESS",
                 })
        {
            Assert.DoesNotContain(value, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ProofUploadGrant GrantFrom(JsonElement response)
    {
        var headers = response.GetProperty("required_headers")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString()!,
                StringComparer.OrdinalIgnoreCase);
        return new(
            response.GetProperty("object_key").GetString()!,
            response.GetProperty("upload_url").GetString()!,
            headers,
            response.GetProperty("expires_at").GetDateTimeOffset());
    }

    private static void AssertEquivalentUploadResponse(
        byte[] expectedBody,
        byte[] actualBody)
    {
        using var expected = JsonDocument.Parse(expectedBody);
        using var actual = JsonDocument.Parse(actualBody);
        foreach (var property in new[]
                 {
                     "id",
                     "status",
                     "upload_url",
                     "object_key",
                     "expires_at",
                 })
        {
            Assert.Equal(
                expected.RootElement.GetProperty(property).ToString(),
                actual.RootElement.GetProperty(property).ToString());
        }

        var expectedHeaders = expected.RootElement.GetProperty("required_headers")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString()!,
                StringComparer.OrdinalIgnoreCase);
        var actualHeaders = actual.RootElement.GetProperty("required_headers")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString()!,
                StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            expectedHeaders.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase),
            actualHeaders.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase));
    }

    private static void AssertEquivalentUploadResult(
        ProofUploadSessionResult expected,
        ProofUploadSessionResult actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.OrderId, actual.OrderId);
        Assert.Equal(expected.ObjectKey, actual.ObjectKey);
        Assert.Equal(expected.UploadUrl, actual.UploadUrl);
        Assert.Equal(expected.ExpiresAt, actual.ExpiresAt);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(
            expected.RequiredHeaders.OrderBy(
                header => header.Key,
                StringComparer.OrdinalIgnoreCase),
            actual.RequiredHeaders.OrderBy(
                header => header.Key,
                StringComparer.OrdinalIgnoreCase));
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
                    new MediaTypeHeaderValue(header.Value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return await client.SendAsync(request);
    }

    private sealed class CountingProofObjectStorage(IProofObjectStorage inner)
        : IProofObjectStorage
    {
        private int createUploadGrantCalls;

        internal int CreateUploadGrantCalls => Volatile.Read(ref createUploadGrantCalls);
        public bool IsEnabled => inner.IsEnabled;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
            inner.CheckHealthAsync(cancellationToken);

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
            Interlocked.Increment(ref createUploadGrantCalls);
            return inner.CreateUploadGrantAsync(
                ownerOrganizationId,
                orderId,
                sessionId,
                requestedBy,
                proofType,
                contentType,
                sizeBytes,
                sha256,
                expiresAt,
                cancellationToken);
        }

        public IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(
            CancellationToken cancellationToken) =>
            inner.ListQuarantineAsync(cancellationToken);

        public Task<Stream> OpenReadAsync(
            string objectKey,
            string? versionId,
            CancellationToken cancellationToken) =>
            inner.OpenReadAsync(objectKey, versionId, cancellationToken);

        public Task PromoteAsync(
            ValidatedProofObject proof,
            CancellationToken cancellationToken) =>
            inner.PromoteAsync(proof, cancellationToken);

        public Task<ProofObjectDescriptor?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken) =>
            inner.HeadAsync(objectKey, cancellationToken);

        public Task DeleteQuarantineAsync(
            string objectKey,
            string? versionId,
            CancellationToken cancellationToken) =>
            inner.DeleteQuarantineAsync(objectKey, versionId, cancellationToken);

        public Task<string> CreateInternalDownloadUrlAsync(
            string objectKey,
            CancellationToken cancellationToken) =>
            inner.CreateInternalDownloadUrlAsync(objectKey, cancellationToken);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(SecureProofReplayAuthorizationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed record TableCounts(
        long Sessions,
        long IdempotencyKeys,
        long Audits,
        long Proofs);
}
