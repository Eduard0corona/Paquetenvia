using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Paqueteria.Infrastructure.Cloud;

/// <summary>
/// MDM-001 route S2 ("Contenedor privado + job"): where the reviewed document sits in a private Blob container.
/// The account is named by its Blob service endpoint only (<c>https://{account}.blob.core.windows.net/</c>): no SAS,
/// query, path or port is accepted, because the job reads with its own managed identity and nothing else. The
/// container and blob names use a narrow alphabet so they can be printed as references.
/// </summary>
internal sealed partial record MasterDataBlobReference(Uri AccountUrl, string Container, string Name)
{
    /// <summary>Parses the three command-line values; an invalid value is refused without being echoed.</summary>
    internal static MasterDataBlobReference Parse(string accountUrl, string container, string name)
    {
        if (!Uri.TryCreate(accountUrl, UriKind.Absolute, out var account) ||
            account.Scheme != Uri.UriSchemeHttps ||
            !account.IsDefaultPort ||
            !string.IsNullOrEmpty(account.UserInfo) ||
            account.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(account.Query) ||
            !string.IsNullOrEmpty(account.Fragment) ||
            !string.Equals(accountUrl.TrimEnd('/'), $"https://{account.Host}", StringComparison.Ordinal) ||
            !AccountHost().IsMatch(account.Host))
        {
            throw new CommandLineException(
                "--blob-account-url must be the Blob service endpoint https://{account}.blob.core.windows.net/ " +
                "(lowercase, no SAS, query, path or port); its value is not shown.");
        }

        if (!ContainerName().IsMatch(container))
        {
            throw new CommandLineException(
                "--blob-container must be a Blob container name (3 to 63 lowercase letters, digits and single " +
                "hyphens); its value is not shown.");
        }

        if (name.Length > 1024 || !BlobName().IsMatch(name))
        {
            throw new CommandLineException(
                "--blob-name must be up to 9 '/'-separated segments of letters, digits, '.', '_' and '-', each " +
                "starting with a letter or digit; its value is not shown.");
        }

        return new MasterDataBlobReference(new Uri($"https://{account.Host}/"), container, name);
    }

    /// <summary>The reference printed by the job: where the document came from, never what it holds.</summary>
    internal string Describe() => $"account={AccountUrl.Host} container={Container} blob={Name}";

