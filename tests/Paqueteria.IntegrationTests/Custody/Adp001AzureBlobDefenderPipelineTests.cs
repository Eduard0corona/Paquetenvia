using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Web;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Custody;

/// <summary>
/// ADP-001-POD-BLOB-DEFENDER end to end against real PostgreSQL: the API issues the Blob upload
/// grant, the "client" uploads with the returned headers, Defender's verdict tag is simulated and the
/// real Worker processor (worker login, RLS) runs. An infected upload is rejected and stays in
/// quarantine; an unscanned upload is left untouched in quarantine; only a clean verdict promotes.
/// The Blob service itself is an in-memory gateway (no Azure access).
/// </summary>
[Trait("Category", "SecureProofUpload")]
public sealed class Adp001AzureBlobDefenderPipelineTests :
    IClassFixture<PostgreSqlSecurityWebApplicationFactory>
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OwnDriverUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");
    private static readonly Guid OwnDriverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd11");
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private readonly PostgreSqlSecurityWebApplicationFactory postgres;

    public Adp001AzureBlobDefenderPipelineTests(PostgreSqlSecurityWebApplicationFactory postgres)
    {
        this.postgres = postgres;
    }

    [Fact]
    public async Task Infected_upload_is_rejected_and_stays_quarantined()
    {
        var (gateway, api, worker) = await PrepareAsync();
        await using (api)
        await using (worker)
        {
            var upload = await CreateSessionAsync(api, $"adp001-infected-{Guid.NewGuid():N}");
            gateway.ClientPut(upload, Png, DateTimeOffset.UtcNow);
            gateway.SetVerdict(upload.ObjectKey, "Malicious", DateTimeOffset.UtcNow.AddSeconds(2));

            await ProcessAsync(worker);

            Assert.Equal("REJECTED", await ReadSessionStatusAsync(upload.Id));
            Assert.Equal(1L, await CountRejectionAuditsAsync(upload.Id, DefenderForStorageThreatScanner.ThreatDetectedCode));
            Assert.Contains(upload.ObjectKey, gateway.Names);
            Assert.DoesNotContain(ProofObjectKeys.Final(OrganizationId, OrderId, upload.Id), gateway.Names);
            await Assert.ThrowsAsync<ProofConflictException>(() => FinalizeAsync(api, upload.Id));

            // A later pass never promotes or deletes it either.
            await ProcessAsync(worker);
            Assert.Contains(upload.ObjectKey, gateway.Names);
            Assert.DoesNotContain(ProofObjectKeys.Final(OrganizationId, OrderId, upload.Id), gateway.Names);
        }
    }

    [Fact]
    public async Task Unscanned_upload_stays_quarantined_and_the_session_is_untouched()
    {
        var (gateway, api, worker) = await PrepareAsync();
        await using (api)
        await using (worker)
        {
            var upload = await CreateSessionAsync(api, $"adp001-unscanned-{Guid.NewGuid():N}");
            gateway.ClientPut(upload, Png, DateTimeOffset.UtcNow);

            await ProcessAsync(worker);

            Assert.Equal("CREATED", await ReadSessionStatusAsync(upload.Id));
            Assert.Contains(upload.ObjectKey, gateway.Names);
            Assert.DoesNotContain(ProofObjectKeys.Final(OrganizationId, OrderId, upload.Id), gateway.Names);

            // A verdict older than the upload (for example of a replaced blob) is still no verdict.
            gateway.SetVerdict(upload.ObjectKey, "No threats found", DateTimeOffset.UtcNow.AddMinutes(-10));
            await ProcessAsync(worker);
            Assert.Equal("CREATED", await ReadSessionStatusAsync(upload.Id));
            Assert.DoesNotContain(ProofObjectKeys.Final(OrganizationId, OrderId, upload.Id), gateway.Names);
        }
    }

    [Fact]
    public async Task Clean_verdict_promotes_and_the_replay_returns_the_same_blob_grant()
    {
        var (gateway, api, worker) = await PrepareAsync();
        await using (api)
        await using (worker)
        {
            var key = $"adp001-clean-{Guid.NewGuid():N}";
            var upload = await CreateSessionAsync(api, key);
            var query = HttpUtility.ParseQueryString(new Uri(upload.UploadUrl).Query);
            Assert.Equal("cw", query["sp"]);
            Assert.Equal("b", query["sr"]);
            Assert.Equal("https", query["spr"]);
            var replay = await CreateSessionAsync(api, key);
            Assert.Equal(upload with { RequiredHeaders = replay.RequiredHeaders }, replay);
            Assert.Equal(
                upload.RequiredHeaders.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase),
                replay.RequiredHeaders.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase));

            gateway.ClientPut(upload, Png, DateTimeOffset.UtcNow);
            gateway.SetVerdict(upload.ObjectKey, "No threats found", DateTimeOffset.UtcNow.AddSeconds(2));
            await ProcessAsync(worker);

            Assert.Equal("READY", await ReadSessionStatusAsync(upload.Id));
            Assert.DoesNotContain(upload.ObjectKey, gateway.Names);
            Assert.Contains(ProofObjectKeys.Final(OrganizationId, OrderId, upload.Id), gateway.Names);
            var proof = await FinalizeAsync(api, upload.Id);
            Assert.Equal("DELIVERY_PHOTO", proof.ProofType);
        }
    }

    [Fact]
    public async Task Unavailable_storage_signing_fails_closed_before_the_transaction_with_no_effects()
    {
        var (gateway, api, worker) = await PrepareAsync();
        await using (api)
        await using (worker)
        {
            gateway.DelegationKeyFails = true;
            var before = await CountSessionArtifactsAsync();

            await Assert.ThrowsAsync<ProofStorageUnavailableException>(() =>
                CreateSessionAsync(api, $"adp001-signing-down-{Guid.NewGuid():N}"));

            Assert.Equal(before, await CountSessionArtifactsAsync());
            Assert.Empty(gateway.Names);
        }
    }

    private async Task<long> CountSessionArtifactsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM custody.proof_upload_sessions WHERE order_id=@order)
                 + (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@organization)
                 + (SELECT count(*) FROM platform.audit_logs WHERE org_id=@organization AND entity_type='proof_upload_session');
            """,
            connection);
        command.Parameters.AddWithValue("order", OrderId);
        command.Parameters.AddWithValue("organization", OrganizationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(InMemoryBlobGateway Gateway, ServiceProvider Api, ServiceProvider Worker)> PrepareAsync()
    {
        await ExecuteAdminAsync(
            """
            DELETE FROM dispatch.assignments WHERE order_id=@order;
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (gen_random_uuid(),@order,@organization,NULL,@driver,NULL,
                    'OWN','ACCEPTED',0,clock_timestamp(),clock_timestamp());
            UPDATE orders.orders SET status='DELIVERING' WHERE id=@order;
            """,
            ("order", OrderId),
            ("organization", OrganizationId),
            ("driver", OwnDriverId));
        var gateway = new InMemoryBlobGateway();
        return (
            gateway,
            BuildProvider(postgres.ApplicationConnectionString, gateway, worker: false),
            BuildProvider(postgres.WorkerConnectionString, gateway, worker: true));
    }

    private static async Task<ProofUploadSessionResult> CreateSessionAsync(ServiceProvider provider, string idempotencyKey)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProofUploadSessionService>().CreateAsync(
            new CreateProofUploadSessionCommand(
                OwnDriverUserId,
                OrganizationId,
                false,
                idempotencyKey,
                OrderId,
                "DELIVERY_PHOTO",
                "image/png",
                Png.Length,
                SHA256.HashData(Png),
                "adp001-pipeline"),
            default);
    }

    private static async Task<ProofResult> FinalizeAsync(ServiceProvider provider, Guid sessionId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProofFinalizationService>().FinalizeAsync(
            new FinalizeProofCommand(
                OwnDriverUserId,
                OrganizationId,
                false,
                $"adp001-finalize-{Guid.NewGuid():N}",
                OrderId,
                sessionId,
                "DELIVERY_PHOTO",
                SHA256.HashData(Png),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                24.8,
                -107.4,
                null,
                "adp001-pipeline"),
            default);
    }

    private static async Task ProcessAsync(ServiceProvider worker)
    {
        await using var scope = worker.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProofValidationProcessor>().ProcessAvailableAsync(default);
    }

    private static ServiceProvider BuildProvider(string connectionString, InMemoryBlobGateway gateway, bool worker)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Paqueteria"] = connectionString,
                ["ProofStorage:Provider"] = "AzureBlob",
                ["ProofStorage:ThreatScanner"] = "DefenderForStorage",
                ["ProofStorage:AzureBlob:ServiceUri"] = "https://paquetenviatest.blob.core.windows.net",
                ["ProofStorage:AzureBlob:ContainerName"] = "proofs",
                ["ProofStorage:UploadUrlLifetimeMinutes"] = "15",
                ["ProofStorage:DownloadUrlLifetimeMinutes"] = "5",
                ["ProofStorage:SessionLifetimeMinutes"] = "30",
                ["ProofStorage:ProcessingIntervalSeconds"] = "1",
                ["ProofStorage:StaleValidationSeconds"] = "60",
                ["ProofStorage:MaximumConcurrency"] = "2",
                ["ProofStorage:MaximumBytes"] = "1048576",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IProofBlobGateway>(gateway);
        services.AddCustodyInfrastructure(configuration, new TestHostEnvironment(), addValidationWorker: worker);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task<string> ReadSessionStatusAsync(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status FROM custody.proof_upload_sessions WHERE id=@session;", connection);
        command.Parameters.AddWithValue("session", sessionId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountRejectionAuditsAsync(Guid sessionId, string code)
    {
        await using var connection = new NpgsqlConnection(postgres.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.audit_logs
            WHERE entity_id=@session AND action='custody.proof_upload_session.rejected'
              AND payload_redacted->>'code'=@code;
            """,
            connection);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("code", code);
        return (long)(await command.ExecuteScalarAsync())!;
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
        public string ApplicationName { get; set; } = nameof(Adp001AzureBlobDefenderPipelineTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>In-memory Blob container with index tags and ETag-conditional copy.</summary>
    private sealed class InMemoryBlobGateway : IProofBlobGateway
    {
        private readonly ConcurrentDictionary<string, (ProofBlob Blob, byte[] Content)> _blobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _tags = new(StringComparer.Ordinal);
        private int _etag;

        public string AccountName => "paquetenviatest";

        public bool DelegationKeyFails { get; set; }

        public IEnumerable<string> Names => _blobs.Keys.ToArray();

        public Uri BlobUri(string name) => new($"https://paquetenviatest.blob.core.windows.net/proofs/{name}");

        public void ClientPut(ProofUploadSessionResult upload, byte[] content, DateTimeOffset at)
        {
            var metadata = upload.RequiredHeaders
                .Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key["x-ms-meta-".Length..], header => header.Value, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("BlockBlob", upload.RequiredHeaders["x-ms-blob-type"]);
            var etag = $"\"0x{Interlocked.Increment(ref _etag):X}\"";
            _blobs[upload.ObjectKey] = (
                new ProofBlob(upload.ObjectKey, content.Length, upload.RequiredHeaders["Content-Type"], etag, at, metadata),
                content);
            _tags.TryRemove(upload.ObjectKey, out _);
        }

        public void SetVerdict(string name, string result, DateTimeOffset scannedAt) =>
            _tags[name] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Malware Scanning scan result"] = result,
                ["Malware Scanning scan time UTC"] = scannedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };

        public Task<ProofBlobContainerState> GetContainerStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProofBlobContainerState(true, true));

        public Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset startsOn, DateTimeOffset expiresOn, CancellationToken cancellationToken) =>
            DelegationKeyFails
                ? Task.FromException<UserDelegationKey>(new InvalidOperationException("Storage is unreachable."))
                : Task.FromResult(BlobsModelFactory.UserDelegationKey(
                "00000000-0000-0000-0000-00000000aaaa",
                "00000000-0000-0000-0000-00000000bbbb",
                startsOn,
                expiresOn,
                "b",
                "2025-01-05",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        public async IAsyncEnumerable<ProofBlob> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var entry in _blobs.Values.Where(entry => entry.Blob.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                yield return entry.Blob;
            }
        }

        public Task<ProofBlob?> GetPropertiesAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(_blobs.TryGetValue(name, out var entry) ? entry.Blob : null);

        public Task<MemoryStream> DownloadAsync(string name, long maximumBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryStream(_blobs[name].Content));

        public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(
                !_blobs.ContainsKey(name) ? null : _tags.TryGetValue(name, out var tags) ? tags : new Dictionary<string, string>());

        public Task CopyAsync(
            string sourceName,
            string sourceETag,
            string destinationName,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (!_blobs.TryGetValue(sourceName, out var source) || source.Blob.ETag != sourceETag)
            {
                throw new ProofObjectChangedException();
            }

            if (_blobs.ContainsKey(destinationName))
            {
                throw new ProofConflictException("FINAL_OBJECT_COLLISION");
            }

            _blobs[destinationName] = (
                source.Blob with
                {
                    Name = destinationName,
                    ETag = $"\"0x{Interlocked.Increment(ref _etag):X}\"",
                    Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
                },
                source.Content);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _blobs.TryRemove(name, out _);
            _tags.TryRemove(name, out _);
            return Task.CompletedTask;
        }

        public Task<bool> CanReadTagsAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
