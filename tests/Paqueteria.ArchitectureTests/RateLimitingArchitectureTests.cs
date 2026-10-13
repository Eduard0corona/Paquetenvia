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
    private const string RateLimitingFolder = "src/BuildingBlocks/Paqueteria.Infrastructure/Security/RateLimiting/";
    private const string Registry = RateLimitingFolder + "RateLimitRegistry.cs";
    private const string PolicyTable = "src/Paqueteria.Api/Http/ApiRateLimitPolicies.cs";

    /// <summary>Rate-limiter API that only the shared registry may use.</summary>
    private static readonly string[] RegistryOnlyTokens =
    [
        "AddRateLimiter(",
        "OnRejected =",
        "RejectionStatusCode",
        "GlobalLimiter",
        "RateLimitPartition.",
        "PartitionedRateLimiter",
        "FixedWindowRateLimiterOptions",
        "SlidingWindowRateLimiterOptions",
        "TokenBucketRateLimiterOptions",
        "ConcurrencyLimiterOptions",
        "AddFixedWindowLimiter(",
        "AddSlidingWindowLimiter(",
        "AddTokenBucketLimiter(",
        "AddConcurrencyLimiter(",
        "IRateLimiterPolicy",
        "using System.Threading.RateLimiting;",
        "using Microsoft.AspNetCore.RateLimiting;",
        "EnableRateLimiting",
        "DisableRateLimiting",
    ];

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
        var violations = SourceFiles()
            .Where(file => !file.Path.StartsWith(RateLimitingFolder, StringComparison.Ordinal))
            .SelectMany(file => RegistryOnlyTokens
                .Where(token => file.Code.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{file.Path}: {token}"))
            .ToArray();

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
        Assert.True(
            program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal) <
                program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal),
            "the limiter runs after authentication, so authenticated callers are partitioned by subject");
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

    private static IEnumerable<(string Path, string Code)> SourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (
                Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/'),
                WithoutComments(File.ReadAllText(path))));

    private static string WithoutComments(string source) =>
        LineCommentPattern().Replace(BlockCommentPattern().Replace(source, string.Empty), string.Empty);

    [GeneratedRegex(@"\.RequireRateLimiting\((?<argument>[^)]*)\)")]
    private static partial Regex RequireRateLimitingPattern();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"(?m)^\s*//.*$")]
    private static partial Regex LineCommentPattern();
}
