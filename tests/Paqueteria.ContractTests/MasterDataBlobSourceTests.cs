using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Paqueteria.ContractTests.Support;

namespace Paqueteria.ContractTests;

/// <summary>
/// MDM-001 route S2 ("Contenedor privado + job"), no database: <c>master-data-load</c> reads the reviewed document
/// from a private Blob container with the production <see cref="MasterDataBlobSource"/> and a stand-in Azure transport
/// (<see cref="StandInBlobService"/>). Covered: exactly one source and the option rules; the download into memory
/// through the OBS-003 transport; the 16 MiB limit before the body is read and while it is read; the SHA-256 checked
/// before any validation or connection (a mismatch is a rejected file, exit 8); no deletion on a rejection or a failure
/// (the committed path is covered against PostgreSQL by <c>MasterDataLoaderPostgreSqlContractTests</c>); and no
/// document content or service response in the output.
/// </summary>
public sealed class MasterDataBlobSourceTests
{
    private const string Organization = "11111111-1111-1111-1111-111111111111";

    /// <summary>A value that sits inside the document; it must never reach the console or an error.</summary>
    private const string Sentinel = "Centinela Sintetica 7f3a";

    // Nothing listens on port 1: any attempt to connect fails at once, and differently from a file rejection.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Username=nobody;Password=none;Timeout=2;Pooling=false";

    private static readonly string[] TraceHeaders = ["traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"];

    private static string[] BlobArguments(params string[] extra) =>
    [
        "master-data-load", "--connection-env", "PAQUETERIA_MASTER_DATA_CONNECTION",
        "--blob-account-url", StandInBlobService.AccountUrl,
        "--blob-container", StandInBlobService.Container,
        "--blob-name", StandInBlobService.BlobName,
        "--organization-id", Organization,
        .. extra,
    ];

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    [Fact]
    public void A_blob_source_takes_the_three_references_and_the_expected_sha256()
    {
        var sha = new string('a', 64);
        var options = CommandOptions.Parse(BlobArguments("--expected-sha256", sha, "--delete-after-load")).MasterData!;

        Assert.Null(options.FilePath);
        Assert.Equal(new Uri(StandInBlobService.AccountUrl), options.Blob!.AccountUrl);
        Assert.Equal(StandInBlobService.Container, options.Blob.Container);
        Assert.Equal(StandInBlobService.BlobName, options.Blob.Name);
        Assert.Equal(Convert.FromHexString(sha), options.ExpectedSha256);
        Assert.True(options.DeleteAfterLoad);
        Assert.False(options.DryRun);

        // Deletion is opt-in.
        Assert.False(CommandOptions.Parse(BlobArguments("--expected-sha256", sha)).MasterData!.DeleteAfterLoad);
    }

    [Fact]
    public void A_file_source_keeps_working_and_may_carry_the_expected_sha256()
    {
        string[] file = ["master-data-load", "--connection-env", "C", "--file", "reviewed.json", "--organization-id", Organization];

        var plain = CommandOptions.Parse(file).MasterData!;
        Assert.Equal("reviewed.json", plain.FilePath);
        Assert.Null(plain.Blob);
        Assert.Null(plain.ExpectedSha256);

        var pinned = CommandOptions.Parse([.. file, "--expected-sha256", new string('0', 64)]).MasterData!;
        Assert.Equal(new byte[32], pinned.ExpectedSha256);
    }

    public static TheoryData<string[], string> RefusedCombinations() => new()
    {
        { ["--file", "reviewed.json", "--blob-account-url", StandInBlobService.AccountUrl, "--blob-container", "c0ntainer", "--blob-name", "a.json", "--expected-sha256", new string('a', 64)], "exactly one source" },
        { [], "exactly one source" },
        { ["--blob-account-url", StandInBlobService.AccountUrl, "--blob-container", "master-data", "--expected-sha256", new string('a', 64)], "together" },
        { ["--blob-name", "a.json", "--expected-sha256", new string('a', 64)], "together" },
        { ["--blob-account-url", StandInBlobService.AccountUrl, "--blob-container", "master-data", "--blob-name", "a.json"], "requires --expected-sha256" },
        { ["--file", "reviewed.json", "--delete-after-load"], "--delete-after-load is valid only" },
        { ["--blob-account-url", StandInBlobService.AccountUrl, "--blob-container", "master-data", "--blob-name", "a.json", "--expected-sha256", new string('a', 64), "--delete-after-load", "--dry-run"], "--delete-after-load is valid only" },
        { ["--file", "reviewed.json", "--expected-sha256", new string('A', 64)], "64 lowercase hexadecimal" },
        { ["--file", "reviewed.json", "--expected-sha256", new string('a', 63)], "64 lowercase hexadecimal" },
        { ["--file", "reviewed.json", "--expected-sha256", "a", "--expected-sha256", "b"], "Unknown, repeated or incomplete option" },
        { ["--blob-account-url", StandInBlobService.AccountUrl, "--blob-account-url", StandInBlobService.AccountUrl], "Unknown, repeated or incomplete option" },
    };

