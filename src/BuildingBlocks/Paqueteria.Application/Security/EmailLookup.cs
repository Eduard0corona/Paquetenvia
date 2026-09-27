using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Paqueteria.Application.Security;

/// <summary>
/// REG-PENDING-MEMBERSHIP-STORAGE: one keyed HMAC-SHA256 of a normalized email and the version of the
/// key that produced it. The address itself is never stored, logged or sent anywhere.
/// </summary>
public sealed record EmailLookupHash(int KeyVersion, byte[] Hash);

/// <summary>
/// Keyed lookup hashes of normalized email addresses. Pending memberships store the hash under the
/// current key; a sign-in looks up every configured key version, so a key rotation keeps older entries
/// reachable until they expire.
/// </summary>
public interface IEmailLookupHasher
{
    /// <summary>False when no key is configured (only permitted in Development and Testing).</summary>
    bool IsAvailable { get; }

    /// <summary>The hash under the current key version. Throws <see cref="EmailLookupUnavailableException"/> when unavailable.</summary>
    EmailLookupHash HashForStorage(string normalizedEmail);

    /// <summary>One hash per configured key version, current first. Throws <see cref="EmailLookupUnavailableException"/> when unavailable.</summary>
    IReadOnlyList<EmailLookupHash> HashForLookup(string normalizedEmail);
}

/// <summary>No email lookup key is configured, so nothing can be hashed.</summary>
public sealed class EmailLookupUnavailableException(string message) : Exception(message);

/// <summary>
/// REG-PENDING-MEMBERSHIP-STORAGE normalization: trim, Unicode NFC and invariant lowercase. The same
/// function runs when an administrator adds an address and when a verified email signs in, so both
/// sides hash the same bytes.
/// </summary>
public static class EmailLookupNormalizer
{
    public const int MaximumLength = 320;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return false;
        }

        string candidate;
        try
        {
            // Unpaired surrogates are not text and are never a usable address.
            _ = StrictUtf8.GetByteCount(value);
            candidate = value.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant()
                .Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var at = candidate.IndexOf('@', StringComparison.Ordinal);
        if (candidate.Length is < 3 or > MaximumLength ||
            at <= 0 ||
            at != candidate.LastIndexOf('@') ||
            at == candidate.Length - 1 ||
            candidate.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
