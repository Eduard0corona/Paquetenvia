using System.Reflection;
using System.Runtime.CompilerServices;

namespace Paqueteria.ArchitectureTests.Architecture;

internal static class ArchitectureRules
{
    private static readonly string[] DomainForbiddenDependencies =
    [
        ".Infrastructure",
        ".Endpoints",
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "StackExchange.Redis",
        "SignalR",
        "OpenTelemetry",
        "Amazon",
        "AWSSDK",
        "Azure.Storage",
        "Google.Cloud",
        "Minio",
        "MediatR",
        "MassTransit",
        "Wolverine",
        "Newtonsoft.Json",
        "System.Text.Json",
    ];

    private static readonly string[] ApplicationForbiddenDependencies =
    [
        ".Infrastructure",
        ".Endpoints",
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "StackExchange.Redis",
        "SignalR",
        "OpenTelemetry",
        "Amazon",
        "AWSSDK",
        "Azure.Storage",
        "Google.Cloud",
        "Minio",
    ];

    internal static IReadOnlyList<string> FindForbiddenTechnicalDependencies(
        ProjectComponent component,
        ProjectMetadata metadata)
    {
        var forbidden = component.Role switch
        {
            ProjectRole.BuildingBlockDomain or ProjectRole.ModuleDomain => DomainForbiddenDependencies,
            ProjectRole.BuildingBlockApplication or ProjectRole.ModuleApplication => ApplicationForbiddenDependencies,
            _ => [],
        };

        var dependencies = component.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Concat(metadata.PackageReferences)
            .Concat(metadata.FrameworkReferences)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return dependencies
            .Where(dependency => forbidden.Any(fragment =>
                dependency.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(dependency => $"{component.Name} -> {dependency}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> FindCrossModuleReferences(
        ProjectComponent component,
        ProjectMetadata metadata)
    {
        if (component.ModuleName is null)
        {
            return [];
        }

        var sourceModule = SolutionCatalog.Modules.Single(module => module.Name == component.ModuleName);
        var projectReferenceNames = metadata.ProjectReferencePaths.Select(path =>
            SolutionCatalog.ByProjectPath.TryGetValue(path, out var project) ? project.Name : path);
        var assemblyReferenceNames = component.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        return projectReferenceNames
            .Concat(assemblyReferenceNames)
            .Distinct(StringComparer.Ordinal)
            .Select(target => new
            {
                Target = target,
                Module = SolutionCatalog.Modules.FirstOrDefault(module =>
                    target.StartsWith($"{module.Name}.", StringComparison.Ordinal)),
            })
            .Where(reference =>
                reference.Module is not null &&
                reference.Module.Name != component.ModuleName &&
                !sourceModule.AllowedCrossModuleDependencies.Contains(reference.Module.Name))
            .Select(reference => $"{component.Name} -> {reference.Target}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> FindTransportTypeViolations(ProjectComponent component)
    {
        var violations = new List<string>();
        var transportTypes = component.Assembly.GetTypes().Where(IsControllerOrHub).ToArray();

        foreach (var type in transportTypes)
        {
            if (component.Role is not ProjectRole.ModuleEndpoints and not ProjectRole.ApiRoot)
            {
                violations.Add($"{type.FullName} is a controller or hub in {component.Role}.");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var exposedTypes = method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(FlattenType);

                foreach (var exposedType in exposedTypes.Where(IsDomainType))
                {
                    violations.Add($"{type.FullName}.{method.Name} exposes domain type {exposedType.FullName}.");
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> FindForeignInternalsVisibility(ProjectComponent component)
    {
        if (component.ModuleName is null)
        {
            return [];
        }

        return component.Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0])
            .Where(target => SolutionCatalog.Modules.Any(module =>
                module.Name != component.ModuleName &&
                target.StartsWith($"{module.Name}.", StringComparison.Ordinal)))
            .Select(target => $"{component.Name} exposes internals to {target}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsControllerOrHub(Type type)
    {
        if (type.Name.EndsWith("Controller", StringComparison.Ordinal) ||
            type.Name.EndsWith("Hub", StringComparison.Ordinal))
        {
            return true;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName is "Microsoft.AspNetCore.Mvc.Controller" or
                "Microsoft.AspNetCore.Mvc.ControllerBase" or
                "Microsoft.AspNetCore.SignalR.Hub")
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in FlattenType(elementType))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in FlattenType(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsDomainType(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        return assemblyName == "Paqueteria.Domain" ||
            assemblyName.EndsWith(".Domain", StringComparison.Ordinal);
    }
}
