using Drivers.Application.Voice;
using Paqueteria.Application.Voice;

namespace Drivers.Infrastructure.Voice;

/// <summary><c>Drivers:Provider=Disabled</c>: no store, so every VOICE-001 operation is unavailable (503).</summary>
public sealed class DisabledDriverPhoneService : IDriverPhoneService
{
    public Task<DriverPhoneStatusResult> GetAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) =>
        Task.FromException<DriverPhoneStatusResult>(new DriverPhoneUnavailableException());

    public Task<DriverPhoneStatusResult> RegisterAsync(RegisterDriverPhoneCommand command, CancellationToken cancellationToken) =>
        Task.FromException<DriverPhoneStatusResult>(new DriverPhoneUnavailableException());

    public Task<DriverPhoneStatusResult> RemoveAsync(
        Guid actorId,
        Guid organizationId,
        string? requestId,
        CancellationToken cancellationToken) =>
        Task.FromException<DriverPhoneStatusResult>(new DriverPhoneUnavailableException());
}

public sealed class DisabledRecipientCallService : IRecipientCallService
{
    public Task<RecipientCallAvailabilityResult> GetAvailabilityAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken) =>
        Task.FromException<RecipientCallAvailabilityResult>(new RecipientCallUnavailableException());

    public Task<RecipientCallRequestResult> RequestAsync(
        RequestRecipientCallCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<RecipientCallRequestResult>(new RecipientCallUnavailableException());

    public Task<bool> RecordStatusAsync(VoiceCallStatusReport report, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>The bridge is disabled: no phone is protected or decrypted.</summary>
public sealed class DisabledDriverPhoneProtector : IDriverPhoneProtector
{
    public Task<ProtectedDriverPhone> ProtectAsync(
        Guid organizationId,
        Guid driverId,
        string digits,
        CancellationToken cancellationToken) =>
        Task.FromException<ProtectedDriverPhone>(new DriverPhoneUnavailableException());
}

public sealed class DisabledRecipientCallPhoneResolver : IRecipientCallPhoneResolver
{
    public Task<(VoicePhoneNumber Driver, VoicePhoneNumber Recipient)> ResolveAsync(
        RecipientCallProtectedPhones phones,
        CancellationToken cancellationToken) =>
        Task.FromException<(VoicePhoneNumber, VoicePhoneNumber)>(
            new RecipientCallPhoneException(RecipientCallPhoneProblem.ProtectionUnavailable));
}
