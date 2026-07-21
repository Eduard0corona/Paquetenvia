using System.Reflection;

namespace Paqueteria.ArchitectureTests;

public sealed class DependencyRulesTests
{
    private static readonly IReadOnlyDictionary<string, Assembly> ProjectAssemblies = new Assembly[]
    {
        typeof(Paqueteria.Domain.AssemblyReference).Assembly,
        typeof(Paqueteria.Contracts.AssemblyReference).Assembly,
        typeof(Paqueteria.Application.AssemblyReference).Assembly,
        typeof(Paqueteria.Infrastructure.AssemblyReference).Assembly,
        typeof(Orders.Domain.AssemblyReference).Assembly,
        typeof(Orders.Application.AssemblyReference).Assembly,
        typeof(Orders.Infrastructure.AssemblyReference).Assembly,
        typeof(Orders.Endpoints.AssemblyReference).Assembly,
        typeof(Paqueteria.Api.AssemblyReference).Assembly,
        typeof(Paqueteria.Worker.AssemblyReference).Assembly,
    }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    [Fact]
    public void Domain_does_not_reference_infrastructure_or_frameworks()
    {
        var forbiddenFragments = new[]
        {
            "Infrastructure",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
        };

        var violations = ProjectAssemblies
            .Where(project => project.Key.EndsWith(".Domain", StringComparison.Ordinal))
            .SelectMany(project => project.Value.GetReferencedAssemblies()
                .Where(reference => forbiddenFragments.Any(fragment =>
                    reference.Name?.Contains(fragment, StringComparison.Ordinal) == true))
                .Select(reference => $"{project.Key} -> {reference.Name}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_does_not_reference_infrastructure()
    {
        var violations = ProjectAssemblies
            .Where(project => project.Key.EndsWith(".Application", StringComparison.Ordinal))
            .SelectMany(project => project.Value.GetReferencedAssemblies()
                .Where(reference => reference.Name?.EndsWith(".Infrastructure", StringComparison.Ordinal) == true)
                .Select(reference => $"{project.Key} -> {reference.Name}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Created_projects_have_no_circular_references()
    {
        var graph = ProjectAssemblies.ToDictionary(
            project => project.Key,
            project => project.Value.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null && ProjectAssemblies.ContainsKey(name))
                .Cast<string>()
                .ToArray(),
            StringComparer.Ordinal);

        foreach (var project in graph.Keys)
        {
            Assert.False(HasPathTo(project, project, graph, []), $"A dependency cycle includes {project}.");
        }
    }

    [Fact]
    public void Api_and_worker_are_composition_roots()
    {
        var compositionRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "Paqueteria.Api",
            "Paqueteria.Worker",
        };

        foreach (var root in compositionRoots)
        {
            Assert.True(ProjectAssemblies[root].EntryPoint is not null, $"{root} must be executable.");
        }

        var invalidRootReferences = ProjectAssemblies
            .Where(project => !compositionRoots.Contains(project.Key))
            .SelectMany(project => project.Value.GetReferencedAssemblies()
                .Where(reference => reference.Name is not null && compositionRoots.Contains(reference.Name))
                .Select(reference => $"{project.Key} -> {reference.Name}"))
            .ToArray();

        Assert.Empty(invalidRootReferences);
    }

    [Fact]
    public void Module_does_not_reference_another_modules_infrastructure()
    {
        var violations = ProjectAssemblies
            .Where(project => IsModuleAssembly(project.Key))
            .SelectMany(project => project.Value.GetReferencedAssemblies()
                .Where(reference => IsForeignModuleInfrastructure(project.Key, reference.Name))
                .Select(reference => $"{project.Key} -> {reference.Name}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool HasPathTo(
        string current,
        string target,
        IReadOnlyDictionary<string, string[]> graph,
        HashSet<string> visited)
    {
        foreach (var dependency in graph[current])
        {
            if (dependency == target)
            {
                return true;
            }

            if (visited.Add(dependency) && HasPathTo(dependency, target, graph, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsModuleAssembly(string assemblyName) =>
        assemblyName.StartsWith("Orders.", StringComparison.Ordinal);

    private static bool IsForeignModuleInfrastructure(string source, string? target)
    {
        if (target is null || !target.EndsWith(".Infrastructure", StringComparison.Ordinal))
        {
            return false;
        }

        var sourceModule = source.Split('.')[0];
        var targetModule = target.Split('.')[0];

        return sourceModule != targetModule && targetModule != "Paqueteria";
    }
}
