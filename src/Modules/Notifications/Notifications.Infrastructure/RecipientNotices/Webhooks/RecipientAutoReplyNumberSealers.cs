using System.Security.Cryptography;
using Notifications.Application.RecipientNotices;
using Paqueteria.Infrastructure.Security.Pii;

namespace Notifications.Infrastructure.RecipientNotices.Webhooks;

/// <summary>A sender number sealed for one automatic-reply request; <see cref="ToString"/> prints no ciphertext.</summary>
public sealed record SealedAutoReplyNumber(byte[] Ciphertext, string KeyVersion)
{
    public override string ToString() => "SealedAutoReplyNumber { [ciphertext redacted] }";
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 10): seals the sender number of one automatic-reply request
/// under the recipient-notice key, bound to <see cref="RecipientAutoReplyNumbers.PlatformBindingId"/> and the request id.
/// The API can only wrap with that key; only the Worker opens the copy. A failure throws
/// <see cref="RecipientNoticeStoreUnavailableException"/> (503, Meta retries).
/// </summary>
public interface IRecipientAutoReplyNumberSealer
{
    Task<SealedAutoReplyNumber> SealAsync(Guid requestId, string normalizedNumber, CancellationToken cancellationToken);
}

/// <summary>The recipient-notice key in Key Vault, wrap only (the Locations sealer's key).</summary>
internal sealed class AzureKeyVaultAutoReplyNumberSealer(IRecipientNoticeEnvelopeSealer sealer) : IRecipientAutoReplyNumberSealer
{
    public async Task<SealedAutoReplyNumber> SealAsync(Guid requestId, string normalizedNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedNumber);
        PiiProtectedBatch batch;
        try
        {
            batch = await sealer.SealAsync(
                    new PiiBinding(RecipientAutoReplyNumbers.PlatformBindingId, requestId),
                    [new PiiPlaintext(RecipientAutoReplyNumbers.Purpose, normalizedNumber)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RecipientNoticeStoreUnavailableException(exception);
        }

        return batch.Ciphertexts.Count == 1 && !string.IsNullOrWhiteSpace(batch.KeyVersion)
            ? new SealedAutoReplyNumber(batch.Ciphertexts[0], batch.KeyVersion)
            : throw new RecipientNoticeStoreUnavailableException();
    }
}

/// <summary>
/// Development, Testing and DEV_SYNTHETIC: 32 random bytes under the synthetic key version, derived from nothing, so the
/// store never holds anything computed from the number. Nothing can open it; the Worker's synthetic provider replies to
/// the AI-24 placeholder instead.
/// </summary>
internal sealed class SyntheticAutoReplyNumberSealer : IRecipientAutoReplyNumberSealer
{
    public Task<SealedAutoReplyNumber> SealAsync(Guid requestId, string normalizedNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedNumber);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SealedAutoReplyNumber(
            RandomNumberGenerator.GetBytes(32),
            RecipientAutoReplyNumbers.SyntheticKeyVersion));
    }
}

/// <summary>No sealer: the WhatsApp webhook refuses to start enabled, so nothing calls this.</summary>
internal sealed class DisabledAutoReplyNumberSealer : IRecipientAutoReplyNumberSealer
{
    public Task<SealedAutoReplyNumber> SealAsync(Guid requestId, string normalizedNumber, CancellationToken cancellationToken) =>
        throw new RecipientNoticeStoreUnavailableException();
}
