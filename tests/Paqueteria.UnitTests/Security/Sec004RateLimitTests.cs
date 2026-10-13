using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Security.RateLimiting;

namespace Paqueteria.UnitTests.Security;

/// <summary>SEC-004 (phase 4 PR 2): the shared 429 shape, the partition keys, the registry and the rejection writer.</summary>
public sealed class Sec004RateLimitTests
{
    private const string Policy = "Synthetic.Policy";

    [Theory]
    [InlineData(61.2, 62)]
    [InlineData(60, 60)]
    [InlineData(0.001, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(300, 300)]
    public void Retry_after_is_whole_seconds_rounded_up_and_at_least_one(double seconds, long expected) =>
        Assert.Equal(expected, TooManyRequestsProblem.RetryAfterSeconds(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_429_shape_is_AI05_TooManyRequests()
    {
        Assert.Equal(429, TooManyRequestsProblem.Status);
        Assert.Equal("about:blank", TooManyRequestsProblem.Type);
        Assert.Equal("Too Many Requests", TooManyRequestsProblem.Title);
        Assert.Equal("application/problem+json", TooManyRequestsProblem.ContentType);
    }

    [Fact]
    public void Subject_partition_is_the_hash_gps_ingestion_used_and_never_the_subject()
    {
        const string subject = "synthetic-subject-sec004";
        var key = SubjectPartition.Key(subject);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"identity:{subject}"))), key);
        Assert.Matches("^[0-9A-F]{64}$", key);
        Assert.DoesNotContain(subject, key, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(key, SubjectPartition.Key("synthetic-subject-other"));
        Assert.ThrowsAny<ArgumentException>(() => SubjectPartition.Key(" "));
    }

    [Fact]
    public void Partition_keys_follow_the_policy_kind()
    {
        var anonymousA = Context("203.0.113.10", subject: null);
        var anonymousB = Context("203.0.113.11", subject: null);
        var signedInA = Context("203.0.113.10", subject: "synthetic-subject-a");
        var signedInElsewhere = Context("203.0.113.11", subject: "synthetic-subject-a");
        var blankSubject = Context("203.0.113.10", subject: "  ");

        // Anonymous policy: always the client network, even for a signed-in caller.
        Assert.Equal(
            ClientNetworkPartition.Key(IPAddress.Parse("203.0.113.10")),
            RateLimitPartitionKeys.For(RateLimitPartitionKind.ClientNetwork, signedInA));
        Assert.NotEqual(
            RateLimitPartitionKeys.For(RateLimitPartitionKind.ClientNetwork, anonymousA),
            RateLimitPartitionKeys.For(RateLimitPartitionKind.ClientNetwork, anonymousB));

        // Subject policy: the subject hash wherever the caller is; the network when there is no subject.
        Assert.Equal(
            SubjectPartition.Key("synthetic-subject-a"),
            RateLimitPartitionKeys.For(RateLimitPartitionKind.SubjectOrClientNetwork, signedInA));
        Assert.Equal(
            RateLimitPartitionKeys.For(RateLimitPartitionKind.SubjectOrClientNetwork, signedInA),
            RateLimitPartitionKeys.For(RateLimitPartitionKind.SubjectOrClientNetwork, signedInElsewhere));
        Assert.Equal(
            ClientNetworkPartition.Key(IPAddress.Parse("203.0.113.10")),
            RateLimitPartitionKeys.For(RateLimitPartitionKind.SubjectOrClientNetwork, anonymousA));
        Assert.Equal(
            ClientNetworkPartition.Key(IPAddress.Parse("203.0.113.10")),
            RateLimitPartitionKeys.For(RateLimitPartitionKind.SubjectOrClientNetwork, blankSubject));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateLimitPartitionKeys.For((RateLimitPartitionKind)42, anonymousA));
    }

    [Fact]
    public void The_registry_refuses_duplicate_empty_or_unknown_policies_and_is_registered_once()
    {
        var policy = Synthetic();

        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry([]));
        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry([policy, policy with { }]));
        Assert.ThrowsAny<ArgumentException>(() => new RateLimitPolicyRegistry([policy with { Name = " " }]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateLimitPolicyRegistry([policy with { Partition = (RateLimitPartitionKind)42 }]));

        var registry = new RateLimitPolicyRegistry([policy]);
        Assert.Same(policy, registry.Find(Policy));
        Assert.Null(registry.Find("Other"));
        Assert.Null(registry.Find(null));
        Assert.Same(policy, registry.For(Endpoint(Policy)));
        Assert.Null(registry.For(new Endpoint(null, new EndpointMetadataCollection(), "unlimited")));

        var services = new ServiceCollection().AddPaqueteriaRateLimiting(policy);
        Assert.Throws<InvalidOperationException>(() => services.AddPaqueteriaRateLimiting(Synthetic("Second")));
    }

    [Fact]
    public async Task The_writer_counts_by_policy_and_falls_back_to_the_window_when_the_lease_has_no_retry_after()
    {
        var counted = 0;
        await using var provider = Services(Synthetic() with { CountRejection = _ => counted++ });
        var context = Rejected(provider, Endpoint(Policy));

        await RateLimitRejectionWriter.WriteAsync(new OnRejectedContext { HttpContext = context, Lease = new NoMetadataLease() }, default);

        Assert.Equal(1, counted);
        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("90", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var body = Body(context);
        Assert.Contains("\"type\":\"about:blank\"", body, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"Too Many Requests\"", body, StringComparison.Ordinal);
        Assert.Contains("\"status\":429", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_writer_answers_public_tracking_surfaces_with_the_uniform_body_and_policy_headers()
    {
        await using var provider = Services(Synthetic() with { SendsRetryAfter = false });
        var context = Rejected(provider, Endpoint(Policy, PublicTrackingRateLimitRejection.Instance));

        await RateLimitRejectionWriter.WriteAsync(new OnRejectedContext { HttpContext = context, Lease = new NoMetadataLease() }, default);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal("""{"type":"about:blank","title":"Too Many Requests","status":429}""", Body(context));
    }

    [Fact]
    public async Task The_writer_refuses_a_rejection_from_an_unregistered_policy()
    {
        await using var provider = Services(Synthetic());
        var context = Rejected(provider, Endpoint("Unregistered"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RateLimitRejectionWriter.WriteAsync(
                new OnRejectedContext { HttpContext = context, Lease = new NoMetadataLease() },
                default));
    }

    private static RateLimitPolicy Synthetic(string name = Policy) =>
        new(name, RateLimitPartitionKind.SubjectOrClientNetwork, static _ => new FixedWindowLimit(5, TimeSpan.FromSeconds(90)));

    private static ServiceProvider Services(RateLimitPolicy policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddPaqueteriaRateLimiting(policy);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Rejected(IServiceProvider services, Endpoint endpoint)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.SetEndpoint(endpoint);
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static Endpoint Endpoint(string policy, params object[] metadata) =>
        new(null, new EndpointMetadataCollection([new EnableRateLimitingAttribute(policy), .. metadata]), "synthetic");

    private static DefaultHttpContext Context(string address, string? subject)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (subject is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Synthetic"));
        }

        return context;
    }

    private sealed class NoMetadataLease : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
