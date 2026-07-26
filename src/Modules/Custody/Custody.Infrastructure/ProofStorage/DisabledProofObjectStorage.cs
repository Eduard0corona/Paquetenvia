using Custody.Application.ProofUploads;
using Custody.Domain;

namespace Custody.Infrastructure.ProofStorage;

public sealed class DisabledProofObjectStorage : IProofObjectStorage
{
    public bool IsEnabled => false;

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(false);

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
        CancellationToken cancellationToken) =>
        Task.FromException<ProofUploadGrant>(new ProofStorageUnavailableException());

    public async IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<Stream> OpenReadAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
        Task.FromException<Stream>(new ProofStorageUnavailableException());

    public Task PromoteAsync(ValidatedProofObject proof, CancellationToken cancellationToken) =>
        Task.FromException(new ProofStorageUnavailableException());

    public Task<ProofObjectDescriptor?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromException<ProofObjectDescriptor?>(new ProofStorageUnavailableException());

    public Task DeleteQuarantineAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
        Task.FromException(new ProofStorageUnavailableException());

    public Task<string> CreateInternalDownloadUrlAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromException<string>(new ProofStorageUnavailableException());
}
