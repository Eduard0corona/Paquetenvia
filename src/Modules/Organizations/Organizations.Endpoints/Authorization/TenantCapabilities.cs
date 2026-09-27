using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Authorization;

/// <summary>
/// The server-side capability of every implemented AI-05 tenant operation. The first block is the
/// D5-CAPABILITY-MATRIX (project owner, 2026-09-26) as AI-05 <c>x-capability-matrix</c> publishes it,
/// with the FINANCE rows of FINANCE-COD-RECONCILIATION and FINANCE-COD-MFA-2026-09-27; roles it does not list
/// receive the uniform 403 before any resource access. The second block restates, unchanged, the rules each
/// operation outside the matrix already enforces inside its module service ("each operation keeps its current
/// enforced rules"), so a refusal whose only missing requirement is a second factor is reported as
/// <c>MFA_REQUIRED</c>.
/// </summary>
/// <remarks>
/// PLATFORM_ADMIN keeps the MFA requirement exactly where an operation already demanded it; DISPATCHER never
/// needs MFA (owner decision); FINANCE needs MFA for financials and COD reconciliation.
/// </remarks>
public static class TenantCapabilities
{
    // D5-CAPABILITY-MATRIX: orders, quotes, locations and CSV.
    public static readonly TenantCapability CreateQuote = Create("createQuote", Dispatcher, PlatformAdmin);
    public static readonly TenantCapability GetQuote = Create("getQuote", Dispatcher, PlatformAdmin, Viewer);
    public static readonly TenantCapability CreateOrder = Create("createOrder", Dispatcher, PlatformAdmin);
    public static readonly TenantCapability ListOrders = Create("listOrders", Dispatcher, PlatformAdmin, Viewer);
    public static readonly TenantCapability GetOrder = Create("getOrder", Dispatcher, PlatformAdmin, Viewer);
    public static readonly TenantCapability PreviewOrderCsv = Create("previewOrderCsv", Dispatcher, PlatformAdmin);
    public static readonly TenantCapability CommitOrderCsv = Create("commitOrderCsv", Dispatcher, PlatformAdmin);
    public static readonly TenantCapability ListCities = Create("listCities", Dispatcher, PlatformAdmin, Viewer);
    public static readonly TenantCapability ListServiceAreas =
        Create("listServiceAreas", Dispatcher, PlatformAdmin, Viewer);
    public static readonly TenantCapability ListOperatingZones =
        Create("listOperatingZones", Dispatcher, PlatformAdmin, Viewer);

    /// <summary>
    /// The matrix admits VIEWER, but only without exact coordinates, and the AI-05 <c>Location</c> schema
    /// requires exact <c>lat</c>/<c>lng</c> while no coarse precision is decided (ADR-032 leaves the precision
    /// threshold open under GATE-007). VIEWER is therefore withheld and fails closed until that decision
    /// exists; see <see cref="WithheldPendingDecision"/>.
    /// </summary>
    public static readonly TenantCapability ListLocations = Create("listLocations", Dispatcher, PlatformAdmin);

    public static readonly TenantCapability CreateLocation = Create("createLocation", Dispatcher, PlatformAdmin);

    // D5-CAPABILITY-MATRIX: a DRIVER (OWN or EXTERNAL) sees only its own stops and offers.
    public static readonly TenantCapability ListMyStops = Create("listMyStops", Driver);
    public static readonly TenantCapability ListMyEligibleExternalOffers =
        Create("listMyEligibleExternalOffers", Driver);

    // D5-CAPABILITY-MATRIX: FINANCE is limited to settlements and finance.
    public static readonly TenantCapability CreateSettlement = Settlement("createSettlement");
    public static readonly TenantCapability ListSettlements = Settlement("listSettlements");
    public static readonly TenantCapability GetSettlement = Settlement("getSettlement");
    public static readonly TenantCapability AddSettlementAdjustment = Settlement("addSettlementAdjustment");
    public static readonly TenantCapability ApproveSettlement = SettlementWithMfa("approveSettlement");
    public static readonly TenantCapability MarkSettlementPaid = SettlementWithMfa("markSettlementPaid");
    public static readonly TenantCapability VoidSettlement = Settlement("voidSettlement");
    public static readonly TenantCapability ExportSettlementCsv = Settlement("exportSettlementCsv");

