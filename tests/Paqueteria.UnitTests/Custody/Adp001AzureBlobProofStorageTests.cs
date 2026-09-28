using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
using Microsoft.Extensions.Options;

namespace Paqueteria.UnitTests.Custody;

/// <summary>
/// ADP-001-POD-BLOB-DEFENDER without Azure: the Blob adapter's SAS scope, header shape, metadata
/// mapping and conditional promotion, and the Defender verdict rules, over an in-memory gateway.
/// </summary>
public sealed class Adp001AzureBlobProofStorageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Order = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Session = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Actor = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");

    [Fact]
    public async Task Upload_grant_is_a_create_write_https_user_delegation_sas_for_exactly_one_quarantine_blob()
    {
        var gateway = new InMemoryProofBlobGateway();
        var storage = CreateStorage(gateway);
        await storage.PrepareUploadGrantAsync(default);
        var expiresAt = Now.AddMinutes(15);

        var grant = await storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, SHA256.HashData([1]), expiresAt, default);

        var uri = new Uri(grant.Url);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal($"/proofs/{ProofObjectKeys.Quarantine(Owner, Order, Session)}", Uri.UnescapeDataString(uri.AbsolutePath));
        Assert.Equal(ProofObjectKeys.Quarantine(Owner, Order, Session), grant.ObjectKey);
        Assert.Equal("cw", query["sp"]);
        Assert.Equal("b", query["sr"]);
        Assert.Equal("https", query["spr"]);
        Assert.Equal(expiresAt, DateTimeOffset.Parse(query["se"]!));
        Assert.False(string.IsNullOrEmpty(query["skoid"]), "A user delegation SAS carries the signer object id.");
        Assert.False(string.IsNullOrEmpty(query["sig"]));
        Assert.DoesNotContain("t", query["sp"]!, StringComparison.Ordinal);
        Assert.Equal(1, gateway.DelegationKeyRequests);
    }

    [Fact]
    public async Task Required_headers_are_accepted_by_the_pwa_and_name_blob_metadata_without_hyphens()
    {
        var storage = CreateStorage(new InMemoryProofBlobGateway());
        await storage.PrepareUploadGrantAsync(default);
        var grant = await storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, SHA256.HashData([1]), Now.AddMinutes(10), default);

        // apps/web driver-sync-api readRequiredHeaders: 1-20 headers named [a-z0-9-]{1,80}.
        Assert.InRange(grant.RequiredHeaders.Count, 1, 20);
        Assert.All(grant.RequiredHeaders.Keys, name => Assert.Matches("^[a-z0-9-]{1,80}$", name.ToLowerInvariant()));
        Assert.Equal("BlockBlob", grant.RequiredHeaders["x-ms-blob-type"]);
        Assert.Equal("image/png", grant.RequiredHeaders["Content-Type"]);
        Assert.Equal(Session.ToString("D"), grant.RequiredHeaders["x-ms-meta-sessionid"]);
        Assert.All(
            grant.RequiredHeaders.Keys.Where(name => name.StartsWith("x-ms-meta-", StringComparison.Ordinal)),
            name => Assert.Matches("^[a-z][a-z0-9]*$", name["x-ms-meta-".Length..]));
    }

    [Fact]
    public async Task A_grant_longer_than_the_upload_lifetime_is_refused_and_the_worker_never_signs()
    {
        var gateway = new InMemoryProofBlobGateway();
        await Assert.ThrowsAsync<ProofStorageUnavailableException>(() => CreateStorage(gateway).CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, null, Now.AddMinutes(16), default));

        var worker = CreateStorage(gateway, signsUrls: false);
        await Assert.ThrowsAsync<ProofStorageUnavailableException>(() => worker.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, null, Now.AddMinutes(5), default));
        await Assert.ThrowsAsync<ProofStorageUnavailableException>(() => worker.CreateInternalDownloadUrlAsync("proofs/x", default));
        Assert.Equal(0, gateway.DelegationKeyRequests);
    }

    [Fact]
    public async Task The_grant_never_calls_storage_and_fails_closed_without_a_prepared_key()
    {
        // ADP-001 review: the grant runs inside the tenant transaction, so only
        // PrepareUploadGrantAsync (called before it) may reach the storage service.
        var gateway = new InMemoryProofBlobGateway();
        var storage = CreateStorage(gateway);

        await Assert.ThrowsAsync<ProofStorageUnavailableException>(() => storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, null, Now.AddMinutes(10), default));
        Assert.Equal(0, gateway.DelegationKeyRequests);

        await storage.PrepareUploadGrantAsync(default);
        Assert.Equal(1, gateway.DelegationKeyRequests);
        gateway.DelegationKeyFails = true;
        _ = await storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, null, Now.AddMinutes(10), default);
        Assert.Equal(1, gateway.DelegationKeyRequests);

        await Assert.ThrowsAsync<ProofStorageUnavailableException>(() =>
            CreateStorage(gateway, signsUrls: false).PrepareUploadGrantAsync(default));
    }

    [Fact]
    public async Task Internal_download_is_a_short_read_only_sas()
    {
        var storage = CreateStorage(new InMemoryProofBlobGateway());
        var url = await storage.CreateInternalDownloadUrlAsync(ProofObjectKeys.Final(Owner, Order, Session), default);
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.Equal("r", query["sp"]);
        Assert.Equal("b", query["sr"]);
        Assert.Equal("https", query["spr"]);
        Assert.Equal(Now.AddMinutes(5), DateTimeOffset.Parse(query["se"]!));
    }

    [Fact]
    public async Task Quarantine_listing_maps_blob_metadata_back_to_the_pod001_names()
    {
        var gateway = new InMemoryProofBlobGateway();
        var storage = CreateStorage(gateway);
        await storage.PrepareUploadGrantAsync(default);
        var grant = await storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, SHA256.HashData([1]), Now.AddMinutes(10), default);
        gateway.ClientPut(grant, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], Now);

        var listed = await ToListAsync(storage.ListQuarantineAsync(default));

        var descriptor = Assert.Single(listed);
        Assert.Equal(grant.ObjectKey, descriptor.ObjectKey);
        Assert.Equal(Session.ToString("D"), descriptor.Metadata[S3CompatibleProofObjectStorage.SessionIdMetadata]);
        Assert.Equal(Owner.ToString("D"), descriptor.Metadata[S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata]);
        Assert.Equal("DELIVERY_PHOTO", descriptor.Metadata[S3CompatibleProofObjectStorage.ProofTypeMetadata]);
        Assert.Equal("8", descriptor.Metadata[S3CompatibleProofObjectStorage.SizeBytesMetadata]);
        Assert.Equal(Now, descriptor.LastModified);
    }

    [Fact]
    public async Task Promotion_is_conditional_on_the_validated_etag_and_rebuilds_metadata()
    {
        var gateway = new InMemoryProofBlobGateway();
        var storage = CreateStorage(gateway);
        await storage.PrepareUploadGrantAsync(default);
        var grant = await storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, null, Now.AddMinutes(10), default);
        var etag = gateway.ClientPut(grant, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], Now);
        var validated = new ValidatedProofObject(
            Session, Order, Owner, "DELIVERY_PHOTO", grant.ObjectKey, ProofObjectKeys.Final(Owner, Order, Session),
            8, "image/png", SHA256.HashData([9]), etag, null);

        gateway.ClientPut(grant, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0b], Now.AddSeconds(1));
        await Assert.ThrowsAsync<ProofObjectChangedException>(() => storage.PromoteAsync(validated, default));
        Assert.Null(await storage.HeadAsync(validated.FinalObjectKey, default));

        var current = (await storage.HeadAsync(grant.ObjectKey, default))!;
        await storage.PromoteAsync(validated with { ETag = current.ETag }, default);
        var final = (await storage.HeadAsync(validated.FinalObjectKey, default))!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData([9])).ToLowerInvariant(), final.Metadata[S3CompatibleProofObjectStorage.Sha256Metadata]);
        Assert.False(final.Metadata.ContainsKey(S3CompatibleProofObjectStorage.RequestedByMetadata));

        // Idempotent for the same session; a different object at the final key is a collision.
        await storage.PromoteAsync(validated with { ETag = current.ETag }, default);
        await Assert.ThrowsAsync<ProofConflictException>(() =>
            storage.PromoteAsync(validated with { ETag = current.ETag, Sha256 = SHA256.HashData([7]) }, default));
    }

    [Fact]
    public async Task Only_quarantine_objects_can_be_deleted_by_the_quarantine_cleanup()
    {
        var storage = CreateStorage(new InMemoryProofBlobGateway());
        await Assert.ThrowsAsync<ProofConflictException>(() =>
            storage.DeleteQuarantineAsync(ProofObjectKeys.Final(Owner, Order, Session), null, default));
    }

    [Fact]
    public async Task Readiness_fails_closed_for_a_public_or_missing_container()
    {
        Assert.True(await CreateStorage(new InMemoryProofBlobGateway()).CheckHealthAsync(default));
        Assert.False(await CreateStorage(new InMemoryProofBlobGateway { ContainerIsPrivate = false }).CheckHealthAsync(default));
        Assert.False(await CreateStorage(new InMemoryProofBlobGateway { ContainerExists = false }).CheckHealthAsync(default));
        Assert.False(await CreateStorage(new InMemoryProofBlobGateway { DelegationKeyFails = true }).CheckHealthAsync(default));
        Assert.True(await CreateStorage(new InMemoryProofBlobGateway { DelegationKeyFails = true }, signsUrls: false).CheckHealthAsync(default));
    }

    [Fact]
    public async Task Defender_verdict_rules_fail_closed()
    {
        var gateway = new InMemoryProofBlobGateway();
        var scanner = new DefenderForStorageThreatScanner(gateway, Options.Create(DefaultOptions()));
        var storage = CreateStorage(gateway);
        await storage.PrepareUploadGrantAsync(default);
        var grant = await storage.CreateUploadGrantAsync(
            Owner, Order, Session, Actor, ProofType.DeliveryPhoto, "image/png", 8, null, Now.AddMinutes(10), default);
        gateway.ClientPut(grant, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], Now);
        var descriptor = (await storage.HeadAsync(grant.ObjectKey, default))!;

        // Not scanned yet: pending, never safe.
        Assert.True(await scanner.IsVerdictPendingAsync(descriptor, default));
        Assert.True((await scanner.ScanAsync(descriptor, Stream.Null, default)).IsPending);
        Assert.False((await scanner.ScanAsync(Stream.Null, default)).IsSafe);

        // A verdict recorded before the last modification belongs to earlier content.
        gateway.SetVerdict(grant.ObjectKey, "No threats found", Now.AddMinutes(-1));
        Assert.True((await scanner.ScanAsync(descriptor, Stream.Null, default)).IsPending);

        gateway.SetVerdict(grant.ObjectKey, "No threats found", Now.AddSeconds(3));
        Assert.True((await scanner.ScanAsync(descriptor, Stream.Null, default)).IsSafe);
        Assert.False(await scanner.IsVerdictPendingAsync(descriptor, default));

        gateway.SetVerdict(grant.ObjectKey, "Malicious", Now.AddSeconds(3));
        var malicious = await scanner.ScanAsync(descriptor, Stream.Null, default);
        Assert.False(malicious.IsSafe);
        Assert.False(malicious.IsPending);
        Assert.Equal(DefenderForStorageThreatScanner.ThreatDetectedCode, malicious.Code);

        gateway.SetVerdict(grant.ObjectKey, "Not scanned", Now.AddSeconds(3));
        Assert.Equal(DefenderForStorageThreatScanner.ScanFailedCode, (await scanner.ScanAsync(descriptor, Stream.Null, default)).Code);

        // Tag keys are case-sensitive: a differently cased key is no verdict at all.
        gateway.Tags[grant.ObjectKey] = new Dictionary<string, string>
        {
            ["malware scanning scan result"] = "No threats found",
            ["Malware Scanning scan time UTC"] = Now.AddSeconds(3).ToString("u"),
        };
        Assert.True((await scanner.ScanAsync(descriptor, Stream.Null, default)).IsPending);

        Assert.True(await scanner.CheckHealthAsync(default));
        gateway.TagReadAllowed = false;
        Assert.False(await scanner.CheckHealthAsync(default));
    }

    [Fact]
    public void Configuration_selects_azure_adapters_and_validates_them_on_start()
    {
        using (var provider = Build(AzureSettings(), environment: "Production", worker: true))
        {
            Assert.IsType<AzureBlobProofObjectStorage>(provider.GetRequiredService<IProofObjectStorage>());
            Assert.IsType<DefenderForStorageThreatScanner>(provider.GetRequiredService<IProofThreatScanner>());
        }

        foreach (var (key, value) in new[]
                 {
                     ("ProofStorage:AzureBlob:ServiceUri", "http://account.blob.core.windows.net"),
                     ("ProofStorage:AzureBlob:ServiceUri", "https://account.blob.core.windows.net/proofs"),
                     ("ProofStorage:AzureBlob:ServiceUri", "https://account.blob.core.windows.net/?sv=2024"),
                     ("ProofStorage:AzureBlob:ContainerName", "Proofs"),
                     ("ProofStorage:AzureBlob:UserDelegationKeyLifetimeMinutes", "20"),
                     ("ProofStorage:DefenderForStorage:MaliciousValue", "No threats found"),
                     ("ProofStorage:DefenderForStorage:ScanResultTagName", ""),
                     ("ProofStorage:Provider", "S3Compatible"),
                 })
        {
            var settings = AzureSettings();
            settings[key] = value;
            if (value == "S3Compatible")
            {
                // A complete S3 configuration, so the only fault is Defender without Azure Blob.
                settings["ProofStorage:ServiceUrl"] = "https://s3.example.test";
                settings["ProofStorage:PublicPresignUrl"] = "https://s3.example.test";
                settings["ProofStorage:Bucket"] = "proofs";
            }

            using var provider = Build(settings, environment: "Production", worker: true);
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProofStorageOptions>>().Value);
        }
    }

    [Fact]
    public void The_local_minio_path_stays_the_default_shape()
    {
        var options = new ProofStorageOptions();
        Assert.Equal(ProofStorageProvider.Disabled, options.Provider);
        Assert.Equal(ProofThreatScannerProvider.Disabled, options.ThreatScanner);
        using var provider = Build(new Dictionary<string, string?>(), environment: "Testing", worker: false);
        Assert.IsType<DisabledProofObjectStorage>(provider.GetRequiredService<IProofObjectStorage>());
        Assert.IsType<DisabledProofThreatScanner>(provider.GetRequiredService<IProofThreatScanner>());
    }

    internal static Dictionary<string, string?> AzureSettings() => new()
    {
        ["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=not-used;Username=not-used;Password=not-used",
        ["ProofStorage:Provider"] = "AzureBlob",
        ["ProofStorage:ThreatScanner"] = "DefenderForStorage",
        ["ProofStorage:AzureBlob:ServiceUri"] = "https://paquetenviatest.blob.core.windows.net",
        ["ProofStorage:AzureBlob:ContainerName"] = "proofs",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings, string environment, bool worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IProofBlobGateway>(new InMemoryProofBlobGateway());
        services.AddCustodyInfrastructure(configuration, new FixedEnvironment(environment), addValidationWorker: worker);
        return services.BuildServiceProvider();
    }

    private static ProofStorageOptions DefaultOptions() => new()
    {
        Provider = ProofStorageProvider.AzureBlob,
        ThreatScanner = ProofThreatScannerProvider.DefenderForStorage,
        AzureBlob = new AzureBlobProofStorageOptions { ServiceUri = "https://paquetenviatest.blob.core.windows.net" },
    };

    private static AzureBlobProofObjectStorage CreateStorage(InMemoryProofBlobGateway gateway, bool signsUrls = true) =>
        new(gateway, Options.Create(DefaultOptions()), new FixedTime(Now), signsUrls);

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Adp001";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>An in-memory Blob container with index tags, conditional copy and a fake delegation key.</summary>
public sealed partial class InMemoryProofBlobGateway : IProofBlobGateway
{
    private readonly ConcurrentDictionary<string, (ProofBlob Blob, byte[] Content)> _blobs = new(StringComparer.Ordinal);
    private int _etag;

    public bool ContainerExists { get; set; } = true;
    public bool ContainerIsPrivate { get; set; } = true;
    public bool DelegationKeyFails { get; set; }
    public bool TagReadAllowed { get; set; } = true;
    public int DelegationKeyRequests { get; private set; }
    public ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tags { get; } = new(StringComparer.Ordinal);

    public string AccountName => "paquetenviatest";

    public IEnumerable<string> Names => _blobs.Keys;

    public Uri BlobUri(string name) => new($"https://paquetenviatest.blob.core.windows.net/proofs/{name}");

    /// <summary>Simulates the client PUT with the grant's headers (Put Blob replaces the blob and its tags).</summary>
    public string ClientPut(ProofUploadGrant grant, byte[] content, DateTimeOffset at)
    {
        var metadata = grant.RequiredHeaders
            .Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key["x-ms-meta-".Length..], header => header.Value, StringComparer.OrdinalIgnoreCase);
        var etag = $"\"0x{Interlocked.Increment(ref _etag):X}\"";
        _blobs[grant.ObjectKey] = (new ProofBlob(grant.ObjectKey, content.Length, grant.RequiredHeaders["Content-Type"], etag, at, metadata), content);
        Tags.TryRemove(grant.ObjectKey, out _);
        return etag;
    }

    public void SetVerdict(string name, string result, DateTimeOffset scannedAt) =>
        Tags[name] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Malware Scanning scan result"] = result,
            ["Malware Scanning scan time UTC"] = scannedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };

    public Task<ProofBlobContainerState> GetContainerStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProofBlobContainerState(ContainerExists, ContainerIsPrivate));

    public Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset startsOn, DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        if (DelegationKeyFails)
        {
            return Task.FromException<UserDelegationKey>(new InvalidOperationException("Delegation denied."));
        }

        DelegationKeyRequests++;
        return Task.FromResult(BlobsModelFactory.UserDelegationKey(
            "00000000-0000-0000-0000-00000000aaaa",
            "00000000-0000-0000-0000-00000000bbbb",
            startsOn,
            expiresOn,
            "b",
            "2025-01-05",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

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
        Task.FromResult(new MemoryStream(_blobs[name].Content.Take(checked((int)maximumBytes + 1)).ToArray()));

    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(
            !_blobs.ContainsKey(name) ? null : Tags.TryGetValue(name, out var tags) ? tags : new Dictionary<string, string>());

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

        var etag = $"\"0x{Interlocked.Increment(ref _etag):X}\"";
        _blobs[destinationName] = (source.Blob with
        {
            Name = destinationName,
            ETag = etag,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
        }, source.Content);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        _blobs.TryRemove(name, out _);
        Tags.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<bool> CanReadTagsAsync(CancellationToken cancellationToken) => Task.FromResult(TagReadAllowed);
}
