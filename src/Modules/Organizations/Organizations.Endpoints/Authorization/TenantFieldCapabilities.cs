using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Authorization;

/// <summary>
/// Capabilities that guard one optional field of an AI-05 operation rather than the operation itself. They are
/// published in their own AI-05 <c>x-capability-matrix</c> section, are not part of <see cref="TenantCapabilities.All"/>
/// (which is keyed by operationId, one capability per operation) and are checked by the endpoint, like the
/// operation capability, before the module service reads or writes any persisted state.
/// </summary>
public static class TenantFieldCapabilities
{
    /// <summary>
    /// x-capability-matrix <c>low_price_authorization</c> (LOW-PRICE-MANUAL-AUTH-2026-10-02, project owner,
    /// 2026-10-02: "Sí, con autorización"): only a DISPATCHER, or a PLATFORM_ADMIN with a satisfied MFA challenge,
    /// may send <c>low_price_authorization</c> on createQuote. Any other caller of createQuote that sends it receives
    /// 403 (MFA_REQUIRED when the second factor is the only thing missing).
    /// </summary>
    public static readonly TenantCapability CreateQuoteLowPriceAuthorization = new(
        "createQuote",
        [
            new TenantCapabilityGrant(OrganizationRole.Dispatcher, false),
            new TenantCapabilityGrant(OrganizationRole.PlatformAdmin, true),
        ]);
}
