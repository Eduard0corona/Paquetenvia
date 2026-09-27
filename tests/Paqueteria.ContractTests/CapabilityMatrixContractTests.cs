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

    private static bool IsMatrixOperationWithoutMfa(string operationId) =>
        OperationIds(Matrix.Mapping("operations")).Contains(operationId) &&
        !operationId.Contains("Settlement", StringComparison.Ordinal);

    private static Dictionary<string, HashSet<string>> Published()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var section in new[] { "operations", "finance_operations", "platform_operations", "membership_operations" })
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
