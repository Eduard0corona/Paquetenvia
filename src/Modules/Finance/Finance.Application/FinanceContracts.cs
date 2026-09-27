namespace Finance.Application;

public enum FinanceConflictCode
{
    InvalidRequest,
    IdempotencyConflict,
    InconsistentReplayEvidence,
    CodNotExpected,
    CodAmountMismatch,
    CodAlreadyRecorded,
    CodStateConflict,
    OrderStateConflict,
    ConcurrencyConflict,
}

public sealed class FinanceConflictException(FinanceConflictCode code, Exception? inner = null)
    : Exception("The finance operation conflicts with current state.", inner)
{
    public FinanceConflictCode Code { get; } = code;
}

public sealed class FinanceForbiddenException : Exception;

public sealed class FinanceNotFoundException : Exception;

/// <summary>
/// Finance cannot serve the request at all: the provider is disabled or its data store is unavailable.
/// This is missing capability of the system, never a statement about the actor or the resource, so it is
/// always reported as 503 and never as 403 or 409.
/// </summary>
public sealed class FinanceUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record FinanceAuthorizationContext(
    string? ActiveRole,
    bool UserActive,
    bool MembershipActive,
    bool MfaSatisfied,
    bool HasMatchingDriverAssignment);

/// <summary>
/// FIN-001 capabilities. Recording a collection is an operational act a driver holding custody may
/// perform; reconciling it is a control act that closes the cash position, so the collecting driver may
/// never reconcile their own collection. Margin data is dispatcher/admin only.
/// FINANCE-COD-RECONCILIATION (owner decision 2026-09-27): FINANCE reconciles collected COD and does
/// nothing else here. It never records a collection, never reads margins and never creates or changes
/// an order; reconciling touches only the COD record and the order's cash position.
/// </summary>
public static class FinanceAuthorizationPolicy
{
    public static bool CanReadFinancials(FinanceAuthorizationContext context) =>
        Active(context) && context.ActiveRole is "PLATFORM_ADMIN" or "DISPATCHER" && Mfa(context);

    public static bool CanRecordCod(FinanceAuthorizationContext context) =>
        Active(context) &&
        context.ActiveRole switch
        {
            "PLATFORM_ADMIN" => context.MfaSatisfied,
            "DISPATCHER" => true,
            "DRIVER" => context.HasMatchingDriverAssignment,
            _ => false,
        };

    /// <summary>
    /// The part of <see cref="CanRecordCod"/> that cannot go stale while a request waits for the order lock:
    /// user and membership status, role and MFA. A DRIVER's assignment can change during that wait, so it is
    /// never decided here; <see cref="CanRecordCod"/> decides it from the assignment observed under the lock.
    /// </summary>
    public static bool MayAttemptRecordCod(FinanceAuthorizationContext context) =>
        CanRecordCod(context with { HasMatchingDriverAssignment = true });

    public static bool CanReconcileCod(FinanceAuthorizationContext context) =>
        Active(context) && context.ActiveRole is "PLATFORM_ADMIN" or "DISPATCHER" or "FINANCE" && Mfa(context);

    private static bool Active(FinanceAuthorizationContext context) =>
        context.UserActive && context.MembershipActive;

    private static bool Mfa(FinanceAuthorizationContext context) =>
        context.ActiveRole != "PLATFORM_ADMIN" || context.MfaSatisfied;
}
