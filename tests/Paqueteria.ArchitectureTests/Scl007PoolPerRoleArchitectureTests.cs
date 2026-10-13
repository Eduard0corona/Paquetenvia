using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// SCL-007 (phase 4 PR 8): every process has one <see cref="RuntimeDataSource"/> per database role, registered only by
/// <see cref="RuntimeDataSourceServiceCollectionExtensions.AddRuntimeDataSource"/>, and every module resolves it by
/// its role. No runtime code builds a pool or a connection of its own; only the migrator side (the E-002 baseline
/// deployer and the design-time contexts of the migrator) keeps its own credential.
/// </summary>
public sealed partial class Scl007PoolPerRoleArchitectureTests
{
    private const string PoolingDirectory = "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Pooling/";

    /// <summary>The source files that may build a pool or a connection, each with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> PoolBuilders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PoolingDirectory + "RuntimeDataSource.cs"] = "the one NpgsqlDataSourceBuilder of every runtime pool",
            [PoolingDirectory + "RuntimeDataSourceServiceCollectionExtensions.cs"] =
                "RuntimeDataSource.Create, once per role and process",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Database/Baseline/DatabaseBaselineDeployer.cs"] =
                "the migrator's E-002 baseline deployment, with the deployment credential (not a runtime pool)",
        };

    /// <summary>
    /// Types constructed by an explicit DI factory that hands them the keyed pool of their role (or by tests), so their
    /// <see cref="NpgsqlDataSource"/> parameter carries no <see cref="FromKeyedServicesAttribute"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FactoryConstructedConsumers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Orders.Infrastructure.Lifecycle.PostgreSqlExpiredClaimWindowFinalizer"] =
                "AddOrdersClaimWindowFinalization passes OrdersWorkerDataSource (the paqueteria_worker pool)",
            ["Orders.Infrastructure.Lifecycle.PostgreSqlOrderAutoCloseOwnerDiscovery"] =
                "AddOrdersAutoClose passes OrdersWorkerDataSource (the paqueteria_worker pool)",
            ["Custody.Infrastructure.Cleanup.PostgreSqlOperationalCleanupGateway"] =
                "AddCustodyOperationalCleanup passes the keyed paqueteria_worker pool",
            ["Paqueteria.Infrastructure.DataProtection.PostgreSqlXmlRepository"] =
                "AddPlatformDataProtection passes the keyed pool named by DataProtection:ConnectionStringName",
        };

    [Fact]
    public void No_runtime_code_builds_its_own_pool_or_connection()
    {
        var builders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (relative, source) in SourceFiles())
        {
            if (!PoolCreation().IsMatch(source))
            {
                continue;
            }

            builders.Add(relative);
            Assert.True(
                PoolBuilders.ContainsKey(relative) || IsDesignTimeContextFactory(source),
                $"{relative} builds a NpgsqlDataSource or NpgsqlConnection; SCL-007 allows only " +
                "AddRuntimeDataSource (one pool per role) and the migrator side.");
        }

        // The allowlist stays exact: a builder that no longer builds anything is removed from it.
        Assert.All(PoolBuilders.Keys, relative => Assert.Contains(relative, builders));
    }

    [Fact]
    public void Every_runtime_entity_framework_context_uses_a_shared_role_pool()
    {
        foreach (var (relative, source) in SourceFiles())
        {
            if (IsDesignTimeContextFactory(source))
            {
                continue;
            }

            foreach (Match call in UseNpgsqlCall().Matches(source))
            {
                var argument = call.Groups["argument"].Value;
                Assert.True(
                    argument.StartsWith("serviceProvider.GetRuntimeDataSource(", StringComparison.Ordinal) ||
                    argument is "serviceProvider.GetRequiredService<DispatchWorkerConnectionFactory>().DataSource" or
                        "serviceProvider.GetRequiredService<OrdersWorkerDataSource>().DataSource",
                    $"{relative}: UseNpgsql({argument}) must use the shared pool of its role (SCL-007).");
            }
        }
    }

    [Fact]
    public void Modules_resolve_pools_only_by_role_and_never_register_one()
    {
        foreach (var (relative, source) in SourceFiles())
        {
            Assert.DoesNotMatch(UnkeyedDataSourceUse(), source);
            if (relative.StartsWith(PoolingDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.False(
                source.Contains("AddKeyedSingleton<NpgsqlDataSource>", StringComparison.Ordinal) ||
                source.Contains("AddKeyedSingleton<RuntimeDataSource>", StringComparison.Ordinal) ||
                source.Contains("AddKeyedSingleton(RuntimeDatabaseRole", StringComparison.Ordinal),
                $"{relative} registers a pool; only AddRuntimeDataSource may (SCL-007).");
        }
    }

    [Fact]
    public void Every_injected_pool_names_its_role()
    {
        var unkeyed = new List<string>();
        foreach (var component in SolutionCatalog.All.Where(static component => component.Role is
                     ProjectRole.BuildingBlockInfrastructure or ProjectRole.ModuleInfrastructure or
                     ProjectRole.ApiRoot or ProjectRole.WorkerRoot))
        {
            foreach (var type in LoadableTypes(component.Assembly))
            {
                foreach (var constructor in type.GetConstructors(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (var parameter in constructor.GetParameters())
                    {
                        if (parameter.ParameterType != typeof(RuntimeDataSource) &&
                            parameter.ParameterType != typeof(NpgsqlDataSource))
                        {
                            continue;
                        }

                        var keyed = parameter.GetCustomAttribute<FromKeyedServicesAttribute>();
                        if (keyed?.Key is RuntimeDatabaseRole)
                        {
                            continue;
                        }

                        if (parameter.ParameterType == typeof(NpgsqlDataSource) &&
                            FactoryConstructedConsumers.ContainsKey(type.FullName ?? string.Empty))
                        {
                            continue;
                        }

                        unkeyed.Add($"{type.FullName}({parameter.Name})");
                    }
                }
            }
        }

        Assert.Empty(unkeyed);
    }

    private static IEnumerable<(string Relative, string Source)> SourceFiles()
    {
        var root = TestRepository.GetPath("src");
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(path));
        }
    }

    private static bool IsDesignTimeContextFactory(string source) =>
        source.Contains("IDesignTimeDbContextFactory<", StringComparison.Ordinal);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    [GeneratedRegex(
        @"NpgsqlDataSource\.Create\(|new\s+NpgsqlDataSourceBuilder\(|new\s+NpgsqlConnection\(|" +
        @"NpgsqlConnection\.GlobalTypeMapper|RuntimeDataSource\.Create\(|new\s+RuntimeDataSource\(")]
    private static partial Regex PoolCreation();

    [GeneratedRegex(@"\.UseNpgsql\(\s*(?<argument>[^,\r\n]+?)\s*(?:,|\)\s*$|\)\s*\n)", RegexOptions.Multiline)]
    private static partial Regex UseNpgsqlCall();

    [GeneratedRegex(
        @"GetRequiredService<NpgsqlDataSource>|GetService<NpgsqlDataSource>|AddSingleton<NpgsqlDataSource>|" +
        @"TryAddSingleton<NpgsqlDataSource>|GetRequiredService<RuntimeDataSource>|GetService<RuntimeDataSource>")]
    private static partial Regex UnkeyedDataSourceUse();
}
