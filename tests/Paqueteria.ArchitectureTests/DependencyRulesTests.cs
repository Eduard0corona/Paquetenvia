using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void Project_reference_separators_are_normalized_for_the_current_platform()
    {
        var normalized = ProjectMetadataReader.NormalizeProjectReferenceInclude("../BuildingBlocks\\Domain/Domain.csproj");

        Assert.Equal(
            Path.Combine("..", "BuildingBlocks", "Domain", "Domain.csproj"),
            normalized);
    }

    [Fact]
    public void Catalog_registers_every_production_project_exactly_once()
    {
        var registered = SolutionCatalog.All
            .Select(component => TestRepository.Normalize(TestRepository.GetPath(component.ProjectPath)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var discovered = Directory.GetFiles(TestRepository.GetPath("src"), "*.csproj", SearchOption.AllDirectories)
            .Select(TestRepository.Normalize)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(registered.Length, registered.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(discovered, registered, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_module_has_the_canonical_four_layers()
    {
        var expectedRoles = new[]
        {
            ProjectRole.ModuleDomain,
            ProjectRole.ModuleApplication,
            ProjectRole.ModuleInfrastructure,
            ProjectRole.ModuleEndpoints,
        };

        foreach (var module in SolutionCatalog.Modules)
        {
            Assert.Equal(expectedRoles, module.Components.Select(component => component.Role));
            Assert.All(module.Components, component => Assert.Equal(module.Name, component.ModuleName));
            Assert.All(module.Components, component => Assert.True(File.Exists(TestRepository.GetPath(component.ProjectPath))));
        }
    }

    [Fact]
    public void Project_references_match_the_explicit_allowed_map()
    {
        var violations = new List<string>();

        foreach (var component in SolutionCatalog.All)
        {
            var metadata = ProjectMetadataReader.Read(component);
            var actual = new List<string>();

            foreach (var path in metadata.ProjectReferencePaths)
            {
                if (SolutionCatalog.ByProjectPath.TryGetValue(path, out var referencedProject))
                {
                    actual.Add(referencedProject.Name);
                }
                else
                {
                    violations.Add($"{component.Name} references unregistered project {path}.");
                }
            }

            var expected = component.AllowedProjectReferences.Order(StringComparer.Ordinal).ToArray();
            var sortedActual = actual.Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(sortedActual, StringComparer.Ordinal))
            {
                violations.Add(
                    $"{component.Name}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", sortedActual)}].");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Emitted_assembly_references_do_not_bypass_the_allowed_map()
    {
        var violations = SolutionCatalog.All
            .SelectMany(component => component.Assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null && SolutionCatalog.ByName.ContainsKey(name))
                .Cast<string>()
                .Where(name => !component.AllowedProjectReferences.Contains(name))
                .Select(name => $"{component.Name} -> {name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Domain_and_application_are_free_of_forbidden_technical_dependencies()
    {
        var violations = SolutionCatalog.All
            .SelectMany(component => ArchitectureRules.FindForbiddenTechnicalDependencies(
                component,
                ProjectMetadataReader.Read(component)))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Modules_do_not_reference_foreign_module_internals()
    {
        var violations = SolutionCatalog.Modules
            .SelectMany(module => module.Components)
            .SelectMany(component => ArchitectureRules.FindCrossModuleReferences(
                component,
                ProjectMetadataReader.Read(component)))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Production_project_graph_is_acyclic()
    {
        var graph = SolutionCatalog.All.ToDictionary(
            component => component.Name,
            component => (IReadOnlyList<string>)ProjectMetadataReader.Read(component).ProjectReferencePaths
                .Select(path => SolutionCatalog.ByProjectPath[path].Name)
                .ToArray(),
            StringComparer.Ordinal);

        var cycle = DependencyGraph.FindCycle(graph);

        Assert.True(cycle is null, $"Dependency cycle: {cycle}");
    }

    [Fact]
    public void Api_and_worker_are_the_only_composition_roots()
    {
        var roots = new[] { SolutionCatalog.Api, SolutionCatalog.Worker };
        Assert.All(roots, root => Assert.NotNull(root.Assembly.EntryPoint));

        var rootNames = roots.Select(root => root.Name).ToHashSet(StringComparer.Ordinal);
        var violations = SolutionCatalog.All
            .Where(component => !rootNames.Contains(component.Name))
            .SelectMany(component => component.Assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null && rootNames.Contains(name))
                .Select(name => $"{component.Name} -> {name}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Controllers_and_hubs_stay_in_transport_layers_and_do_not_expose_domain_types()
    {
        var violations = SolutionCatalog.All
            .SelectMany(ArchitectureRules.FindTransportTypeViolations)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Module_internals_are_not_exposed_to_foreign_modules()
    {
        var violations = SolutionCatalog.Modules
            .SelectMany(module => module.Components)
            .SelectMany(ArchitectureRules.FindForeignInternalsVisibility)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }
}
