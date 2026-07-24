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
        typeof(Orders.Endpoints.AssemblyReference).Assembly,
        additionalApplicationReferences: ["Paqueteria.Contracts"],
        additionalInfrastructureReferences: ["Paqueteria.Application", "Paqueteria.Contracts"],
        additionalEndpointReferences: ["Organizations.Application", "Organizations.Endpoints", "Paqueteria.Application"],
        allowedCrossModuleDependencies: ["Organizations"]);

    internal static readonly ModuleDefinition Pricing = Module(
        "Pricing",
        typeof(Pricing.Domain.AssemblyReference).Assembly,
        typeof(Pricing.Application.AssemblyReference).Assembly,
        typeof(Pricing.Infrastructure.AssemblyReference).Assembly,
        typeof(Pricing.Endpoints.AssemblyReference).Assembly,
        additionalInfrastructureReferences: ["Locations.Application", "Paqueteria.Application"],
        additionalEndpointReferences: ["Organizations.Application", "Organizations.Endpoints", "Paqueteria.Application"],
        allowedCrossModuleDependencies: ["Locations", "Organizations"]);

    internal static readonly ModuleDefinition Identity = Module(
        "Identity",
        typeof(Identity.Domain.AssemblyReference).Assembly,
        typeof(Identity.Application.AssemblyReference).Assembly,
        typeof(Identity.Infrastructure.AssemblyReference).Assembly,
        typeof(Identity.Endpoints.AssemblyReference).Assembly,
        usesSharedDomainContracts: true);

    internal static readonly ModuleDefinition Organizations = Module(
        "Organizations",
        typeof(Organizations.Domain.AssemblyReference).Assembly,
        typeof(Organizations.Application.AssemblyReference).Assembly,
        typeof(Organizations.Infrastructure.AssemblyReference).Assembly,
        typeof(Organizations.Endpoints.AssemblyReference).Assembly,
        usesSharedDomainContracts: true);

    internal static readonly ModuleDefinition Locations = Module(
        "Locations",
        typeof(Locations.Domain.AssemblyReference).Assembly,
        typeof(Locations.Application.AssemblyReference).Assembly,
        typeof(Locations.Infrastructure.AssemblyReference).Assembly,
        typeof(Locations.Endpoints.AssemblyReference).Assembly,
        usesSharedDomainContracts: true,
        additionalEndpointReferences: ["Organizations.Application", "Organizations.Endpoints"],
        allowedCrossModuleDependencies: ["Organizations"]);

    internal static readonly ModuleDefinition Drivers = Module(
        "Drivers",
        typeof(Drivers.Domain.AssemblyReference).Assembly,
        typeof(Drivers.Application.AssemblyReference).Assembly,
        typeof(Drivers.Infrastructure.AssemblyReference).Assembly,
        typeof(Drivers.Endpoints.AssemblyReference).Assembly,
        additionalInfrastructureReferences: ["Paqueteria.Application"]);

    internal static readonly ModuleDefinition Dispatch = Module(
        "Dispatch",
        typeof(Dispatch.Domain.AssemblyReference).Assembly,
        typeof(Dispatch.Application.AssemblyReference).Assembly,
        typeof(Dispatch.Infrastructure.AssemblyReference).Assembly,
        typeof(Dispatch.Endpoints.AssemblyReference).Assembly,
        additionalApplicationReferences: ["Drivers.Application"],
        additionalInfrastructureReferences:
        [
            "Paqueteria.Application",
            "Drivers.Application",
            "Orders.Application",
            "Orders.Domain",
        ],
        additionalEndpointReferences:
        [
            "Organizations.Application",
            "Organizations.Endpoints",
            "Paqueteria.Application",
        ],
        allowedCrossModuleDependencies: ["Drivers", "Orders", "Organizations"]);

    internal static readonly IReadOnlyList<ModuleDefinition> Modules =
        [Identity, Orders, Pricing, Organizations, Locations, Drivers, Dispatch];

    internal static readonly ProjectComponent Api = Component(
        "Paqueteria.Api",
        ProjectRole.ApiRoot,
        typeof(Paqueteria.Api.AssemblyReference).Assembly,
        "src/Paqueteria.Api/Paqueteria.Api.csproj",
        allowed:
        [
            "Paqueteria.Infrastructure",
            "Paqueteria.Domain",
            "Identity.Application",
            "Identity.Endpoints",
            "Identity.Infrastructure",
            "Orders.Endpoints",
            "Orders.Infrastructure",
            "Pricing.Endpoints",
            "Pricing.Infrastructure",
            "Organizations.Endpoints",
            "Organizations.Infrastructure",
            "Organizations.Application",
            "Locations.Endpoints",
            "Locations.Infrastructure",
            "Drivers.Infrastructure",
            "Dispatch.Endpoints",
            "Dispatch.Infrastructure",
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
        .. Identity.Components,
        .. Organizations.Components,
        .. Locations.Components,
        .. Drivers.Components,
        .. Dispatch.Components,
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
        System.Reflection.Assembly endpointsAssembly,
        bool usesSharedDomainContracts = false,
        string[]? additionalApplicationReferences = null,
        string[]? additionalInfrastructureReferences = null,
        string[]? additionalEndpointReferences = null,
        string[]? allowedCrossModuleDependencies = null)
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
            (usesSharedDomainContracts
                ? new[] { "Paqueteria.Application", "Paqueteria.Domain", $"{name}.Domain" }
                : new[] { "Paqueteria.Application", $"{name}.Domain" })
            .Concat(additionalApplicationReferences ?? []).ToArray());
        var infrastructure = Component(
            $"{name}.Infrastructure",
            ProjectRole.ModuleInfrastructure,
            infrastructureAssembly,
            $"src/Modules/{name}/{name}.Infrastructure/{name}.Infrastructure.csproj",
            name,
            (usesSharedDomainContracts
                ? new[] { "Paqueteria.Infrastructure", "Paqueteria.Application", "Paqueteria.Domain", $"{name}.Application", $"{name}.Domain" }
                : new[] { "Paqueteria.Infrastructure", $"{name}.Application", $"{name}.Domain" })
                .Concat(additionalInfrastructureReferences ?? []).ToArray());
        var endpoints = Component(
            $"{name}.Endpoints",
            ProjectRole.ModuleEndpoints,
            endpointsAssembly,
            $"src/Modules/{name}/{name}.Endpoints/{name}.Endpoints.csproj",
            name,
            (usesSharedDomainContracts
                ? new[] { "Paqueteria.Application", "Paqueteria.Domain", $"{name}.Application" }
                : [$"{name}.Application"]).Concat(additionalEndpointReferences ?? []).ToArray());

        return new ModuleDefinition(
            name,
            domain,
            application,
            infrastructure,
            endpoints,
            new HashSet<string>(allowedCrossModuleDependencies ?? [], StringComparer.Ordinal));
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
