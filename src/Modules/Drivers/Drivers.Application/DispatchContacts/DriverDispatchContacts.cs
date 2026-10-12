using Paqueteria.Application.Voice;

namespace Drivers.Application.DispatchContacts;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable (Recommended)"): one dispatch phone number per
/// organization, set in the server configuration (<c>Drivers:DispatchContacts</c>), that the driver app shows as
/// "Llamar a despacho" where it asks the driver to call dispatch. It is a business contact, not a person's datum,
/// but it is never logged and never part of a <c>ToString</c>.
/// </summary>
public static class DriverDispatchContactPolicy
{
    /// <summary>
    /// The organization id is written as a canonical UUID: the lowercase hyphenated form (<c>D</c>), never the nil
    /// UUID, so one organization is always written the same way and a duplicate is always seen.
    /// </summary>
    public static bool TryParseOrganizationId(string? value, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (value is null ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            return false;
        }

        organizationId = parsed;
        return true;
    }

    /// <summary>The length of <c>+52</c> and the ten national digits.</summary>
    public const int PhoneE164Length = 13;

    /// <summary>
    /// A Mexican number in E.164: <c>+52</c> followed by the ten national ASCII digits, the first one 2-9 (Mexican
    /// numbers never start with 0 or 1; the numbers <see cref="VoicePhoneNumber.IsDialable"/> calls). Nothing else
    /// is accepted: no spaces, hyphens, line breaks, other country codes, extensions or non-ASCII digits. AI-05
    /// publishes the same rule as the pattern <c>^\+52[2-9][0-9]{9}$</c>.
    /// </summary>
    public static bool IsPhoneE164(string? value)
    {
        if (value is not { Length: PhoneE164Length } ||
            !value.StartsWith(VoicePhoneNumber.CountryPrefix, StringComparison.Ordinal) ||
            value[3] is < '2' or > '9')
        {
            return false;
        }

        for (var index = 4; index < PhoneE164Length; index++)
        {
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The configured dispatch contacts, read from the server configuration only (no tenant data).</summary>
public interface IDriverDispatchContactDirectory
{
    /// <summary>
    /// The E.164 dispatch number configured for <paramref name="organizationId"/>; null when that organization has
    /// none. Never another organization's number.
    /// </summary>
    string? FindPhoneE164(Guid organizationId);
}