    [GeneratedRegex(@"^[a-z0-9]{3,24}\.blob\.core\.windows\.net$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountHost();

    [GeneratedRegex(@"^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerName();

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]{0,127}/){0,8}[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex BlobName();
}

/// <summary>The bytes of the reviewed document and the ETag they were read at.</summary>
internal sealed record MasterDataBlobDownload(byte[] Content, ETag ETag);

/// <summary>
/// MDM-001 route S2: reads the reviewed document from its private Blob container into memory with the job's managed
/// identity (<see cref="AzureWorkloadCredential"/>, user-assigned through <c>AZURE_CLIENT_ID</c> as every other job;
/// S6: no password, key or SAS), and, only when the operator asked for it and the load committed, deletes exactly the
/// version it read. The size is refused above <see cref="MasterDataDocumentValidator.MaximumFileBytes"/> before the
/// body is read, and the read stops one byte past the limit whatever the service says. Every request goes through the
/// OBS-003 transport (<see cref="AzureClientTracing"/>); content logging is off, and a failure reports only the HTTP
/// status and the Azure error code, never the response or the document.
/// </summary>
internal sealed class MasterDataBlobSource
{
    /// <summary>The deletion was asked for and the load committed, but the blob was not deleted.</summary>
    internal const int DeleteFailedExitCode = 11;

    private readonly BlobClient _blob;

    /// <param name="transport">Test seam: routes the Blob SDK through a stand-in transport.</param>
    internal MasterDataBlobSource(MasterDataBlobReference reference, TokenCredential credential, HttpPipelineTransport? transport = null)
    {
        Reference = reference;
        _blob = new BlobServiceClient(reference.AccountUrl, credential, CreateClientOptions(transport))
            .GetBlobContainerClient(reference.Container)
            .GetBlobClient(reference.Name);
    }

    internal MasterDataBlobReference Reference { get; }

    /// <summary>The production source: the job's managed identity, as the Key Vault reader of the same job uses it.</summary>
    internal static MasterDataBlobSource Create(MasterDataBlobReference reference) => new MasterDataBlobSource(
        reference,
        AzureWorkloadCredential.Create(Environment.GetEnvironmentVariable(AzureWorkloadCredential.ClientIdVariable)));

    /// <summary>
    /// Bounded retries, no content logging and no trace context on any request (OBS-003). <paramref name="transport"/>
    /// is a test seam.
    /// </summary>
    internal static BlobClientOptions CreateClientOptions(HttpPipelineTransport? transport = null)
    {
        var options = new BlobClientOptions();
        if (transport is not null)
        {
            options.Transport = transport;
        }

        options.Retry.MaxRetries = 3;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsLoggingEnabled = false;
        return options.WithoutDistributedTracing();
    }

    internal async Task<MasterDataBlobDownload> DownloadAsync(CancellationToken cancellationToken)
    {
        const long maximum = MasterDataDocumentValidator.MaximumFileBytes;
        try
        {
            var properties = (await _blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            if (properties.ContentLength is <= 0 or > maximum)
            {
                throw SizeRefused(properties.ContentLength);
            }

            // The version measured is the version read: a blob replaced in between answers 412 instead.
            var response = await _blob.DownloadStreamingAsync(
                    new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = properties.ETag } },
                    cancellationToken)
                .ConfigureAwait(false);
            using var download = response.Value;
            if (download.Details.ContentLength > maximum)
            {
                throw SizeRefused(download.Details.ContentLength);
            }

            await using var source = download.Content;
            var content = new MemoryStream((int)Math.Min(download.Details.ContentLength, maximum) + 1);
            var buffer = new byte[81_920];
            int read;
            while (content.Length <= maximum &&
                   (read = await source.ReadAsync(
                           buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum + 1 - content.Length)), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                content.Write(buffer, 0, read);
            }

            if (content.Length is 0 or > maximum)
            {
                throw SizeRefused(content.Length);
            }

            return new MasterDataBlobDownload(content.ToArray(), properties.ETag);
        }
        catch (RequestFailedException exception)
        {
            throw new MasterDataLoadException(
                $"MDM001_BLOB_UNREADABLE status={exception.Status} code={ErrorCode(exception)} {Reference.Describe()}",
                MasterDataLoader.ValidationExitCode);
        }
        catch (Exception exception) when (exception is not (MasterDataLoadException or OperationCanceledException))
        {
            // A credential or transport failure: its message may quote a response, so only its type is named.
            throw new MasterDataLoadException(
                $"MDM001_BLOB_UNREADABLE ({exception.GetType().Name}) {Reference.Describe()}",
                MasterDataLoader.ValidationExitCode);
        }
    }

    /// <summary>
    /// Deletes the blob only while it still has <paramref name="etag"/>, the version that was loaded, with its
    /// snapshots. A blob replaced after the download is left alone.
    /// </summary>
    internal async Task DeleteAsync(ETag etag, CancellationToken cancellationToken)
    {
        try
        {
            await _blob.DeleteAsync(
                    DeleteSnapshotsOption.IncludeSnapshots,
                    new BlobRequestConditions { IfMatch = etag },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception)
        {
            throw new MasterDataLoadException(
                $"MDM001_BLOB_DELETE_FAILED status={exception.Status} code={ErrorCode(exception)} {Reference.Describe()}; " +
                "the load was committed and the blob is still there",
                DeleteFailedExitCode);
        }
        catch (Exception exception) when (exception is not (MasterDataLoadException or OperationCanceledException))
        {
            throw new MasterDataLoadException(
                $"MDM001_BLOB_DELETE_FAILED ({exception.GetType().Name}) {Reference.Describe()}; " +
                "the load was committed and the blob is still there",
                DeleteFailedExitCode);
        }
    }

    private MasterDataLoadException SizeRefused(long bytes) => new MasterDataLoadException(
        $"MDM001_BLOB_SIZE bytes={bytes} (1..{MasterDataDocumentValidator.MaximumFileBytes}) {Reference.Describe()}",
        MasterDataLoader.ValidationExitCode);

    /// <summary>The Azure error code when it is a plain token; anything else is not printed.</summary>
    private static string ErrorCode(RequestFailedException exception) =>
        exception.ErrorCode is { Length: > 0 and <= 64 } code && code.All(char.IsAsciiLetterOrDigit) ? code : "none";
}
