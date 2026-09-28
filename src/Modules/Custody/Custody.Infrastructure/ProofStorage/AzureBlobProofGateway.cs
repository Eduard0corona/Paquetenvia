using System.Net;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Custody.Application.ProofUploads;
using Microsoft.Extensions.Options;

namespace Custody.Infrastructure.ProofStorage;

/// <summary>A blob as the proof adapters see it; metadata keeps the raw Azure names.</summary>
public sealed record ProofBlob(
    string Name,
    long SizeBytes,
    string ContentType,
    string ETag,
    DateTimeOffset LastModified,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ProofBlobContainerState(bool Exists, bool IsPrivate);

/// <summary>
/// The narrow set of Blob Storage operations ADP-001 uses. The production implementation is a thin
/// pass-through to the Azure SDK; unit tests substitute a fake so the adapter logic (SAS scope,
/// metadata mapping, conditional promotion, verdict handling) is verified without Azure.
/// </summary>
public interface IProofBlobGateway
{
    string AccountName { get; }

    Uri BlobUri(string name);

    Task<ProofBlobContainerState> GetContainerStateAsync(CancellationToken cancellationToken);

    Task<UserDelegationKey> GetUserDelegationKeyAsync(
        DateTimeOffset startsOn,
        DateTimeOffset expiresOn,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProofBlob> ListAsync(string prefix, CancellationToken cancellationToken);

    Task<ProofBlob?> GetPropertiesAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads at most <paramref name="maximumBytes"/> + 1 bytes, so an oversized object is detectable.</summary>
    Task<MemoryStream> DownloadAsync(string name, long maximumBytes, CancellationToken cancellationToken);

    /// <summary>The blob index tags, or <see langword="null"/> when the blob does not exist.</summary>
    Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Server-side copy that succeeds only while the source still has <paramref name="sourceETag"/>
    /// and the destination does not exist. Throws <see cref="ProofObjectChangedException"/> when the
    /// source changed and <see cref="ProofConflictException"/> (<c>FINAL_OBJECT_COLLISION</c>) when the
    /// destination exists.
    /// </summary>
    Task CopyAsync(
        string sourceName,
        string sourceETag,
        string destinationName,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// True when this identity may read blob index tags. Probed with a Get Blob Tags on a blob that
    /// never exists: 404 proves the permission, 403 proves its absence.
    /// </summary>
    Task<bool> CanReadTagsAsync(CancellationToken cancellationToken);
}

public sealed class AzureBlobProofGateway : IProofBlobGateway
{
    internal const string TagProbeBlobName = "quarantine/.adp001-tag-read-probe";

    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;

    public AzureBlobProofGateway(IOptions<ProofStorageOptions> options, TokenCredential credential)
    {
        var settings = options.Value.AzureBlob;
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.MaxRetries = 3;
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        clientOptions.Diagnostics.IsLoggingContentEnabled = false;
        _service = new BlobServiceClient(new Uri(settings.ServiceUri), credential, clientOptions);
        _container = _service.GetBlobContainerClient(settings.ContainerName);
    }

    public string AccountName => _service.AccountName;

    public Uri BlobUri(string name) => _container.GetBlobClient(name).Uri;

    public async Task<ProofBlobContainerState> GetContainerStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var properties = await _container.GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new ProofBlobContainerState(true, properties.Value.PublicAccess is null or PublicAccessType.None);
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            return new ProofBlobContainerState(false, true);
        }
    }

    public async Task<UserDelegationKey> GetUserDelegationKeyAsync(
        DateTimeOffset startsOn,
        DateTimeOffset expiresOn,
        CancellationToken cancellationToken) =>
        (await _service.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken).ConfigureAwait(false)).Value;

    public async IAsyncEnumerable<ProofBlob> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _container
                           .GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (item.Deleted || item.Properties.ContentLength is null || item.Properties.ETag is null)
            {
                continue;
            }

            yield return new ProofBlob(
                item.Name,
                item.Properties.ContentLength.Value,
                item.Properties.ContentType ?? string.Empty,
                item.Properties.ETag.Value.ToString(),
                item.Properties.LastModified ?? DateTimeOffset.MinValue,
                new Dictionary<string, string>(item.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
        }
    }

    public async Task<ProofBlob?> GetPropertiesAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var properties = (await _container.GetBlobClient(name)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false)).Value;
            return new ProofBlob(
                name,
                properties.ContentLength,
                properties.ContentType ?? string.Empty,
                properties.ETag.ToString(),
                properties.LastModified,
                new Dictionary<string, string>(properties.Metadata, StringComparer.OrdinalIgnoreCase));
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<MemoryStream> DownloadAsync(string name, long maximumBytes, CancellationToken cancellationToken)
    {
        var response = await _container.GetBlobClient(name)
            .DownloadStreamingAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await using var source = response.Value.Content;
        var limit = checked(maximumBytes + 1);
        var content = new MemoryStream(checked((int)Math.Min(response.Value.Details.ContentLength, limit)));
        var buffer = new byte[81_920];
        int read;
        while (content.Length < limit &&
               (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - content.Length)), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            content.Write(buffer, 0, read);
        }

        content.Position = 0;
        return content;
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _container.GetBlobClient(name)
                .GetTagsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new Dictionary<string, string>(result.Value.Tags, StringComparer.Ordinal);
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CopyAsync(
        string sourceName,
        string sourceETag,
        string destinationName,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await _container.GetBlobClient(destinationName).StartCopyFromUriAsync(
                    _container.GetBlobClient(sourceName).Uri,
                    new BlobCopyFromUriOptions
                    {
                        Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                        SourceConditions = new BlobRequestConditions { IfMatch = new ETag(sourceETag) },
                        DestinationConditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (
            exception.Status == (int)HttpStatusCode.PreconditionFailed &&
            string.Equals(exception.ErrorCode, "SourceConditionNotMet", StringComparison.Ordinal))
        {
            throw new ProofObjectChangedException();
        }
        catch (RequestFailedException exception) when (
            exception.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.PreconditionFailed)
        {
            // BlobAlreadyExists (409) or the destination If-None-Match (412 ConditionNotMet).
            throw new ProofConflictException("FINAL_OBJECT_COLLISION");
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken) =>
        await _container.GetBlobClient(name)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> CanReadTagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _container.GetBlobClient(TagProbeBlobName)
                .GetTagsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (RequestFailedException)
        {
            return false;
        }
    }
}
