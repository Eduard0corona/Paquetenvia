using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Authorization;

/// <summary>
/// Grants that admit a role to an AI-05 operation only for one exact request shape, on top of the operation's
/// <see cref="TenantCapabilities"/> row. They are kept out of <see cref="TenantCapabilities.All"/> because they never
/// widen the operation as a whole: a caller admitted only here is refused every other shape of the operation.
/// </summary>
public static class TenantCapabilityRefinements
{
    /// <summary>
    /// FIN-PENDING-COD-LIST-FINANCE-2026-10-02 (literal "finanzas sí ve la lista"): FINANCE members with a satisfied
    /// MFA challenge may call listOrders only with <c>cod_pending_reconciliation=true</c> (AI-05 x-capability-matrix
    /// cod_pending_reconciliation_filter). Without MFA the refusal is <c>MFA_REQUIRED</c>, as on the other finance
    /// reads; every other listOrders call by FINANCE keeps the uniform 403.
    /// </summary>
    public static readonly TenantCapability ListOrdersCodPendingReconciliationOnly =
        new("listOrders", [new(OrganizationRole.Finance, true)]);
}
