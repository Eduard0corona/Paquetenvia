using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;

namespace Orders.Endpoints;

/// <summary>
/// FINANCE-COD-RECONCILIATION (owner decision 2026-09-27): FINANCE never creates or modifies orders. An
/// actor whose only active role in the selected organization is FINANCE is refused order creation —
/// <c>createOrder</c> and <c>commitOrderCsv</c> — with the uniform 403 before any persisted state is read.
/// ORD-002 transitions already exclude FINANCE. The remaining D5 capability matrix rows for order creation
/// are aligned by their own task.
/// </summary>
public static class OrderCreationCapability
{
    public static bool Permits(IOrganizationRequestSession session, Guid organizationId) =>
        !FinanceRoleBoundary.IsFinanceOnly(session, organizationId);
}
