using Paqueteria.Application.Voice;

namespace Drivers.Application.Voice;

/// <summary>Why a recipient call is not available now (AI-05 <c>RecipientCallAvailability.reason</c>).</summary>
public static class RecipientCallReasons
{
    public const string VoiceCallsDisabled = "VOICE_CALLS_DISABLED";
    public const string OrderStateNotAllowed = "ORDER_STATE_NOT_ALLOWED";
    public const string RecipientPhoneUnavailable = "RECIPIENT_PHONE_UNAVAILABLE";
    public const string DriverPhoneRequired = "DRIVER_PHONE_REQUIRED";
    public const string RateLimited = "RATE_LIMITED";

    public static IReadOnlyList<string> All { get; } =
        [VoiceCallsDisabled, OrderStateNotAllowed, RecipientPhoneUnavailable, DriverPhoneRequired, RateLimited];
}

/// <summary>The 409 codes of <c>requestRecipientCall</c> (AI-05 <c>RecipientCallConflictProblem</c>).</summary>
public static class RecipientCallConflictCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string OrderStateNotAllowed = RecipientCallReasons.OrderStateNotAllowed;
    public const string RecipientPhoneUnavailable = RecipientCallReasons.RecipientPhoneUnavailable;
    public const string DriverPhoneRequired = RecipientCallReasons.DriverPhoneRequired;
    public const string DriverPhoneRejected = "DRIVER_PHONE_REJECTED";

    public static IReadOnlyList<string> All { get; } =
    [
        InvalidRequest, IdempotencyConflict, OrderStateNotAllowed, RecipientPhoneUnavailable, DriverPhoneRequired,
        DriverPhoneRejected,
    ];
}

/// <summary>Stored request statuses; the API returns REQUESTED, PLACED or UNCONFIRMED (a FAILED request is an error).</summary>
public static class RecipientCallStatuses
{
    public const string Requested = "REQUESTED";
    public const string Placed = "PLACED";
    public const string Unconfirmed = "UNCONFIRMED";
    public const string Failed = "FAILED";

    /// <summary>
    /// The statuses a provider status callback may promote to PLACED: the request is in flight, placed, or ambiguous
    /// (a timeout or a 5xx after which the call may exist). A FAILED request was answered as definitively failed (no
    /// call was created) and is never promoted, so it never starts counting toward the limits.
    /// </summary>
    public static IReadOnlyList<string> PromotableByCallback { get; } = [Requested, Placed, Unconfirmed];

    /// <summary>The statuses whose call may ring, so a key press may still arrive for them.</summary>
    public static IReadOnlyList<string> MayRing { get; } = [Requested, Placed, Unconfirmed];
}

/// <summary>
/// VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11: how far the key-press bridge of a call request got. Stored, never a number.
/// </summary>
public static class RecipientCallBridgeStages
{
    /// <summary>The driver pressed 1: the recipient is being dialed through the screening.</summary>
    public const string DriverConfirmed = "DRIVER_CONFIRMED";

    /// <summary>No 1 on the driver leg (no key, another key, a timeout or a voicemail): hung up, nobody dialed.</summary>
    public const string DriverNotConfirmed = "DRIVER_NOT_CONFIRMED";

    /// <summary>The recipient pressed 1: both legs connected.</summary>
    public const string RecipientConfirmed = "RECIPIENT_CONFIRMED";

    /// <summary>No 1 on the recipient leg (no key, another key, a timeout or a voicemail): hung up without bridging.</summary>
    public const string RecipientNotConfirmed = "RECIPIENT_NOT_CONFIRMED";

    /// <summary>The driver pressed 1 but the recipient's number could not be used: hung up, nobody dialed.</summary>
    public const string BridgeFailed = "BRIDGE_FAILED";

    public static IReadOnlyList<string> All { get; } =
        [DriverConfirmed, DriverNotConfirmed, RecipientConfirmed, RecipientNotConfirmed, BridgeFailed];
}

/// <param name="RetryAfterSeconds">Only with <see cref="RecipientCallReasons.RateLimited"/>: when a new request fits.</param>
public sealed record RecipientCallAvailabilityResult(bool Available, string? Reason, int? RetryAfterSeconds = null)
{
    public static RecipientCallAvailabilityResult Yes { get; } = new(true, null);

    public static RecipientCallAvailabilityResult No(string reason) => new(false, reason);

    public static RecipientCallAvailabilityResult RateLimited(TimeSpan retryAfter) =>
        new(false, RecipientCallReasons.RateLimited, Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
}

/// <summary>
/// The answer to the driver's key press: the recipient's number to dial (only in memory, only for the provider's
/// TwiML), or <see langword="null"/> to hang up without dialing anyone.
/// </summary>
public sealed record RecipientCallBridgeDecision(VoicePhoneNumber? Recipient)
{
    public static RecipientCallBridgeDecision Hangup { get; } = new((VoicePhoneNumber?)null);

    public override string ToString() => Recipient is null ? "RecipientCallBridgeDecision { Hangup }" : "RecipientCallBridgeDecision { Bridge }";
}

public sealed record RequestRecipientCallCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string IdempotencyKey,
    string? RequestId);

/// <param name="Status">REQUESTED (in progress), PLACED or UNCONFIRMED.</param>
public sealed record RecipientCallRequestResult(Guid CallRequestId, string Status);

/// <summary>
/// VOICE-001: the driver asks the platform to call them and bridge them with the delivery recipient. Phones are
/// decrypted only in memory inside the request and are never returned, logged, stored in clear or audited.
/// </summary>
public interface IRecipientCallService
{
    Task<RecipientCallAvailabilityResult> GetAvailabilityAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);

    Task<RecipientCallRequestResult> RequestAsync(
        RequestRecipientCallCommand command,
        CancellationToken cancellationToken);

    /// <summary>The provider's final report (status callback); returns whether a stored request was updated.</summary>
    Task<bool> RecordStatusAsync(VoiceCallStatusReport report, CancellationToken cancellationToken);

    /// <summary>
    /// VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11, the driver leg: a first, timely 1 on a call request that may ring returns
    /// the recipient's number to dial; anything else hangs up and records the outcome. Never throws for the provider.
    /// </summary>
    Task<RecipientCallBridgeDecision> ConfirmDriverAsync(VoiceKeypress keypress, CancellationToken cancellationToken);

    /// <summary>The recipient leg: true connects both legs; anything else hangs up and records the outcome.</summary>
    Task<bool> ConfirmRecipientAsync(VoiceKeypress keypress, CancellationToken cancellationToken);
}

/// <summary>The actor holds no ACTIVE driver profile in the selected organization (403).</summary>
public sealed class RecipientCallForbiddenException()
    : Exception("The actor has no active driver profile in the selected organization.");

/// <summary>Unknown order, another tenant's order or not the driver's own current assignment: the uniform 404.</summary>
public sealed class RecipientCallNotFoundException() : Exception("The stop is not available.");

public sealed class RecipientCallConflictException(string code) : Exception("The recipient call was refused.")
{
    public string Code { get; } = code;
}

public sealed class RecipientCallRateLimitedException(TimeSpan retryAfter) : Exception("Too many recipient calls.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>The bridge is disabled or unavailable, or the store failed (503).</summary>
public sealed class RecipientCallUnavailableException(Exception? innerException = null)
    : Exception("The recipient call cannot be placed now.", innerException);
