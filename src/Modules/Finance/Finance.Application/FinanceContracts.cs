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

    public static bool CanReconcileCod(FinanceAuthorizationContext context) =>
        Active(context) && context.ActiveRole is "PLATFORM_ADMIN" or "DISPATCHER" && Mfa(context);

    private static bool Active(FinanceAuthorizationContext context) =>
        context.UserActive && context.MembershipActive;

    private static bool Mfa(FinanceAuthorizationContext context) =>
        context.ActiveRole != "PLATFORM_ADMIN" || context.MfaSatisfied;
}