    [Theory]
    [MemberData(nameof(RefusedCombinations))]
    public void Every_other_combination_is_refused(string[] options, string expected)
    {
        string[] arguments = ["master-data-load", "--connection-env", "C", "--organization-id", Organization, .. options];

        var refused = Assert.Throws<CommandLineException>(() => CommandOptions.Parse(arguments));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://stmdm001synthetic.blob.core.windows.net/?sv=2025-11-05&sig=c2VjcmV0", "--blob-account-url")]
    [InlineData("https://stmdm001synthetic.blob.core.windows.net/master-data", "--blob-account-url")]
    [InlineData("http://stmdm001synthetic.blob.core.windows.net/", "--blob-account-url")]
    [InlineData("https://stmdm001synthetic.blob.core.windows.net:8443/", "--blob-account-url")]
    [InlineData("https://user:secret@stmdm001synthetic.blob.core.windows.net/", "--blob-account-url")]
    [InlineData("https://STMDM001SYNTHETIC.blob.core.windows.net/", "--blob-account-url")]
    [InlineData("https://stmdm001synthetic.example.test/", "--blob-account-url")]
    public void An_account_url_other_than_the_blob_endpoint_is_refused_without_echoing_it(string url, string expected)
    {
        var refused = Assert.Throws<CommandLineException>(() => MasterDataBlobReference.Parse(url, "master-data", "a.json"));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(url, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Master-Data", "a.json", "--blob-container")]
    [InlineData("md", "a.json", "--blob-container")]
    [InlineData("master--data", "a.json", "--blob-container")]
    [InlineData("-master-data", "a.json", "--blob-container")]
    [InlineData("master-data", "../a.json", "--blob-name")]
    [InlineData("master-data", "/a.json", "--blob-name")]
    [InlineData("master-data", "a b.json", "--blob-name")]
    [InlineData("master-data", "a.json?comp=list", "--blob-name")]
    [InlineData("master-data", "", "--blob-name")]
    public void Container_and_blob_names_use_a_narrow_alphabet(string container, string name, string expected)
    {
        var refused = Assert.Throws<CommandLineException>(() =>
            MasterDataBlobReference.Parse(StandInBlobService.AccountUrl, container, name));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_account_url_is_normalized_to_the_service_endpoint()
    {
        var reference = MasterDataBlobReference.Parse("https://stmdm001synthetic.blob.core.windows.net", "master-data", "2026-11/zonas.json");

        Assert.Equal(new Uri("https://stmdm001synthetic.blob.core.windows.net/"), reference.AccountUrl);
        Assert.Equal("account=stmdm001synthetic.blob.core.windows.net container=master-data blob=2026-11/zonas.json", reference.Describe());
    }

    [Fact]
    public async Task The_blob_is_downloaded_into_memory_at_the_version_measured()
    {
        var content = Encoding.UTF8.GetBytes("{\"synthetic\":true}");
        var service = new StandInBlobService(content);

        var download = await service.Source().DownloadAsync(CancellationToken.None);

        Assert.Equal(content, download.Content);
        Assert.Equal(StandInBlobService.ETag, download.ETag.ToString("H"));
        Assert.Collection(
            service.Requests,
            head =>
            {
                Assert.Equal("HEAD", head.Method);
                Assert.Equal($"/{StandInBlobService.Container}/{StandInBlobService.BlobName}", head.Path);
                Assert.Contains("Authorization", head.HeaderNames);
            },
            get =>
            {
                Assert.Equal("GET", get.Method);
                Assert.Equal(StandInBlobService.ETag, get.IfMatch);
            });
    }

    [Fact]
    public async Task No_request_carries_trace_context()
    {
        // Control: a Blob client with the SDK defaults sends traceparent through the same stand-in, so the capture is real.
        var control = new StandInBlobService(Encoding.UTF8.GetBytes("{}"));
        var unwrapped = new Azure.Storage.Blobs.BlobClient(
            new Uri($"{StandInBlobService.AccountUrl}{StandInBlobService.Container}/{StandInBlobService.BlobName}"),
            new Azure.Storage.Blobs.BlobClientOptions { Transport = control.Transport });
        await WithinSampledActivityAsync(() => unwrapped.GetPropertiesAsync());
        Assert.Contains(control.Requests, request => request.HeaderNames.Contains("traceparent"));

        var service = new StandInBlobService(Encoding.UTF8.GetBytes("{}"));
        await WithinSampledActivityAsync(async () =>
        {
            var blob = service.Source();
            var download = await blob.DownloadAsync(CancellationToken.None);
            await blob.DeleteAsync(download.ETag, CancellationToken.None);
        });

        Assert.Equal(["HEAD", "GET", "DELETE"], service.Requests.Select(request => request.Method));
        foreach (var request in service.Requests)
        {
            foreach (var header in TraceHeaders)
            {
                Assert.DoesNotContain(header, request.HeaderNames);
            }
        }
    }

    /// <summary>As <c>Obs003AzureClientTracingTests</c>: a sampled activity with trace state and baggage is current.</summary>
    private static async Task WithinSampledActivityAsync(Func<Task> call)
    {
        using var source = new ActivitySource("Mdm001.Blob");
        using var listener = new ActivityListener
        {
            ShouldListenTo = item => item.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity(
            "synthetic-job", ActivityKind.Internal,
            ActivityContext.Parse("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", "vendor=synthetic"))!;
        activity.AddBaggage("tenant", "synthetic-org");
        Assert.True(activity.Recorded);
        await call();
    }

    [Theory]
    [InlineData(MasterDataDocumentValidator.MaximumFileBytes + 1L)]
    [InlineData(0L)]
    public async Task A_blob_size_outside_the_limit_is_refused_before_its_body_is_read(long reported)
    {
        var service = new StandInBlobService(Encoding.UTF8.GetBytes("{}")) { ReportedLength = reported };

        var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => service.Source().DownloadAsync(CancellationToken.None));

        Assert.Equal(MasterDataLoader.ValidationExitCode, refused.ExitCode);
        Assert.StartsWith($"MDM001_BLOB_SIZE bytes={reported} ", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["HEAD"], service.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task The_read_stops_one_byte_past_the_limit_whatever_the_service_says()
    {
        var oversized = new byte[MasterDataDocumentValidator.MaximumFileBytes + 4096];
        var service = new StandInBlobService(oversized) { ReportedLength = 2, DownloadReportedLength = 2 };

        var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => service.Source().DownloadAsync(CancellationToken.None));

        Assert.Equal(MasterDataLoader.ValidationExitCode, refused.ExitCode);
        Assert.StartsWith($"MDM001_BLOB_SIZE bytes={MasterDataDocumentValidator.MaximumFileBytes + 1} ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_failure_names_only_its_status_and_code()
    {
        var service = new StandInBlobService(Encoding.UTF8.GetBytes("{}"))
        {
            Failure = (HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch"),
            FailureBody = Sentinel,
        };
        var output = new StringWriter();

        var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(new byte[32]), new MasterDataEnvironment("Testing", null), output,
            CancellationToken.None, service.Source()));

        Assert.Equal(MasterDataLoader.ValidationExitCode, refused.ExitCode);
        Assert.Equal(
            $"MDM001_BLOB_UNREADABLE status=403 code=AuthorizationPermissionMismatch {StandInBlobService.Reference.Describe()}",
            refused.Message);
        Assert.DoesNotContain(Sentinel, refused.Message + output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_sha256_lets_the_document_through_to_validation_and_prints_references_only()
    {
        var content = SyntheticDocument(owner: Guid.NewGuid().ToString("D"));
        var service = new StandInBlobService(content);
        var output = new StringWriter();

        var rejected = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(SHA256.HashData(content), deleteAfterLoad: true),
            new MasterDataEnvironment("Testing", null), output, CancellationToken.None, service.Source()));

        // The digest matched, so the validator ran and refused the owner; nothing reached the database.
        Assert.Equal(MasterDataLoader.ValidationExitCode, rejected.ExitCode);
        Assert.StartsWith("MDM001_DOCUMENT_REJECTED errors=1 ", rejected.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"MDM001_SOURCE source=blob {StandInBlobService.Reference.Describe()} bytes={content.Length} sha256={Sha(content)}",
            output.ToString(), StringComparison.Ordinal);
        Assert.Contains("MDM001_VALIDATION_ERROR document.owner_org_id: MDM001_OWNER_ORGANIZATION_MISMATCH", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, rejected.Message + output, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", service.Requests.Select(request => request.Method));
        Assert.DoesNotContain("MDM001_BLOB_DELETED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sha256_mismatch_is_a_rejected_file_before_validation_or_any_connection()
    {
        var content = SyntheticDocument(Organization);
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("the reviewed file was another one"));
        var service = new StandInBlobService(content);
        var output = new StringWriter();

        var rejected = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(expected, deleteAfterLoad: true), new MasterDataEnvironment("Testing", null), output,
            CancellationToken.None, service.Source()));

        Assert.Equal(MasterDataLoader.ValidationExitCode, rejected.ExitCode);
        Assert.Equal(
            $"MDM001_FILE_SHA256_MISMATCH sha256={Sha(content)} expected={Convert.ToHexStringLower(expected)}; " +
            "the document was neither validated nor loaded",
            rejected.Message);
        Assert.DoesNotContain("MDM001_VALIDATION_ERROR", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, rejected.Message + output, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", service.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task A_sha256_mismatch_on_a_local_file_is_refused_the_same_way()
    {
        var content = SyntheticDocument(Organization);
        var file = Path.Combine(Path.GetTempPath(), $"mdm001-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(file, content);
        try
        {
            var rejected = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataLoader.RunAsync(
                UnreachableDatabase,
                new MasterDataLoadOptions(file, Guid.Parse(Organization), false, false, ExpectedSha256: new byte[32]),
                new MasterDataEnvironment("Testing", null), TextWriter.Null, CancellationToken.None));

            Assert.Equal(MasterDataLoader.ValidationExitCode, rejected.ExitCode);
            Assert.StartsWith($"MDM001_FILE_SHA256_MISMATCH sha256={Sha(content)} ", rejected.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_failure_after_validation_never_deletes_the_blob()
    {
        var content = SyntheticDocument(Organization);
        var service = new StandInBlobService(content);
        var output = new StringWriter();

        // A valid, matching document whose database cannot be reached: the load fails, so the blob stays.
        var failed = await Assert.ThrowsAnyAsync<Exception>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(SHA256.HashData(content), deleteAfterLoad: true),
            new MasterDataEnvironment("Testing", null), output, CancellationToken.None, service.Source()));

        Assert.IsNotType<MasterDataLoadException>(failed);
        Assert.Equal(["HEAD", "GET"], service.Requests.Select(request => request.Method));
        Assert.False(service.Deleted);
        Assert.DoesNotContain(Sentinel, failed.Message + output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_loader_refuses_options_that_would_delete_on_a_dry_run_or_without_a_blob()
    {
        var service = new StandInBlobService(SyntheticDocument(Organization));

        await Assert.ThrowsAsync<InvalidOperationException>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(new byte[32], deleteAfterLoad: true) with { DryRun = true },
            new MasterDataEnvironment("Testing", null), TextWriter.Null, CancellationToken.None, service.Source()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(null), new MasterDataEnvironment("Testing", null), TextWriter.Null,
            CancellationToken.None, service.Source()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MasterDataLoader.RunAsync(
            UnreachableDatabase, Options(new byte[32]), new MasterDataEnvironment("Testing", null), TextWriter.Null,
            CancellationToken.None, blobSource: null));

        Assert.Empty(service.Requests);
    }

    private static MasterDataLoadOptions Options(byte[]? expected, bool deleteAfterLoad = false) => new(
        null, Guid.Parse(Organization), DryRun: false, AllowRealDriverProfiles: false, StandInBlobService.Reference, expected,
        deleteAfterLoad);

    /// <summary>The shipped synthetic example with a sentinel area name and the given owner.</summary>
    internal static byte[] SyntheticDocument(string owner)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "tests", "fixtures", "mdm-001", "synthetic-master-data.json"))
            .Replace(Organization, owner, StringComparison.Ordinal)
            .Replace("Synthetic MDM Service Area", Sentinel, StringComparison.Ordinal);
        Assert.Contains(Sentinel, text, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text);
    }
}
