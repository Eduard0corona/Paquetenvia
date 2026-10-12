using Orders.Domain;

namespace Orders.Application.Orders;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10 (project owner: "Sí, que se cierre sola"): the only ORD-002 edge the system actor may
/// take is DELIVERED -> CLOSED, and only through the same transition, guards and writes as a manual close. The system
/// actor has no user: the order event and the audit row carry no actor and the reason below says who closed it.
/// </summary>
public static class OrderSystemTransitionPolicy
{
    /// <summary>The reason recorded in the event and audit payload of every automatic close.</summary>
    public const string AutoCloseReason =
        "Cierre automático: entregada, sin incidencias abiertas y con el cobro conciliado (si había cobro contra entrega)";

    /// <summary>
    /// Prefix of the per-attempt idempotency key: a fresh random key per attempt, so an automatic close never replays
    /// a stored response; the state machine and the order row lock are what make the job idempotent.
    /// </summary>
    public const string IdempotencyKeyPrefix = "ord-auto-close-";

    public static bool IsAllowed(OrderStatus source, OrderStatus target) =>
        source == OrderStatus.Delivered && target == OrderStatus.Closed;
}

/// <summary>An automatic close of one order the owner organization owns, at the version the job read under RLS.</summary>
public sealed record SystemCloseOrderCommand(Guid OwnerOrganizationId, Guid OrderId, int ExpectedVersion);

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: DELIVERED -> CLOSED as the system actor, inside one tenant transaction of the owner
/// organization only. Rejections are the same <see cref="OrderTransitionConflictException"/> a manual close receives.
/// </summary>
public interface IOrderSystemTransitionService
{
    Task<OrderResult> CloseDeliveredAsync(SystemCloseOrderCommand command, CancellationToken cancellationToken);
}
