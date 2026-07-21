using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class NegativeFixtureTests
{
    private static readonly ProjectComponent InvalidOrdersDomain = new(
        "Invalid.Orders.Domain",
        ProjectRole.ModuleDomain,
        typeof(ArchitectureFixtures.InvalidDomainController).Assembly,
        "tests/Paqueteria.ArchitectureFixtures/Paqueteria.ArchitectureFixtures.csproj",
        "Orders",
        new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public void Same_rules_reject_forbidden_framework_and_infrastructure_dependencies()
    {
        var violations = ArchitectureRules.FindForbiddenTechnicalDependencies(
            InvalidOrdersDomain,
            ProjectMetadataReader.Read(InvalidOrdersDomain));

        Assert.Contains(violations, violation => violation.Contains("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("Pricing.Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_rules_reject_a_foreign_module_infrastructure_reference()
    {
        var violations = ArchitectureRules.FindCrossModuleReferences(
            InvalidOrdersDomain,
            ProjectMetadataReader.Read(InvalidOrdersDomain));

        Assert.Contains(violations, violation => violation.Contains("Pricing.Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_rules_reject_a_controller_in_domain()
    {
        var violations = ArchitectureRules.FindTransportTypeViolations(InvalidOrdersDomain);

        Assert.Contains(violations, violation => violation.Contains("InvalidDomainController", StringComparison.Ordinal));
    }

    [Fact]
    public void Cycle_detector_reports_the_complete_dependency_chain()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> fixture =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["Orders.Domain"] = ["Pricing.Application"],
                ["Pricing.Application"] = ["Pricing.Infrastructure"],
                ["Pricing.Infrastructure"] = ["Orders.Domain"],
            };

        var cycle = DependencyGraph.FindCycle(fixture);

        Assert.Equal(
            "Orders.Domain -> Pricing.Application -> Pricing.Infrastructure -> Orders.Domain",
            cycle);
    }
}
