using Organizations.Endpoints.Authorization;
using Paqueteria.ContractTests.Support;
using Paqueteria.Domain.Tenancy;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// D5-CAPABILITY-MATRIX: the server-side capability catalog is exactly the AI-05 <c>x-capability-matrix</c>
/// (operations, finance_operations, the REG-ALLY-APPROVAL-PATH platform_operations and the REG-JOIN-ADDERS-MFA membership_operations), role for role, and every capability names a real AI-05 operation.
/// </summary>
public sealed class CapabilityMatrixContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static readonly YamlMappingNode Matrix = Contract.Mapping("x-capability-matrix");

    [Fact]
    public void The_matrix_is_the_decided_D5_matrix()
    {
        Assert.Equal("DECIDED", Matrix.Scalar("status"));
        Assert.StartsWith("D5-CAPABILITY-MATRIX", Matrix.Scalar("decision"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_matrix_operation_has_exactly_its_roles_server_side()
    {
        var published = Published();
        Assert.True(published.Count >= 24, $"Only {published.Count} matrix operations were read.");
        foreach (var (operationId, roles) in published)
        {
            Assert.True(
                TenantCapabilities.All.TryGetValue(operationId, out var capability),
                $"{operationId} has no server-side capability.");
            var enforced = capability!.Grants.Select(grant => ContractValue(grant.Role)).ToHashSet(StringComparer.Ordinal);
            Assert.True(
                roles.SetEquals(enforced),
                $"{operationId}: AI-05 admits [{string.Join(',', roles.Order())}], the server admits " +
                $"[{string.Join(',', enforced.Order())}].");
        }
    }

    /// <summary>
    /// D5-VIEWER-LOCATION-PRECISION-2026-09-27: AI-05 publishes the VIEWER precision on listLocations and the
    /// server applies exactly it; the implementation marker is IMPLEMENTED.
    /// </summary>
    [Fact]
    public void Viewer_location_precision_is_published_and_the_matrix_is_implemented()
    {
        Assert.StartsWith("IMPLEMENTED", Matrix.Scalar("implementation"), StringComparison.Ordinal);
        Assert.Contains("VIEWER reads never return exact coordinates", RulesText(), StringComparison.Ordinal);
        Assert.Contains("D5-VIEWER-LOCATION-PRECISION-2026-09-27", Matrix.Scalar("viewer_location_precision"), StringComparison.Ordinal);

        var listLocations = Contract.Mapping("paths").Mapping("/locations").Mapping("get");
        var precision = listLocations.Mapping("x-viewer-coordinate-precision");
        Assert.Equal("D5-VIEWER-LOCATION-PRECISION-2026-09-27", precision.Scalar("decision"));
        Assert.Equal("VIEWER", precision.Scalar("applies_to"));
        Assert.Equal(
            Locations.Endpoints.ViewerCoordinatePrecision.DecimalPlaces.ToString(System.Globalization.CultureInfo.InvariantCulture),
            precision.Scalar("decimal_places"));
        Assert.Equal(["lat", "lng"], precision.Sequence("fields").Children.Select(node => ((YamlScalarNode)node).Value));
        Assert.Equal(
            ["DISPATCHER", "PLATFORM_ADMIN"],
            precision.Sequence("exact_for").Children.Select(node => ((YamlScalarNode)node).Value));

        // The Location schema keeps lat and lng as required numbers: a VIEWER gets rounded values, not nulls.
        var location = Contract.Mapping("components").Mapping("schemas").Mapping("Location");
        var required = location.Sequence("required").Children.Select(node => ((YamlScalarNode)node).Value).ToArray();
        Assert.Contains("lat", required);
        Assert.Contains("lng", required);
    }

    [Theory]
    [InlineData(24.805, 24.81)]
    [InlineData(-107.395, -107.4)]
    [InlineData(-0.005, -0.01)]
    [InlineData(0.015, 0.02)]
    [InlineData(24.8049999, 24.8)]
    [InlineData(-0.004, 0)]
    [InlineData(179.996, 180)]
    [InlineData(-33.4489123, -33.45)]
    [InlineData(28.61, 28.61)]
    public void Viewer_coordinates_round_half_away_from_zero_on_the_decimal_value(double exact, double expected)
    {
        var rounded = Locations.Endpoints.ViewerCoordinatePrecision.Round(exact);
        Assert.Equal(expected, rounded);
        var serialized = System.Text.Json.JsonSerializer.Serialize(rounded);
        Assert.Matches(@"^-?\d+(\.\d{1,2})?$", serialized);
    }

    /// <summary>
    /// MFA: settlements need it for PLATFORM_ADMIN, and approve and pay for FINANCE too (D7-SETTLEMENT-MFA); the
    /// finance control operations need it for PLATFORM_ADMIN and FINANCE (FINANCE-COD-MFA-2026-09-27); DISPATCHER
    /// never needs it; the rest of the matrix never demanded MFA and keeps none.
    /// </summary>
    [Fact]
    public void Mfa_requirements_follow_the_published_rules()
    {
        Assert.Contains("PLATFORM_ADMIN keeps the existing MFA requirement", RulesText(), StringComparison.Ordinal);
        var financeScope = Matrix.Scalar("finance_scope");
        Assert.Contains("PLATFORM_ADMIN members with a satisfied MFA challenge", financeScope, StringComparison.Ordinal);
        Assert.Contains("FINANCE members with a satisfied MFA challenge", financeScope, StringComparison.Ordinal);

        // D7-SETTLEMENT-MFA: approve and pay need MFA for every permitted role, FINANCE included.
        var settlementMfa = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "D7-SETTLEMENT-MFA");
        Assert.Equal("DECIDED", settlementMfa.Scalar("status"));
        var mfaForEveryRole = new[] { "approveSettlement", "markSettlementPaid" };
        Assert.All(mfaForEveryRole, id => Assert.Contains(id, settlementMfa.Scalar("delta"), StringComparison.Ordinal));
        Assert.Contains("every permitted role, including", settlementMfa.Scalar("delta"), StringComparison.Ordinal);

        var financeOperations = OperationIds(Matrix.Mapping("finance_operations"));
        financeOperations.UnionWith(mfaForEveryRole);
        foreach (var (operationId, capability) in TenantCapabilities.All)
        {
            foreach (var grant in capability.Grants)
            {
                var expected = grant.Role switch
                {
                    OrganizationRole.Dispatcher or OrganizationRole.Driver or OrganizationRole.Viewer => false,
                    OrganizationRole.Finance => financeOperations.Contains(operationId),
                    OrganizationRole.PlatformAdmin => !IsMatrixOperationWithoutMfa(operationId),
                    // REG-JOIN-ADDERS-MFA: every administrator who adds, lists, renews or revokes needs MFA.
                    OrganizationRole.AllyAdmin or OrganizationRole.BusinessAdmin =>
                        OperationIds(Matrix.Mapping("membership_operations")).Contains(operationId),
                    _ => throw new InvalidOperationException($"{operationId} grants unexpected role {grant.Role}."),
                };
                Assert.True(
                    expected == grant.RequiresMfa,
                    $"{operationId}: {ContractValue(grant.Role)} RequiresMfa should be {expected}.");
            }
        }
    }

    /// <summary>
    /// TRK-002-ISSUE-ENDPOINT: D5 predates the tracking link operation, so AI-05 publishes its roles in their own
    /// section and names the owner decisions; TRK-002-NO-REVOCATION confirmed the roles ("Sí, con MFA") and removed
    /// revocation. It hands out a public bearer credential, so the server grants exactly the roles of assignDriver,
    /// createRoute and createExternalOffer: DISPATCHER without MFA and PLATFORM_ADMIN with MFA.
    /// </summary>
    [Fact]
    public void Tracking_link_operations_take_DISPATCHER_and_PLATFORM_ADMIN_with_MFA()
    {
        var decision = Matrix.Scalar("tracking_link_operations_decision");
        Assert.StartsWith("TRK-002-ISSUE-ENDPOINT", decision, StringComparison.Ordinal);
        Assert.Contains("TRK-002-NO-REVOCATION", decision, StringComparison.Ordinal);
        Assert.Contains("\"Sí, con MFA\"", decision, StringComparison.Ordinal);
        Assert.DoesNotContain("pending owner confirmation", decision, StringComparison.Ordinal);
        Assert.Contains("DISPATCHER members without MFA", decision, StringComparison.Ordinal);
        Assert.Contains("PLATFORM_ADMIN members with a satisfied MFA challenge", decision, StringComparison.Ordinal);
        var section = Matrix.Mapping("tracking_link_operations");
        Assert.Equal(["issueTrackingLink"], OperationIds(section).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            typeof(TenantCapabilities).GetFields(),
            field => field.Name.Contains("Revoke", StringComparison.Ordinal) &&
                field.Name.Contains("TrackingLink", StringComparison.Ordinal));
        foreach (var capability in new[] { TenantCapabilities.IssueTrackingLink })
        {
            foreach (var reference in new[]
                     {
                         TenantCapabilities.AssignDriver, TenantCapabilities.CreateRoute,
                         TenantCapabilities.CreateExternalOffer,
                     })
            {
                Assert.Equal(
                    reference.Grants.Select(grant => (grant.Role, grant.RequiresMfa)),
                    capability.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
            }

            Assert.Equal(
                [(OrganizationRole.Dispatcher, false), (OrganizationRole.PlatformAdmin, true)],
                capability.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        }
    }

    /// <summary>
    /// API-FIN-COD-VISIBILITY-2026-09-29: the listOrders COD pending filter is honored for the roles that hold both
    /// listOrders and getOrderFinancials; FINANCE's narrower grant (FIN-PENDING-COD-LIST-FINANCE-2026-10-02) is
    /// checked separately and never adds FINANCE to the listOrders row.
    /// </summary>
    [Fact]
    public void Cod_pending_filter_takes_the_intersection_of_listOrders_and_getOrderFinancials()
    {
        var decision = Matrix.Scalar("cod_pending_reconciliation_filter");
        Assert.StartsWith("API-FIN-COD-VISIBILITY-2026-09-29", decision, StringComparison.Ordinal);
        Assert.Contains("both listOrders and getOrderFinancials", decision, StringComparison.Ordinal);

        var published = Published();
        Assert.DoesNotContain("FINANCE", published["listOrders"]);
        var admitted = TenantCapabilities.ListOrders.Grants.Select(grant => grant.Role)
            .Intersect(TenantCapabilities.GetOrderFinancials.Grants.Select(grant => grant.Role))
            .ToArray();
        Assert.Equal([OrganizationRole.Dispatcher, OrganizationRole.PlatformAdmin], admitted);
        Assert.Contains(
            TenantCapabilities.GetOrderFinancials.Grants,
            grant => grant.Role == OrganizationRole.PlatformAdmin && grant.RequiresMfa);

        var listOrders = Contract.Mapping("paths").Mapping("/orders").Mapping("get");
        var parameter = listOrders.Sequence("parameters").Children.Cast<YamlMappingNode>()
            .Single(node => node.Children.ContainsKey(new YamlScalarNode("name")) &&
                node.Scalar("name") == "cod_pending_reconciliation");
        Assert.Equal("query", parameter.Scalar("in"));
        Assert.Equal("boolean", parameter.Mapping("schema").Scalar("type"));
        Assert.Contains("getOrderFinancials", parameter.Scalar("description"), StringComparison.Ordinal);
    }

    /// <summary>
    /// FIN-PENDING-COD-LIST-FINANCE-2026-10-02 ("finanzas sí ve la lista"): AI-05 publishes that FINANCE with MFA may
    /// call listOrders only with cod_pending_reconciliation=true, and the server grant is exactly that: one FINANCE
    /// grant needing MFA, a subset of getOrderFinancials, kept out of the listOrders capability row.
    /// </summary>
    [Fact]
    public void Finance_sees_only_the_cod_pending_list_with_mfa()
    {
        var decision = Matrix.Scalar("cod_pending_reconciliation_filter");
        Assert.Contains("FIN-PENDING-COD-LIST-FINANCE-2026-10-02", decision, StringComparison.Ordinal);
        Assert.Contains("finanzas sí ve la lista", decision, StringComparison.Ordinal);
        Assert.Contains(
            "FINANCE members with a satisfied MFA challenge may also call listOrders, only with cod_pending_reconciliation=true",
            decision,
            StringComparison.Ordinal);
        Assert.Contains("every other listOrders call by FINANCE stays 403", decision, StringComparison.Ordinal);
        Assert.Contains("FINANCE still never creates or modifies orders", decision, StringComparison.Ordinal);

        var refinement = TenantCapabilityRefinements.ListOrdersCodPendingReconciliationOnly;
        Assert.Equal("listOrders", refinement.OperationId);
        Assert.Equal([(OrganizationRole.Finance, true)], refinement.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        Assert.Contains(
            TenantCapabilities.GetOrderFinancials.Grants,
            grant => grant.Role == OrganizationRole.Finance && grant.RequiresMfa);
        Assert.Same(TenantCapabilities.ListOrders, TenantCapabilities.All["listOrders"]);
        Assert.DoesNotContain(TenantCapabilities.ListOrders.Grants, grant => grant.Role == OrganizationRole.Finance);
        foreach (var write in new[]
                 {
                     TenantCapabilities.CreateOrder, TenantCapabilities.PreviewOrderCsv, TenantCapabilities.CommitOrderCsv,
                     TenantCapabilities.TransitionOrder, TenantCapabilities.GetOrder,
                 })
        {
            Assert.DoesNotContain(write.Grants, grant => grant.Role == OrganizationRole.Finance);
        }

        var listOrders = Contract.Mapping("paths").Mapping("/orders").Mapping("get");
        Assert.Equal("capability-before-persisted-state", listOrders.Scalar("x-authorization-precedence"));
        Assert.Contains("FIN-PENDING-COD-LIST-FINANCE-2026-10-02", listOrders.Scalar("description"), StringComparison.Ordinal);
        var parameter = listOrders.Sequence("parameters").Children.Cast<YamlMappingNode>()
            .Single(node => node.Children.ContainsKey(new YamlScalarNode("name")) &&
                node.Scalar("name") == "cod_pending_reconciliation");
        Assert.Contains("FIN-PENDING-COD-LIST-FINANCE-2026-10-02", parameter.Scalar("description"), StringComparison.Ordinal);
    }

    /// <summary>
    /// API-INC-LIST-PROOFS-2026-09-29: AI-05 publishes who may open an incident exactly as the server already
    /// enforces it — the capability catalog and the SQL authorization of the incident service agree role for role —
    /// and the three incident desk reads admit exactly the resolveIncident grants, MFA included.
    /// </summary>
    [Fact]
    public void Incident_operations_publish_the_enforced_open_rule_and_reads_mirror_resolution()
    {
        var decision = Matrix.Scalar("incident_operations_decision");
        Assert.StartsWith("API-INC-LIST-PROOFS-2026-09-29", decision, StringComparison.Ordinal);
        Assert.Contains("rule the server already enforces", decision, StringComparison.Ordinal);
        Assert.Contains("ACCEPTED or ACTIVE assignment", decision, StringComparison.Ordinal);
        Assert.Equal(
            ["getIncident", "listIncidents", "listOrderProofs", "openIncident", "resolveIncident"],
            OperationIds(Matrix.Mapping("incident_operations")).Order(StringComparer.Ordinal));

        Assert.Equal(
            [(OrganizationRole.Dispatcher, false), (OrganizationRole.PlatformAdmin, true), (OrganizationRole.Driver, false)],
            TenantCapabilities.OpenIncident.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        foreach (var read in new[]
                 {
                     TenantCapabilities.ListIncidents, TenantCapabilities.GetIncident, TenantCapabilities.ListOrderProofs,
                 })
        {
            Assert.Equal(
                TenantCapabilities.ResolveIncident.Grants.Select(grant => (grant.Role, grant.RequiresMfa)),
                read.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        }

        // The SQL the incident service authorizes an opening with admits exactly these roles: a DISPATCHER, a
        // PLATFORM_ADMIN only with MFA, and a DRIVER only through an ACCEPTED or ACTIVE assignment of the order.
        var sql = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "src", "Modules", "Incidents", "Incidents.Infrastructure", "Incidents",
            "IncidentsSql.cs"));
        var start = sql.IndexOf("ReadAuthorizedOrderAsync(", StringComparison.Ordinal);
        var end = sql.IndexOf("AS authorized", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var authorization = sql[start..end];
        Assert.Equal(
            ["DISPATCHER", "DRIVER", "PLATFORM_ADMIN"],
            System.Text.RegularExpressions.Regex.Matches(authorization, "m\\.role='([A-Z_]+)'")
                .Select(match => match.Groups[1].Value)
                .Order(StringComparer.Ordinal));
        Assert.Contains("@mfa\n                  AND EXISTS", authorization.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("a.status IN ('ACCEPTED','ACTIVE')", authorization, StringComparison.Ordinal);
    }

    /// <summary>
    /// UI-PHASE2-DRIVER-PICKER-2026-10-05: AI-05 publishes the driver picker read in its own section, citing the owner
    /// literal, and the server grants exactly the assignDriver roles it serves: DISPATCHER without MFA and
    /// PLATFORM_ADMIN with MFA.
    /// </summary>
    [Fact]
    public void Assignable_driver_operations_mirror_assignDriver()
    {
        var decision = Matrix.Scalar("assignable_driver_operations_decision");
        Assert.StartsWith("UI-PHASE2-DRIVER-PICKER-2026-10-05", decision, StringComparison.Ordinal);
        Assert.Contains("\"Sí a los 5 grupos de estado, avanza con la fase 2\"", decision, StringComparison.Ordinal);
        Assert.Contains("exactly the roles of assignDriver", decision, StringComparison.Ordinal);
        Assert.Equal(
            ["listAssignableDrivers"],
            OperationIds(Matrix.Mapping("assignable_driver_operations")).Order(StringComparer.Ordinal));
        Assert.Equal(
            TenantCapabilities.AssignDriver.Grants.Select(grant => (grant.Role, grant.RequiresMfa)),
            TenantCapabilities.ListAssignableDrivers.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        Assert.Equal(
            [(OrganizationRole.Dispatcher, false), (OrganizationRole.PlatformAdmin, true)],
            TenantCapabilities.ListAssignableDrivers.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
    }

    /// <summary>
    /// UI-PHASE2-QUEUE-COUNTS-2026-10-05: AI-05 publishes the work-queue counts read in its own section, citing the
    /// owner literal, and the server grants exactly the roles of the operations dashboard it feeds
    /// (OperationsRolePolicy): DISPATCHER without MFA and PLATFORM_ADMIN with MFA; VIEWER never.
    /// </summary>
    [Fact]
    public void Operations_queue_operations_mirror_the_operations_dashboard_roles()
    {
        var decision = Matrix.Scalar("operations_queue_operations_decision");
        Assert.StartsWith("UI-PHASE2-QUEUE-COUNTS-2026-10-05", decision, StringComparison.Ordinal);
        Assert.Contains("\"Sí a los 5 grupos de estado, avanza con la fase 2\"", decision, StringComparison.Ordinal);
        Assert.Contains("exactly the roles of the operations dashboard", decision, StringComparison.Ordinal);
        Assert.Equal(
            ["getOperationsQueueCounts"],
            OperationIds(Matrix.Mapping("operations_queue_operations")).Order(StringComparer.Ordinal));
        Assert.Equal(
            [(OrganizationRole.Dispatcher, false), (OrganizationRole.PlatformAdmin, true)],
            TenantCapabilities.GetOperationsQueueCounts.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        foreach (var role in Enum.GetValues<OrganizationRole>())
        {
            foreach (var mfa in new[] { false, true })
            {
                var admitted = TenantCapabilities.GetOperationsQueueCounts.Grants
                    .Any(grant => grant.Role == role && (mfa || !grant.RequiresMfa));
                Assert.Equal(OperationsRolePolicy.IsAllowed(role, mfa), admitted);
            }
        }
    }

    [Fact]
    public void Every_capability_names_an_AI05_tenant_operation_that_declares_the_Forbidden_response()
    {
        var operations = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        foreach (var path in Contract.Mapping("paths").Children.Values.Cast<YamlMappingNode>())
        {
            foreach (var operation in path.Children.Values.OfType<YamlMappingNode>())
            {
                if (operation.Children.TryGetValue(new YamlScalarNode("operationId"), out var id))
                {
                    operations[((YamlScalarNode)id).Value!] = operation;
                }
            }
        }

        foreach (var operationId in TenantCapabilities.All.Keys)
        {
            Assert.True(operations.TryGetValue(operationId, out var operation), $"{operationId} is not in AI-05.");
            var forbidden = operation!.Mapping("responses").Mapping("403");
            Assert.Equal("#/components/responses/Forbidden", forbidden.Scalar("$ref"));
        }

        var problem = Contract.Mapping("components").Mapping("schemas").Mapping("ForbiddenProblem");
        var codes = problem.Mapping("properties").Mapping("code").Sequence("enum").Children
            .Select(node => ((YamlScalarNode)node).Value);
        Assert.Equal([TenantCapabilityGate.MfaRequiredCode], codes);
    }

    /// <summary>
    /// LOW-PRICE-MANUAL-AUTH-2026-10-02: AI-05 publishes the createQuote field capability in its own section and the
    /// server enforces exactly it - DISPATCHER without MFA, PLATFORM_ADMIN only with MFA - while createQuote itself
    /// keeps its D5 roles; createQuote declares the uniform 409 and both the request and the Quote carry the field.
    /// </summary>
    [Fact]
    public void Low_price_authorization_field_capability_matches_AI05_and_the_server()
    {
        Assert.StartsWith("LOW-PRICE-MANUAL-AUTH-2026-10-02", Matrix.Scalar("low_price_authorization_decision"), StringComparison.Ordinal);
        Assert.StartsWith("IMPLEMENTED", Matrix.Scalar("low_price_authorization_status"), StringComparison.Ordinal);
        var section = Matrix.Mapping("low_price_authorization");
        var (key, value) = Assert.Single(section.Children);
        Assert.Equal("createQuote.low_price_authorization", ((YamlScalarNode)key).Value);
        var published = ((YamlSequenceNode)value).Children.Select(node => ((YamlScalarNode)node).Value!).ToArray();

        var capability = TenantFieldCapabilities.CreateQuoteLowPriceAuthorization;
        Assert.Equal("createQuote", capability.OperationId);
        Assert.Equal(published, capability.Grants.Select(grant => ContractValue(grant.Role)));
        Assert.Equal(
            [(OrganizationRole.Dispatcher, false), (OrganizationRole.PlatformAdmin, true)],
            capability.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        Assert.Same(TenantCapabilities.CreateQuote, TenantCapabilities.All["createQuote"]);

        var createQuote = Contract.Mapping("paths").Mapping("/quotes").Mapping("post");
        Assert.Contains("LOW-PRICE-MANUAL-AUTH-2026-10-02", createQuote.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal("#/components/responses/Conflict", createQuote.Mapping("responses").Mapping("409").Scalar("$ref"));
        Assert.Equal("#/components/responses/Forbidden", createQuote.Mapping("responses").Mapping("403").Scalar("$ref"));

        var schemas = Contract.Mapping("components").Mapping("schemas");
        Assert.Equal(
            "#/components/schemas/LowPriceAuthorizationInput",
            schemas.Mapping("CreateQuoteRequest").Mapping("properties").Mapping("low_price_authorization").Scalar("$ref"));
        var reason = schemas.Mapping("LowPriceAuthorizationInput").Mapping("properties").Mapping("reason");
        Assert.Equal("1", reason.Scalar("minLength"));
        Assert.Equal(
            Pricing.Application.Quotes.QuoteLowPriceAuthorizationPolicy.MaximumReasonLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason.Scalar("maxLength"));
        var quoteField = schemas.Mapping("Quote").Mapping("properties").Mapping("low_price_authorization");
        Assert.Equal(
            ["valid_until"],
            quoteField.Sequence("required").Children.Select(node => ((YamlScalarNode)node).Value));
        Assert.Equal(
            ["valid_until", "actor_id", "reason"],
            quoteField.Mapping("properties").Children.Keys.Select(node => ((YamlScalarNode)node).Value));
        Assert.Contains("getOrderFinancials", quoteField.Scalar("description"), StringComparison.Ordinal);
    }

    private static bool IsMatrixOperationWithoutMfa(string operationId) =>
        OperationIds(Matrix.Mapping("operations")).Contains(operationId) &&
        !operationId.Contains("Settlement", StringComparison.Ordinal);

    private static Dictionary<string, HashSet<string>> Published()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var section in new[]
                 {
                     "operations", "finance_operations", "platform_operations", "membership_operations",
                     "tracking_link_operations", "incident_operations", "assignable_driver_operations",
                     "operations_queue_operations",
                 })
        {
            foreach (var (key, value) in Matrix.Mapping(section).Children)
            {
                result.Add(
                    ((YamlScalarNode)key).Value!,
                    ((YamlSequenceNode)value).Children
                        .Select(node => ((YamlScalarNode)node).Value!)
                        .ToHashSet(StringComparer.Ordinal));
            }
        }

        // AI05-LIST-SETTLEMENTS: listSettlements has the same capabilities as getSettlement.
        result.Add("listSettlements", result["getSettlement"]);
        return result;
    }

    private static HashSet<string> OperationIds(YamlMappingNode section) =>
        section.Children.Keys.Select(key => ((YamlScalarNode)key).Value!).ToHashSet(StringComparer.Ordinal);

    private static string RulesText() =>
        string.Join('\n', Matrix.Sequence("rules").Children.Select(node => ((YamlScalarNode)node).Value));

    private static string ContractValue(OrganizationRole role) => role.ToContractValue();
}
