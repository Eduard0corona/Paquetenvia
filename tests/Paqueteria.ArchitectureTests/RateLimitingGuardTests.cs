using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// SEC-004 review: <see cref="RateLimitingGuard"/> refuses each known way a module could register a limiter, build a
/// partition or write a rejection on its own, as fixture sources at a module path; the production patterns pass.
/// </summary>
public sealed class RateLimitingGuardTests
{
    private const string ModuleFile = "src/Modules/Orders/Orders.Endpoints/RogueRateLimiting.cs";

    /// <summary>The bypass the review reproduced: an alias using, options configured directly, a fully qualified partition.</summary>
    private const string ReviewedBypass = """
        using RL = Microsoft.AspNetCore.RateLimiting;
        using Microsoft.Extensions.DependencyInjection;

        namespace Orders.Endpoints;

        internal static class Rogue
        {
            internal static void Add(IServiceCollection services) =>
                services.Configure<RL.RateLimiterOptions>(o =>
                {
                    o.OnRejected=(_, _) => default;
                    o.AddPolicy("Rogue", _ => new System.Threading.RateLimiting.RateLimitPartition<string>("k", _ => null!));
                });
        }
        """;

    [Fact]
    public void The_reviewed_bypass_is_refused_on_every_count()
    {
        var violations = RateLimitingGuard.CodeViolations(ModuleFile, ReviewedBypass);

        Assert.Contains($"{ModuleFile}: the Microsoft.AspNetCore.RateLimiting namespace", violations);
        Assert.Contains($"{ModuleFile}: the System.Threading.RateLimiting namespace", violations);
        Assert.Contains($"{ModuleFile}: an OnRejected assignment", violations);
        Assert.Contains($"{ModuleFile}: RateLimiterOptions or a limiter's options", violations);
        Assert.Contains($"{ModuleFile}: a rate-limit partition", violations);
    }

    [Theory]
    [InlineData("global using System.Threading.RateLimiting;", "the System.Threading.RateLimiting namespace")]
    [InlineData("global using Microsoft.AspNetCore.RateLimiting;", "the Microsoft.AspNetCore.RateLimiting namespace")]
    [InlineData("using static System.Threading.RateLimiting.RateLimitPartition;", "the System.Threading.RateLimiting namespace")]
    [InlineData("using L = System . Threading . RateLimiting;", "the System.Threading.RateLimiting namespace")]
    [InlineData("var x = typeof(global::System.Threading.RateLimiting.RateLimiter);", "the System.Threading.RateLimiting namespace")]
    [InlineData("services.AddRateLimiter(o => { });", "AddRateLimiter")]
    [InlineData("options.OnRejected += Write;", "an OnRejected assignment")]
    [InlineData("services.Configure<RateLimiterOptions>(o => { });", "RateLimiterOptions or a limiter's options")]
    [InlineData("var o = new FixedWindowRateLimiterOptions { PermitLimit = 1 };", "RateLimiterOptions or a limiter's options")]
    [InlineData("return RateLimitPartition.GetNoLimiter(\"k\");", "a rate-limit partition")]
    [InlineData("options.RejectionStatusCode = 503;", "the rejection status code")]
    [InlineData("options.GlobalLimiter = limiter;", "a global limiter")]
    [InlineData("PartitionedRateLimiter<HttpContext> limiter = null!;", "a partitioned limiter")]
    [InlineData("internal sealed class Policy : IRateLimiterPolicy<string> { }", "a limiter policy type")]
    [InlineData("internal sealed class Rogue : RateLimiter { }", "a RateLimiter subclass")]
    [InlineData("internal sealed class Rogue : ReplenishingRateLimiter { }", "a RateLimiter subclass")]
    [InlineData("internal sealed class Rogue : Limits.RateLimiter { }", "a RateLimiter subclass")]
    [InlineData("var limiter = new FixedWindowRateLimiter(options);", "a limiter instance")]
    [InlineData("var limiter = new Limits.ConcurrencyLimiter(options);", "a limiter instance")]
    [InlineData("options.AddFixedWindowLimiter(\"x\", _ => { });", "a limiter extension")]
    [InlineData("options.AddTokenBucketLimiter(\"x\", _ => { });", "a limiter extension")]
    [InlineData("[EnableRateLimiting(\"x\")] public void M() { }", "a limiter attribute or extension")]
    [InlineData("endpoints.MapGet(\"/\", () => 1).DisableRateLimiting();", "a limiter attribute or extension")]
    public void Each_bypass_is_refused_outside_the_registry(string code, string reason)
    {
        Assert.Contains($"{ModuleFile}: {reason}", RateLimitingGuard.CodeViolations(ModuleFile, code));
        Assert.DoesNotContain(
            RateLimitingGuard.CodeViolations(RateLimitingGuard.RegistryFolder + "Fixture.cs", code),
            violation => violation.EndsWith(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void The_limiter_middleware_is_used_by_the_api_host_only()
    {
        const string code = "app.UseRateLimiter();";

        Assert.Empty(RateLimitingGuard.CodeViolations(RateLimitingGuard.ApiProgram, code));
        Assert.Contains(
            $"{ModuleFile}: UseRateLimiter outside the API host",
            RateLimitingGuard.CodeViolations(ModuleFile, code));
    }

    [Theory]
    [InlineData("""<ItemGroup><Using Include="System.Threading.RateLimiting" /></ItemGroup>""")]
    [InlineData("""<ItemGroup><Using Include="Microsoft.AspNetCore.RateLimiting" Alias="RL" /></ItemGroup>""")]
    [InlineData("""<ItemGroup><using include="Microsoft.AspNetCore.RateLimiting"/></ItemGroup>""")]
    [InlineData("""<ItemGroup><Using Update="System.Threading.RateLimiting" Static="true" /></ItemGroup>""")]
    public void A_project_wide_using_of_a_rate_limiting_namespace_is_refused(string project)
    {
        Assert.NotEmpty(RateLimitingGuard.ProjectViolations("src/Modules/Orders/Orders.Endpoints/Orders.Endpoints.csproj", project));
        Assert.NotEmpty(RateLimitingGuard.ProjectViolations("Directory.Build.props", $"<Project>{project}</Project>"));
    }

    [Fact]
    public void Comments_and_the_production_patterns_pass()
    {
        const string production = """
            using Microsoft.AspNetCore.Builder;
            using Paqueteria.Application.Security;

            // SEC-004: the limiter is registered by the host; System.Threading.RateLimiting stays in the registry.
            /// <summary>Not RateLimiterOptions; see <c>AddRateLimiter</c>.</summary>
            internal static class Endpoints
            {
                internal static void Map(IEndpointRouteBuilder endpoints) =>
                    endpoints.MapGet("/x", () => 1)
                        .RequireRateLimiting(PublicTrackingEndpointDefaults.RateLimitPolicy)
                        .WithMetadata(PublicTrackingRateLimitRejection.Instance);

                internal static (int PermitLimit, TimeSpan Window) RateLimit(IServiceProvider services) => (1, TimeSpan.FromSeconds(1));
                internal static RateLimitPartitionKind Kind => RateLimitPartitionKind.ClientNetwork;
                internal static string Reason => "RATE_LIMITED";
            }
            """;

        Assert.Empty(RateLimitingGuard.CodeViolations(ModuleFile, production));
        Assert.Empty(RateLimitingGuard.ProjectViolations(
            "Directory.Build.props",
            """<Project><!-- <Using Include="System.Threading.RateLimiting" /> --><ItemGroup><Using Include="Xunit" /></ItemGroup></Project>"""));
    }
}
