using System.Diagnostics.CodeAnalysis;

namespace Paqueteria.Application.Security;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.opt_out.hash</c>): the channel a recipient contact is
/// reached on. Its wire name is part of the keyed hash.
/// </summary>
public enum RecipientContactChannel
{
    WhatsApp,
    Email,
}

/// <summary>One keyed hash of a normalized recipient contact and the version of the key that produced it.</summary>
public sealed record ContactLookupHash(int KeyVersion, byte[] Hash)
{
    public override string ToString() => $"ContactLookupHash {{ KeyVersion = {KeyVersion} }}";
}

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.opt_out.hash</c>; NTF-RECIPIENT-NOTICE-CAPS-2026-10-23
/// L4 choice 6): HMAC-SHA256 under the contact lookup key (Key Vault secret <c>paquetenvia-contact-lookup-key-1</c>,
/// versioned like <c>EmailLookup:Keys</c>) over <c>paquetenvia-contact-v1|{channel}|{value}</c>. The contact itself is
/// never stored, logged or returned; the opt-out store keeps only these hashes.
/// </summary>
public interface IRecipientContactLookup
{
    /// <summary>False when no contact lookup key is configured: nothing can be hashed, so nothing may be sent.</summary>
    bool IsAvailable { get; }

    /// <summary>One hash per configured key version, current first. Throws when unavailable or the value is invalid.</summary>
    IReadOnlyList<ContactLookupHash> HashForLookup(RecipientContactChannel channel, string contact);
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 7 (AI-24 <c>recipient_notices.webhooks.unsubscribe</c>): the token of
/// the e-mail unsubscribe link and of its <c>List-Unsubscribe</c> one-click URL. It binds the owner organization and the
/// notification under HMAC-SHA256 with the contact lookup key and the prefix <c>paquetenvia-unsubscribe-v1</c>; it is
/// never stored or logged. AI-08 NTF-006 verifies it with <see cref="TryVerify"/>.
/// </summary>
public interface IRecipientNoticeUnsubscribeTokens
{
    bool IsAvailable { get; }

    string Issue(Guid ownerOrganizationId, Guid notificationId);

    bool TryVerify(string? token, out Guid ownerOrganizationId, out Guid notificationId);
}

/// <summary>No contact lookup key is configured, so nothing can be hashed or signed.</summary>
public sealed class ContactLookupUnavailableException(string message) : Exception(message);

/// <summary>
/// AI-24 <c>recipient_notices.opt_out.hash</c>: the value a contact is hashed as. A phone is the E.164 <c>+52</c> number
/// (ten national digits; a WhatsApp <c>521</c> mobile prefix is reduced to <c>52</c> first); an e-mail is the trimmed,
/// lower-cased address. The same function runs on every side, so a stored hash and a lookup hash agree.
/// </summary>
public static class RecipientContactValue
{
    public const string PhoneCountryPrefix = "+52";

    public static string WireName(RecipientContactChannel channel) => channel switch
    {
        RecipientContactChannel.WhatsApp => "WHATSAPP",
        RecipientContactChannel.Email => "EMAIL",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown recipient contact channel."),
    };

    public static bool TryNormalize(
        RecipientContactChannel channel,
        string? value,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(value) || value.Length > 320)
        {
            return false;
        }

        switch (channel)
        {
            case RecipientContactChannel.WhatsApp:
            {
                var digits = value.StartsWith('+') ? value[1..] : value;
                if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
                {
                    return false;
                }

                var national = digits.Length switch
                {
                    10 when !value.StartsWith('+') => digits,
                    12 when digits.StartsWith("52", StringComparison.Ordinal) => digits[2..],
                    13 when digits.StartsWith("521", StringComparison.Ordinal) => digits[3..],
                    _ => null,
                };
                if (national is null)
                {
                    return false;
                }

                normalized = PhoneCountryPrefix + national;
                return true;
            }

            case RecipientContactChannel.Email:
            {
                var trimmed = value.Trim(' ', '\t', '\n', '\v', '\f', '\r');
                var at = trimmed.IndexOf('@', StringComparison.Ordinal);
                if (trimmed.Length is < 3 or > 254 ||
                    trimmed.Any(character => character is < '!' or > '~') ||
                    at < 1 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
                {
                    return false;
                }

                normalized = trimmed.ToLowerInvariant();
                return true;
            }

            default:
                return false;
        }
    }
}
