using Orders.Application.Tracking;

namespace Notifications.Application.RecipientNotices;

/// <summary>NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.notices</c>): the four recipient notices.</summary>
public enum RecipientNoticeKind
{
    TrackingLink,
    InTransit,
    DeliveryFailed,
    Delivered,
}

/// <summary>The notice wire names of the request payload and their template keys, version 1.</summary>
public static class RecipientNoticeCatalog
{
    public const int TemplateVersion = 1;

    public static IReadOnlyList<RecipientNoticeKind> All { get; } = Array.AsReadOnly(new[]
    {
        RecipientNoticeKind.TrackingLink,
        RecipientNoticeKind.InTransit,
        RecipientNoticeKind.DeliveryFailed,
        RecipientNoticeKind.Delivered,
    });

    public static string Notice(RecipientNoticeKind kind) => kind switch
    {
        RecipientNoticeKind.TrackingLink => "TRACKING_LINK",
        RecipientNoticeKind.InTransit => "IN_TRANSIT",
        RecipientNoticeKind.DeliveryFailed => "DELIVERY_FAILED",
        RecipientNoticeKind.Delivered => "DELIVERED",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recipient notice."),
    };

    public static string TemplateKey(RecipientNoticeKind kind) => kind switch
    {
        RecipientNoticeKind.TrackingLink => "recipient.tracking-link",
        RecipientNoticeKind.InTransit => "recipient.in-transit",
        RecipientNoticeKind.DeliveryFailed => "recipient.delivery-failed",
        RecipientNoticeKind.Delivered => "recipient.delivered",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recipient notice."),
    };

    public static bool TryFromNotice(string? notice, out RecipientNoticeKind kind) =>
        TryFind(value => Notice(value) == notice, out kind);

    public static bool TryFromTemplateKey(string? templateKey, out RecipientNoticeKind kind) =>
        TryFind(value => TemplateKey(value) == templateKey, out kind);

    private static bool TryFind(Func<RecipientNoticeKind, bool> predicate, out RecipientNoticeKind kind)
    {
        foreach (var value in All)
        {
            if (predicate(value))
            {
                kind = value;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>
/// The lane's own closed codes for recipient notices (AI-24 <c>recipient_notices</c>; the NTF-003 SQL allowlist): the
/// outcomes of a request row and the reasons a notice is cancelled or cannot be sent. Never a provider message.
/// </summary>
public static class RecipientNoticeCodes
{
    public const string SourceExpanded = "SOURCE_EXPANDED";
    public const string Disabled = "RECIPIENT_NOTICES_DISABLED";
    public const string NoEligibleChannel = "NO_ELIGIBLE_CHANNEL";
    public const string Stale = "NOTICE_STALE";
    public const string Superseded = "NOTICE_SUPERSEDED";
    public const string LimitReached = "NOTICE_LIMIT_REACHED";
    public const string OptedOut = "RECIPIENT_OPTED_OUT";
    public const string TrackingLinkUnavailable = "TRACKING_LINK_UNAVAILABLE";

    /// <summary>The settle of a request row that reached a decision: PROCESSED (the rest end DEAD).</summary>
    public static bool IsProcessedSourceCode(string code) =>
        code is SourceExpanded or Disabled or NoEligibleChannel or Stale;
}

/// <summary>
/// AI-24 <c>recipient_notices.notices[].valid_at_send</c>: whether a notice still applies when it is sent, from the
/// current internal status of its order. <see cref="PublicOrderStatusPolicy.Map"/> throws on an unmapped status (AI-01
/// rule 12), so an unknown status never sends anything.
/// </summary>
public static class RecipientNoticeValidity
{
    public static bool IsValidAtSend(RecipientNoticeKind kind, string internalStatus, PublicOrderStatusPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var publicStatus = policy.Map(internalStatus);
        return kind switch
        {
            // The public status of the order is not DELIVERED, RETURNED or CANCELLED.
            RecipientNoticeKind.TrackingLink => !PublicTrackingLinkPolicy.IsFinal(publicStatus),
            // The order is still IN_TRANSIT.
            RecipientNoticeKind.InTransit => internalStatus == "IN_TRANSIT",
            // The order is neither DELIVERING nor in the public status DELIVERED.
            RecipientNoticeKind.DeliveryFailed =>
                internalStatus != "DELIVERING" && publicStatus != PublicOrderStatus.Delivered,
            // The public status of the order is DELIVERED.
            RecipientNoticeKind.Delivered => publicStatus == PublicOrderStatus.Delivered,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recipient notice."),
        };
    }
}

/// <summary>
/// AI-24 <c>recipient_notices.quiet_hours</c> (E4-P10, "8:00 a 21:00 (Recommended)"): messages leave only from the
/// window start, inclusive, to its end, exclusive, in America/Mazatlan wall-clock time. 08:00:00 is open and 21:00:00
/// is closed (<c>quiet_hours.window</c>). The time zone conversion happens in SQL (<c>AT TIME ZONE</c>), so the container
/// needs no time-zone data; this type decides on the local wall-clock values only.
/// </summary>
public sealed class RecipientNoticeQuietHours
{
    public RecipientNoticeQuietHours(TimeOnly start, TimeOnly end)
    {
        if (start >= end)
        {
            throw new ArgumentException("The quiet-hours window must start before it ends.");
        }

        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public bool IsOpen(TimeOnly localTime) => localTime >= Start && localTime < End;

    public bool IsOpen(DateTime local) => IsOpen(TimeOnly.FromDateTime(local));

    /// <summary>The local wall-clock moment a notice at <paramref name="local"/> may leave: itself when the window is
    /// open, the start of the same day before it, or the start of the next day after it.</summary>
    public DateTime NextOpening(DateTime local)
    {
        var time = TimeOnly.FromDateTime(local);
        if (IsOpen(time))
        {
            return local;
        }

        var day = DateOnly.FromDateTime(local);
        return (time < Start ? day : day.AddDays(1)).ToDateTime(Start, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// AI-24 <c>recipient_notices.staleness.bound</c>: the age of a notice "counted from occurred_at without the
    /// quiet-hours wait" - from the first moment it could leave (<see cref="NextOpening"/> of its occurrence) to now,
    /// never negative.
    /// </summary>
    public TimeSpan AgeWithoutWait(DateTime occurredLocal, DateTime nowLocal)
    {
        var age = nowLocal - NextOpening(occurredLocal);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
