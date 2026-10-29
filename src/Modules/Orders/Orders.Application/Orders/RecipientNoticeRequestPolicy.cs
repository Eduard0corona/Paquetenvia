using Orders.Domain;

namespace Orders.Application.Orders;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (project owner: E4 "Los 4 del plan (Recommended)", E4-P3 "Al iniciar traslado
/// (Recommended)", E4-P4 "Al quedar confirmada (Recommended)" and E4-P5 "Sin motivo, con enlace (Recommended)"; AI-24
/// <c>recipient_notices.notices</c> and <c>producer</c>; AI-08 NTF-004): the committed ORD-002 transitions for which
/// Orders writes, in the same transaction, one <c>orders.recipient-notice-requested</c> outbox row, the way
/// <see cref="DispatchReactionRequestPolicy"/> asks Dispatch (D8-OUTBOX-LANE-DISPATCH, AI-13 §7.2). It adds no AI-13 §4
/// flow and no coordinator: the row is one more INSERT into the shared outbox.
/// </summary>
/// <remarks>
/// It decides from the source, the target and the attempt stage only, and whatever the switches say
/// (<c>producer.unconditional</c>): while recipient notices are off the Worker settles each row at once. The attempt stage
/// of a FAILED_ATTEMPT is the state the attempt failed from, the <c>attempt_stage</c> the ORD-002 order event records: a
/// delivery attempt (IN_TRANSIT or DELIVERING) tells the recipient, a failed pickup at the sender (AT_PICKUP) never does.
/// A unit test pins all 29 AI-04 transitions.
/// </remarks>
public static class RecipientNoticeRequestPolicy
{
    public const string Topic = "orders.recipient-notice-requested";
    public const string SchemaVersion = "recipient-notice-requested-v1";

    /// <summary>The notice wire names of <c>recipient-notice-requested-v1</c> (AI-24 <c>recipient_notices.notices.kind</c>).</summary>
    public const string TrackingLink = "TRACKING_LINK";
    public const string InTransit = "IN_TRANSIT";
    public const string DeliveryFailed = "DELIVERY_FAILED";
    public const string Delivered = "DELIVERED";

    /// <summary>The four notices, in AI-24 order.</summary>
    public static IReadOnlyList<string> Notices { get; } =
        Array.AsReadOnly(new[] { TrackingLink, InTransit, DeliveryFailed, Delivered });

    /// <summary>
    /// The notice a committed transition requests, or <see langword="null"/> when it requests none. For a FAILED_ATTEMPT
    /// the attempt stage is <paramref name="source"/>.
    /// </summary>
    public static string? NoticeFor(OrderStatus source, OrderStatus target) => (source, target) switch
    {
        (OrderStatus.Draft, OrderStatus.Confirmed) => TrackingLink,
        (OrderStatus.PickedUp, OrderStatus.InTransit) => InTransit,
        (OrderStatus.InTransit, OrderStatus.FailedAttempt) => DeliveryFailed,
        (OrderStatus.Delivering, OrderStatus.FailedAttempt) => DeliveryFailed,
        (OrderStatus.Delivering, OrderStatus.Delivered) => Delivered,
        _ => null,
    };

    public static bool Requires(OrderStatus source, OrderStatus target) => NoticeFor(source, target) is not null;
}
