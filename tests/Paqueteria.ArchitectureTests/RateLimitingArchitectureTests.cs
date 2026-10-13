using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// SEC-004 (phase 4 PR 2): one registry holds every rate-limit policy, its partition keys and the one rejection
/// writer. No module registers a limiter, builds a partition or writes a rejection; modules only name a registered
/// policy with <c>RequireRateLimiting</c>, and the host registers the policy table once.
/// </summary>
public sealed partial class RateLimitingArchitectureTests
{
    private const string RateLimitingFolder = RateLimitingGuard.RegistryFolder;
    private const string Registry = RateLimitingFolder + "RateLimitRegistry.cs";
    private const string PolicyTable = "src/Paqueteria.Api/Http/ApiRateLimitPolicies.cs";

    [Fact]
    public void Only_the_shared_registry_calls_AddRateLimiter()
    {
        var callers = SourceFiles()
            .Where(file => file.Code.Contains("AddRateLimiter(", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        Assert.Equal([Registry], callers);
        var registry = Assert.Single(SourceFiles(), file => file.Path == Registry).Code;
        Assert.Equal(1, Regex.Count(registry, @"\.AddRateLimiter\("));
        Assert.Equal(1, Regex.Count(registry, @"options\.OnRejected = RateLimitRejectionWriter\.WriteAsync;"));
    }

    [Fact]
    public void No_module_registers_a_limiter_builds_a_partition_or_writes_a_rejection()
    {
        var files = SourceFiles().ToArray();
        var violations = files.SelectMany(file => RateLimitingGuard.CodeViolations(file.Path, file.Text)).ToArray();

        Assert.True(files.Length > 100, $"Only {files.Length} source files were read.");
        Assert.Empty(violations);
        // The registry itself is what the guard keeps the others from doing.
        Assert.Contains(files, file => file.Path == Registry && file.Code.Contains("AddRateLimiter(", StringComparison.Ordinal));
    }

    [Fact]
    public void No_project_imports_a_rate_limiting_namespace_project_wide()
    {
        var projects = ProjectFiles().ToArray();
        var violations = projects.SelectMany(file => RateLimitingGuard.ProjectViolations(file.Path, file.Text)).ToArray();

        Assert.Contains(projects, file => file.Path == "Directory.Build.props");
        Assert.Contains(projects, file => file.Path.EndsWith("Orders.Endpoints.csproj", StringComparison.Ordinal));
        Assert.Empty(violations);
    }

    [Fact]
    public void Modules_only_name_a_policy_and_the_host_registers_the_table_once()
    {
        var files = SourceFiles().ToArray();
        var requirements = files
            .SelectMany(file => RequireRateLimitingPattern().Matches(file.Code).Select(match => (file.Path, match.Groups["argument"].Value)))
            .ToArray();

        // publicTracking, publishDriverLocation and the three hubs.
        Assert.Equal(5, requirements.Length);
        Assert.All(requirements, requirement => Assert.Matches(@"^[A-Za-z]+\.RateLimitPolicy$", requirement.Value));
        Assert.All(requirements, requirement => Assert.StartsWith("src/Modules/", requirement.Path, StringComparison.Ordinal));

        var table = Assert.Single(files, file => file.Path == PolicyTable).Code;
        foreach (var policy in requirements.Select(requirement => requirement.Value).Distinct(StringComparer.Ordinal))
        {
            Assert.Contains(policy, table, StringComparison.Ordinal);
        }

        Assert.Equal(
            [PolicyTable],
            files.Where(file => file.Code.Contains("AddPaqueteriaRateLimiting(", StringComparison.Ordinal) &&
                                !file.Path.StartsWith(RateLimitingFolder, StringComparison.Ordinal))
                .Select(file => file.Path));

        var program = Assert.Single(files, file => file.Path == "src/Paqueteria.Api/Program.cs").Code;
        Assert.Equal(1, Regex.Count(program, @"builder\.Services\.AddApiRateLimiting\(\);"));
        Assert.Equal(1, Regex.Count(program, @"app\.UseRateLimiter\(\);"));
        var routing = program.IndexOf("app.UseRouting();", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var limiter = program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        // Routing first, so the writer sees the endpoint and its policy; authentication first, so authenticated
        // callers are partitioned by subject.
        Assert.True(routing > 0 && routing < limiter, "the limiter runs after routing");
        Assert.True(authentication > 0 && authentication < limiter, "the limiter runs after authentication");
    }

    [Fact]
    public void Rejections_are_counted_by_policy_name_and_nothing_else()
    {
        var writer = Assert.Single(SourceFiles(), file => file.Path == RateLimitingFolder + "RateLimitRejectionWriter.cs").Code;

        // One counter with one tag, the policy name; no log call, no path, user, address or partition key.
        Assert.Equal(1, Regex.Count(writer, @"CreateCounter<long>\("));
        Assert.Equal(1, Regex.Count(writer, @"new KeyValuePair<string, object\?>\(PolicyTag, policy\)"));
        Assert.Contains("public const string PolicyTag = \"policy\";", writer, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "ILogger", "Log(", "LogInformation", "LogWarning", "LogDebug", "RemoteIpAddress", "HttpContext.User",
                     "context.User", "RateLimitPartitionKeys", "SubjectPartition", "ClientNetworkPartition", "Request.Path",
                 })
        {
            Assert.DoesNotContain(forbidden, writer, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(string Path, string Text, string Code)> SourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(IsSource)
            .Select(path =>
            {
                var text = File.ReadAllText(path);
                return (Relative(path), text, RateLimitingGuard.WithoutComments(text));
            });

    /// <summary>Every project, props and targets file of the solution, the root Directory.Build files included.</summary>
    private static IEnumerable<(string Path, string Text)> ProjectFiles() =>
        new[] { "*.csproj", "*.props", "*.targets" }
            .SelectMany(pattern =>
                Directory.EnumerateFiles(TestRepository.Root, pattern, SearchOption.TopDirectoryOnly)
                    .Concat(new[] { "src", "tests", "tools", "templates" }
                        .Select(TestRepository.GetPath)
                        .Where(Directory.Exists)
                        .SelectMany(root => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))))
            .Where(IsSource)
            .Distinct(StringComparer.Ordinal)
            .Select(path => (Relative(path), File.ReadAllText(path)));

    private static bool IsSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/');

    [GeneratedRegex(@"\.RequireRateLimiting\((?<argument>[^)]*)\)")]
    private static partial Regex RequireRateLimitingPattern();
}
