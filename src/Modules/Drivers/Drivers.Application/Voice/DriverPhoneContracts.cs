using Paqueteria.Application.Contacts;

namespace Drivers.Application.Voice;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11: the driver's own mobile number, used only to connect delivery calls through the
/// masked bridge and never shown to recipients. It is collected only while the bridge is enabled, with the consent
/// text <see cref="DriverPhoneConsent.CurrentVersion"/> the PWA shows, and stored protected (ADP-001 envelope).
/// </summary>
public static class DriverPhoneConsent
{
    /// <summary>The version of the consent text in the PWA "Cuenta" area; the request must name it exactly.</summary>
    public const string CurrentVersion = "VOICE-001-CONSENT-V1";
}

public static class DriverPhonePolicy
{
    /// <summary>
    /// The ORD-PHONE-PLUS52-LOCATIONS-2026-10-03 input rules (ten Mexican digits, optional leading <c>+52</c>, spaces
    /// and hyphens removed) plus a dialable first digit (2-9): Mexican numbers never start with 0 or 1.
    /// </summary>
    public static bool TryNormalize(string? value, out string digits)
    {
        if (MexicanPhonePolicy.TryNormalize(value, out digits) && digits[0] is >= '2' and <= '9')
        {
            return true;
        }

        digits = string.Empty;
        return false;
    }
}

/// <param name="VoiceCallsEnabled">Whether the bridge is enabled in this deployment; the PWA hides the form otherwise.</param>
/// <param name="Registered">Whether a number is stored; the number itself is never returned.</param>
public sealed record DriverPhoneStatusResult(
    bool VoiceCallsEnabled,
    bool Registered,
    string? ConsentVersion,
    DateTimeOffset? ConsentedAt);

/// <param name="PhoneDigits">The ten normalized national digits; personal data, never part of <see cref="ToString"/>.</param>
public sealed record RegisterDriverPhoneCommand(
    Guid ActorId,
    Guid OrganizationId,
    string PhoneDigits,
    string ConsentVersion,
    string? RequestId)
{
    public override string ToString() =>
        $"RegisterDriverPhoneCommand {{ ActorId = {ActorId:D}, OrganizationId = {OrganizationId:D}, ConsentVersion = {ConsentVersion} }}";
}

public interface IDriverPhoneService
{
    Task<DriverPhoneStatusResult> GetAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken);

    Task<DriverPhoneStatusResult> RegisterAsync(RegisterDriverPhoneCommand command, CancellationToken cancellationToken);

    Task<DriverPhoneStatusResult> RemoveAsync(
        Guid actorId,
        Guid organizationId,
        string? requestId,
        CancellationToken cancellationToken);
}

/// <summary>The actor holds no ACTIVE driver profile in the selected organization (403).</summary>
public sealed class DriverPhoneForbiddenException()
    : Exception("The actor has no active driver profile in the selected organization.");

/// <summary>The bridge is disabled, the protector is unavailable or the store failed (503); nothing was written.</summary>
public sealed class DriverPhoneUnavailableException(Exception? innerException = null)
    : Exception("The driver phone cannot be processed now.", innerException);
