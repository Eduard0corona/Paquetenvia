using System.Reflection;

namespace Paqueteria.ArchitectureTests.Architecture;

internal sealed record ProjectComponent(
    string Name,
    ProjectRole Role,
    Assembly Assembly,
    string ProjectPath,
    string? ModuleName,
    IReadOnlySet<string> AllowedProjectReferences);

internal sealed record ModuleDefinition(
    string Name,
    ProjectComponent Domain,
    ProjectComponent Application,
    ProjectComponent Infrastructure,
    ProjectComponent Endpoints,
    IReadOnlySet<string> AllowedCrossModuleDependencies)
{
    internal IReadOnlyList<ProjectComponent> Components =>
        [Domain, Application, Infrastructure, Endpoints];
}
