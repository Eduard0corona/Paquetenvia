using Organizations.Application.Session;
using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints.Authorization;

/// <summary>
/// The endpoint side of <see cref="TenantCapability"/>. An endpoint calls it after pure request-shape validation
/// and before its module service opens a transaction, so the order is shape validation, then capability, then
/// persisted state; the UI is never the barrier.
/// </summary>
public static class TenantCapabilityGate
{
    /// <summary>Problem-details <c>code</c> of a 403 whose only unmet requirement is MFA (AUTH-001-MFA-STEP-UP).</summary>
    public const string MfaRequiredCode = "MFA_REQUIRED";

    /// <summary>
    /// Null when the session holds the capability in the selected organization; otherwise the AI-05
    /// <c>Forbidden</c> response: a generic 403 problem, or one carrying <c>code: MFA_REQUIRED</c> when a
    /// second factor is the only thing missing.
    /// </summary>
    public static IResult? Deny(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        TenantCapability capability)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(capability);
        var decision = tenantContext.IsSelected
            ? capability.Evaluate(session, tenantContext.OrganizationId)
            : TenantCapabilityDecision.Forbidden;
        return decision switch
        {
            TenantCapabilityDecision.Allowed => null,
            TenantCapabilityDecision.MfaRequired => Forbidden(MfaRequiredCode),
            _ => Forbidden(null),
        };
    }

    /// <summary>
    /// The 403 for a refusal a module service decided itself, capability first inside its own transaction.
    /// It carries <c>MFA_REQUIRED</c> only when the session proves a second factor is the only thing missing:
    /// every role the actor holds that the operation admits needs MFA and the session has none. Any narrower
    /// refusal (a DRIVER without the assignment, for example) stays the generic 403.
    /// </summary>
    public static IResult Refused(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        TenantCapability capability) =>
        Deny(session, tenantContext, capability) ?? Forbidden();

    /// <summary>The uniform 403 problem; <paramref name="code"/> is only ever <see cref="MfaRequiredCode"/>.</summary>
    public static IResult Forbidden(string? code = null) => code is null
        ? Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.")
        : Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden.",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