    // x-capability-matrix finance_operations (FINANCE-COD-RECONCILIATION, FINANCE-COD-MFA-2026-09-27).
    public static readonly TenantCapability GetOrderFinancials = FinanceControl("getOrderFinancials");
    public static readonly TenantCapability GetRouteFinancials = FinanceControl("getRouteFinancials");
    public static readonly TenantCapability ReconcileCod = FinanceControl("reconcileCod");

    // Operations outside the matrix keep the rule their module service already enforces, capability first.
    public static readonly TenantCapability TransitionOrder =
        Create("transitionOrder", Dispatcher, PlatformAdminMfa, Driver);
    public static readonly TenantCapability AssignDriver = Create("assignDriver", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability CreateExternalOffer =
        Create("createExternalOffer", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability AcceptExternalOffer = Create("acceptExternalOffer", Driver);
    public static readonly TenantCapability CreateRoute = Create("createRoute", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability AddRouteStop = Create("addRouteStop", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability RemoveRouteStop = Create("removeRouteStop", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability ReorderRouteStops =
        Create("reorderRouteStops", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability CreateProofUploadSession =
        Create("createProofUploadSession", Dispatcher, PlatformAdminMfa, Driver);
    public static readonly TenantCapability FinalizeProof = Create("finalizeProof", Dispatcher, PlatformAdminMfa, Driver);
    public static readonly TenantCapability OpenIncident = Create("openIncident", Dispatcher, PlatformAdminMfa, Driver);
    public static readonly TenantCapability ResolveIncident = Create("resolveIncident", Dispatcher, PlatformAdminMfa);
    public static readonly TenantCapability RecordCodCollection =
        Create("recordCodCollection", Dispatcher, PlatformAdminMfa, Driver);

    /// <summary>
    /// Roles the D5 matrix admits that the server still refuses, each with the open decision that keeps it
    /// closed. The contract test compares the catalog plus these entries against AI-05.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<OrganizationRole>> WithheldPendingDecision { get; } =
        new Dictionary<string, IReadOnlyList<OrganizationRole>>(StringComparer.Ordinal)
        {
            ["listLocations"] = [OrganizationRole.Viewer],
        };

    /// <summary>Every capability above, keyed by AI-05 operationId.</summary>
    public static IReadOnlyDictionary<string, TenantCapability> All { get; } = typeof(TenantCapabilities)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(TenantCapability))
        .Select(field => (TenantCapability)field.GetValue(null)!)
        .ToDictionary(capability => capability.OperationId, StringComparer.Ordinal);

    private static TenantCapabilityGrant Dispatcher => new(OrganizationRole.Dispatcher, false);

    private static TenantCapabilityGrant PlatformAdmin => new(OrganizationRole.PlatformAdmin, false);

    private static TenantCapabilityGrant PlatformAdminMfa => new(OrganizationRole.PlatformAdmin, true);

    private static TenantCapabilityGrant Viewer => new(OrganizationRole.Viewer, false);

    private static TenantCapabilityGrant Driver => new(OrganizationRole.Driver, false);

    private static TenantCapability Create(string operationId, params TenantCapabilityGrant[] grants) =>
        new(operationId, grants);

    /// <summary>SET-001: FINANCE, and PLATFORM_ADMIN with a satisfied MFA challenge.</summary>
    private static TenantCapability Settlement(string operationId) =>
        Create(operationId, new(OrganizationRole.Finance, false), PlatformAdminMfa);

    /// <summary>
    /// D7-SETTLEMENT-MFA (AI-05 x-pilot-contract-deltas, D7-SETTLEMENT-RULES): approving and paying a settlement
    /// require a satisfied MFA challenge for every permitted role, FINANCE included.
    /// </summary>
    private static TenantCapability SettlementWithMfa(string operationId) =>
        Create(operationId, new(OrganizationRole.Finance, true), PlatformAdminMfa);

    /// <summary>FIN-001 control reads and reconciliation: DISPATCHER, and PLATFORM_ADMIN or FINANCE with MFA.</summary>
    private static TenantCapability FinanceControl(string operationId) =>
        Create(operationId, Dispatcher, PlatformAdminMfa, new(OrganizationRole.Finance, true));
}
