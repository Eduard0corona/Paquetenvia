using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// SCL-007 (phase 4 PR 8): every process has one <see cref="RuntimeDataSource"/> per database role, registered only by
/// <see cref="RuntimeDataSourceServiceCollectionExtensions.AddRuntimeDataSource"/>, and every module resolves it by
/// its role. No runtime code builds a pool or a connection of its own; only the migrator side (the E-002 baseline
/// deployer and the design-time contexts of the migrator) keeps its own credential. The guard reads both the source
/// and the compiled IL, so no syntax (target-typed <c>new</c>, a qualified name, an object initializer, a lambda or an
/// async method) gets around it, and it proves on fixtures that it catches every such form.
/// </summary>
public sealed partial class Scl007PoolPerRoleArchitectureTests
{
    private const string PoolingDirectory = "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Pooling/";
    private const string PoolingNamespace = "Paqueteria.Infrastructure.Database.Pooling";
    private const string BaselineNamespace = "Paqueteria.Infrastructure.Database.Baseline";

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
    /// Design-time contexts of the migrator (<c>dotnet ef</c> and the module lanes), which give EF Core a connection
    /// string. Only files named <c>*DbContextFactory.cs</c> qualify, plus this one, whose factory shares the file of its
    /// migration-only context.
    /// </summary>
    private static readonly IReadOnlySet<string> DesignTimeFactoryFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Evolution/PlatformEvolutionDbContext.cs",
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
    public void No_runtime_source_builds_its_own_pool_or_connection()
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
                PoolBuilders.ContainsKey(relative),
                $"{relative} builds a NpgsqlDataSource or NpgsqlConnection; SCL-007 allows only " +
                "AddRuntimeDataSource (one pool per role) and the migrator's baseline deployer.");
        }

        // The allowlist stays exact: a builder that no longer builds anything is removed from it.
        Assert.All(PoolBuilders.Keys, relative => Assert.Contains(relative, builders));
    }

    [Fact]
    public void No_runtime_assembly_builds_its_own_pool_or_connection()
    {
        var findings = SolutionCatalog.All
            .SelectMany(static component => PoolCreationScanner.Scan(component.Assembly))
            .ToArray();
        var exempt = new Dictionary<string, int>(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var finding in findings)
        {
            if (ExemptionOf(finding) is { } reason)
            {
                exempt[reason] = exempt.GetValueOrDefault(reason) + 1;
            }
            else
            {
                violations.Add(finding.ToString());
            }
        }

        Assert.Empty(violations);

        // Each exemption is still used, so a stale one surfaces instead of hiding a future violation.
        Assert.Equal(
            ["design-time context", "migrator baseline", "pooling building block"],
            exempt.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_runtime_entity_framework_context_uses_a_shared_role_pool()
    {
        var designTime = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (relative, source) in SourceFiles())
        {
            if (IsDesignTimeFactoryFile(relative))
            {
                Assert.Contains("IDesignTimeDbContextFactory<", source, StringComparison.Ordinal);
                designTime.Add(relative);
                continue;
            }

            // The IL guard exempts a design-time factory type; this keeps such types inside the design-time files.
            Assert.False(
                source.Contains("IDesignTimeDbContextFactory<", StringComparison.Ordinal),
                $"{relative} declares a design-time context factory; only *DbContextFactory.cs files may (SCL-007).");

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

        Assert.All(DesignTimeFactoryFiles, relative => Assert.Contains(relative, designTime));
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

    [Fact]
    public void The_il_guard_catches_every_way_around_it()
    {
        var found = PoolCreationScanner.Scan(typeof(BypassForms))
            .Select(static finding => (Source: SourceMethodName(finding.Method), finding.Kind, finding.What))
            .ToArray();

        foreach (var (method, what) in new[]
        {
            (nameof(BypassForms.TargetTypedConnection), "new NpgsqlConnection"),
            (nameof(BypassForms.QualifiedConnection), "new NpgsqlConnection"),
            (nameof(BypassForms.InitializerConnection), "new NpgsqlConnection"),
            (nameof(BypassForms.TargetTypedBuilder), "new NpgsqlDataSourceBuilder"),
            (nameof(BypassForms.SlimBuilder), "new NpgsqlSlimDataSourceBuilder"),
            (nameof(BypassForms.StaticCreate), "NpgsqlDataSource.Create"),
            (nameof(BypassForms.GlobalMapper), "NpgsqlConnection.GlobalTypeMapper"),
            (nameof(BypassForms.StandalonePool), "RuntimeDataSource.Create"),
            (nameof(BypassForms.InLambda), "new NpgsqlConnection"),
            (nameof(BypassForms.InAsyncMethod), "new NpgsqlConnection"),
            (nameof(BypassForms.EntityFrameworkConnectionString), "UseNpgsql(String)"),
            (nameof(BypassForms.EntityFrameworkConnection), "UseNpgsql(DbConnection)"),
            (nameof(BypassForms.EntityFrameworkGenericConnectionString), "UseNpgsql(String)"),
        })
        {
            Assert.Contains(found, item => item.Source == method && item.What == what);
        }

        Assert.DoesNotContain(found, item => item.Source == nameof(BypassForms.SharedPool));

        // None of the bypass forms is exempt: the fixtures live outside the pooling and baseline namespaces.
        Assert.All(
            PoolCreationScanner.Scan(typeof(BypassForms)),
            finding => Assert.Null(ExemptionOf(finding)));

        // A design-time factory is exempt only for the connection string it gives EF Core, never for a connection
        // it opens itself.
        var designTime = PoolCreationScanner.Scan(typeof(DesignTimeFixtureFactory)).ToArray();
        Assert.Equal(typeof(DesignTimeFixtureFactory), Assert.Single(designTime.Select(static f => f.DeclaredType).Distinct()));
        Assert.Equal(
            "design-time context",
            ExemptionOf(Assert.Single(designTime, static finding => finding.What == "UseNpgsql(String)")));
        Assert.Null(ExemptionOf(Assert.Single(designTime, static finding => finding.What == "new NpgsqlConnection")));
    }

    [Fact]
    public void The_source_guard_catches_every_way_around_it()
    {
        string[] forms =
        [
            "using var connection = new NpgsqlConnection(connectionString);",
            "NpgsqlConnection connection = new(connectionString);",
            "NpgsqlConnection? connection = new (connectionString);",
            "var connection = new Npgsql.NpgsqlConnection(connectionString);",
            "var connection = new global::Npgsql.NpgsqlConnection(connectionString);",
            "var connection = new NpgsqlConnection { ConnectionString = connectionString };",
            "NpgsqlDataSourceBuilder builder = new(connectionString);",
            "var builder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);",
            "var builder = new NpgsqlSlimDataSourceBuilder(connectionString);",
            "NpgsqlSlimDataSourceBuilder builder = new(connectionString);",
            "var dataSource = NpgsqlDataSource.Create(connectionString);",
            "NpgsqlConnection.GlobalTypeMapper.UseNetTopologySuite();",
            "var pool = RuntimeDataSource.Create(role, connectionString, 4);",
        ];
        Assert.All(forms, form => Assert.Matches(PoolCreation(), form));

        // Parsing a connection string or using the shared pool is not building one.
        Assert.DoesNotMatch(PoolCreation(), "var login = new NpgsqlConnectionStringBuilder(connectionString).Username;");
        Assert.DoesNotMatch(PoolCreation(), "await using var connection = await pool.OpenConnectionAsync(token);");

        // Only *DbContextFactory.cs files (and the one listed) are design-time; another file with a factory is not.
        Assert.True(IsDesignTimeFactoryFile("src/Modules/Orders/Orders.Infrastructure/Persistence/OrdersDbContextFactory.cs"));
        Assert.False(IsDesignTimeFactoryFile("src/Modules/Orders/Orders.Infrastructure/DependencyInjection.cs"));
    }

    /// <summary>The method a finding comes from as written in source (a lambda or async body carries its name).</summary>
    private static string SourceMethodName(MethodBase method)
    {
        var name = method.Name;
        var owner = method.DeclaringType;
        var candidate = name.StartsWith('<') ? name : owner?.Name ?? name;
        var match = Regex.Match(candidate, "^<(?<name>[^>]+)>");
        return match.Success ? match.Groups["name"].Value : name;
    }

    private static string? ExemptionOf(PoolCreationScanner.Finding finding)
    {
        var declared = finding.DeclaredType;
        if (declared.Namespace == PoolingNamespace && finding.Kind == PoolCreationScanner.Kind.PoolOrConnection)
        {
            return "pooling building block";
        }

        if (declared.Namespace == BaselineNamespace && finding.What == "new NpgsqlConnection")
        {
            return "migrator baseline";
        }

        if (finding.Kind == PoolCreationScanner.Kind.EntityFrameworkOwnPool &&
            declared.GetInterfaces().Any(static contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>)))
        {
            return "design-time context";
        }

        return null;
    }

    private static bool IsDesignTimeFactoryFile(string relative) =>
        relative.EndsWith("DbContextFactory.cs", StringComparison.Ordinal) || DesignTimeFactoryFiles.Contains(relative);

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
        @"NpgsqlDataSource\.Create\(" +
        @"|\bnew\s+(?:global::)?(?:Npgsql\.)?Npgsql(?:Slim)?DataSourceBuilder\b" +
        @"|\bnew\s+(?:global::)?(?:Npgsql\.)?NpgsqlConnection\b" +
        @"|\b(?:Npgsql\.)?Npgsql(?:Slim)?DataSourceBuilder\??\s+\w+\s*=\s*new\s*\(" +
        @"|\b(?:Npgsql\.)?NpgsqlConnection\??\s+\w+\s*=\s*new\s*\(" +
        @"|NpgsqlConnection\.GlobalTypeMapper" +
        @"|RuntimeDataSource\.Create\(" +
        @"|\bnew\s+RuntimeDataSource\(")]
    private static partial Regex PoolCreation();

    [GeneratedRegex(@"\.UseNpgsql\(\s*(?<argument>[^,\r\n]+?)\s*(?:,|\)\s*$|\)\s*\n)", RegexOptions.Multiline)]
    private static partial Regex UseNpgsqlCall();

    [GeneratedRegex(
        @"GetRequiredService<NpgsqlDataSource>|GetService<NpgsqlDataSource>|AddSingleton<NpgsqlDataSource>|" +
        @"TryAddSingleton<NpgsqlDataSource>|GetRequiredService<RuntimeDataSource>|GetService<RuntimeDataSource>")]
    private static partial Regex UnkeyedDataSourceUse();

    /// <summary>Never executed: each method is one way around the guard that the IL scan must catch.</summary>
    private static class BypassForms
    {
        public static object TargetTypedConnection(string connectionString)
        {
            NpgsqlConnection connection = new(connectionString);
            return connection;
        }

        public static object QualifiedConnection(string connectionString) =>
            new Npgsql.NpgsqlConnection(connectionString);

        public static object InitializerConnection(string connectionString) =>
            new NpgsqlConnection { ConnectionString = connectionString };

        public static object TargetTypedBuilder(string connectionString)
        {
            NpgsqlDataSourceBuilder builder = new(connectionString);
            return builder;
        }

        public static object SlimBuilder(string connectionString) => new NpgsqlSlimDataSourceBuilder(connectionString);

        public static object StaticCreate(string connectionString) => NpgsqlDataSource.Create(connectionString);

#pragma warning disable CS0618 // The obsolete global mapper is exactly what the guard refuses.
        public static object GlobalMapper() => NpgsqlConnection.GlobalTypeMapper;
#pragma warning restore CS0618

        public static object StandalonePool(string connectionString) =>
            RuntimeDataSource.Create(RuntimeDatabaseRole.Application, connectionString, 1);

        public static Func<object> InLambda(string connectionString) => () => new NpgsqlConnection(connectionString);

        public static async Task<object> InAsyncMethod(string connectionString)
        {
            await Task.Yield();
            return new NpgsqlConnection(connectionString);
        }

        public static DbContextOptionsBuilder EntityFrameworkConnectionString(
            DbContextOptionsBuilder options,
            string connectionString) => options.UseNpgsql(connectionString);

        public static DbContextOptionsBuilder EntityFrameworkConnection(
            DbContextOptionsBuilder options,
            DbConnection connection) => options.UseNpgsql(connection);

        public static DbContextOptionsBuilder<DbContext> EntityFrameworkGenericConnectionString(
            DbContextOptionsBuilder<DbContext> options,
            string connectionString) => options.UseNpgsql(connectionString);

        /// <summary>The allowed form: EF Core on a pool it is handed.</summary>
        public static DbContextOptionsBuilder SharedPool(DbContextOptionsBuilder options, NpgsqlDataSource pool) =>
            options.UseNpgsql(pool);
    }

    /// <summary>
    /// Never executed: a design-time factory is exempt only for the connection string it gives EF Core, never for a
    /// connection it opens itself.
    /// </summary>
    private sealed class DesignTimeFixtureFactory : IDesignTimeDbContextFactory<DbContext>
    {
        public DbContext CreateDbContext(string[] args)
        {
            using var connection = new NpgsqlConnection(args[0]);
            return new DbContext(new DbContextOptionsBuilder().UseNpgsql(args[1]).Options);
        }
    }
}
