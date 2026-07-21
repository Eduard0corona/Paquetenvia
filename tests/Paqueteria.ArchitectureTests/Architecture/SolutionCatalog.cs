namespace Paqueteria.ArchitectureTests.Architecture;

internal static class SolutionCatalog
{
    internal static readonly ProjectComponent Domain = Component(
        "Paqueteria.Domain",
        ProjectRole.BuildingBlockDomain,
        typeof(Paqueteria.Domain.AssemblyReference).Assembly,
        "src/BuildingBlocks/Paqueteria.Domain/Paqueteria.Domain.csproj");

    internal static readonly ProjectComponent Contracts = Component(
        "Paqueteria.Contracts",
        ProjectRole.BuildingBlockContracts,
        typeof(Paqueteria.Contracts.AssemblyReference).Assembly,
        "src/BuildingBlocks/Paqueteria.Contracts/Paqueteria.Contracts.csproj");

    internal static readonly ProjectComponent Application = Component(
        "Paqueteria.Application",
        ProjectRole.BuildingBlockApplication,
        typeof(Paqueteria.Application.AssemblyReference).Assembly,
        "src/BuildingBlocks/Paqueteria.Application/Paqueteria.Application.csproj",
        allowed: ["Paqueteria.Contracts", "Paqueteria.Domain"]);

    internal static readonly ProjectComponent Infrastructure = Component(
        "Paqueteria.Infrastructure",
        ProjectRole.BuildingBlockInfrastructure,
        typeof(Paqueteria.Infrastructure.AssemblyReference).Assembly,
        "src/BuildingBlocks/Paqueteria.Infrastructure/Paqueteria.Infrastructure.csproj",
        allowed: ["Paqueteria.Application", "Paqueteria.Domain"]);

    internal static readonly ModuleDefinition Orders = Module(
        "Orders",
        typeof(Orders.Domain.AssemblyReference).Assembly,
        typeof(Orders.Application.AssemblyReference).Assembly,
        typeof(Orders.Infrastructure.AssemblyReference).Assembly,
        typeof(Orders.Endpoints.AssemblyReference).Assembly);

    internal static readonly ModuleDefinition Pricing = Module(
        "Pricing",
        typeof(Pricing.Domain.AssemblyReference).Assembly,
        typeof(Pricing.Application.AssemblyReference).Assembly,
        typeof(Pricing.Infrastructure.AssemblyReference).Assembly,
        typeof(Pricing.Endpoints.AssemblyReference).Assembly);

    internal static readonly IReadOnlyList<ModuleDefinition> Modules = [Orders, Pricing];

    internal static readonly ProjectComponent Api = Component(
        "Paqueteria.Api",
        ProjectRole.ApiRoot,
        typeof(Paqueteria.Api.AssemblyReference).Assembly,
        "src/Paqueteria.Api/Paqueteria.Api.csproj",
        allowed:
        [
            "Paqueteria.Infrastructure",
            "Orders.Endpoints",
            "Orders.Infrastructure",
            "Pricing.Endpoints",
            "Pricing.Infrastructure",
        ]);

    internal static readonly ProjectComponent Worker = Component(
        "Paqueteria.Worker",
        ProjectRole.WorkerRoot,
        typeof(Paqueteria.Worker.AssemblyReference).Assembly,
        "src/Paqueteria.Worker/Paqueteria.Worker.csproj",
        allowed: ["Paqueteria.Infrastructure", "Orders.Infrastructure", "Pricing.Infrastructure"]);

    internal static IReadOnlyList<ProjectComponent> All { get; } =
    [
        Domain,
        Contracts,
        Application,
        Infrastructure,
        .. Orders.Components,
        .. Pricing.Components,
        Api,
        Worker,
    ];

    internal static IReadOnlyDictionary<string, ProjectComponent> ByName { get; } =
        All.ToDictionary(component => component.Name, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, ProjectComponent> ByProjectPath { get; } =
        All.ToDictionary(
            component => TestRepository.Normalize(TestRepository.GetPath(component.ProjectPath)),
            StringComparer.OrdinalIgnoreCase);

    private static ModuleDefinition Module(
        string name,
        System.Reflection.Assembly domainAssembly,
        System.Reflection.Assembly applicationAssembly,
        System.Reflection.Assembly infrastructureAssembly,
        System.Reflection.Assembly endpointsAssembly)
    {
        var domain = Component(
            $"{name}.Domain",
            ProjectRole.ModuleDomain,
            domainAssembly,
            $"src/Modules/{name}/{name}.Domain/{name}.Domain.csproj",
            name,
            ["Paqueteria.Domain"]);
        var application = Component(
            $"{name}.Application",
            ProjectRole.ModuleApplication,
            applicationAssembly,
            $"src/Modules/{name}/{name}.Application/{name}.Application.csproj",
            name,
            ["Paqueteria.Application", $"{name}.Domain"]);
        var infrastructure = Component(
            $"{name}.Infrastructure",
            ProjectRole.ModuleInfrastructure,
            infrastructureAssembly,
            $"src/Modules/{name}/{name}.Infrastructure/{name}.Infrastructure.csproj",
            name,
            ["Paqueteria.Infrastructure", $"{name}.Application", $"{name}.Domain"]);
        var endpoints = Component(
            $"{name}.Endpoints",
            ProjectRole.ModuleEndpoints,
            endpointsAssembly,
            $"src/Modules/{name}/{name}.Endpoints/{name}.Endpoints.csproj",
            name,
            [$"{name}.Application"]);

        return new ModuleDefinition(
            name,
            domain,
            application,
            infrastructure,
            endpoints,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static ProjectComponent Component(
        string name,
        ProjectRole role,
        System.Reflection.Assembly assembly,
        string projectPath,
        string? moduleName = null,
        string[]? allowed = null) =>
        new(
            name,
            role,
            assembly,
            projectPath,
            moduleName,
            new HashSet<string>(allowed ?? [], StringComparer.Ordinal));
}
