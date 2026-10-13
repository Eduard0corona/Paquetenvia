using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Security.RateLimiting;
using Paqueteria.UnitTests.Operations;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// SEC-004 (phase 4 PR 3): a window two policies share (CSV preview and commit, owner answer Q1), the in-flight rule
/// ("1 importación a la vez"), the rejecting policy's name on every rejection, the sign-in page for browser
/// navigations (owner answer Q6) and the OBS-002 rejection summary with registry names only (owner answer Q8).
/// </summary>
public sealed class Sec004HeavyEndpointLimitTests
{
    private const string Preview = "Synthetic.Preview";
    private const string Commit = "Synthetic.Commit";
    private const string Plain = "Synthetic.Plain";
    private const string PartitionA = "synthetic-partition-a";
    private const string PartitionB = "synthetic-partition-b";

    private static readonly Func<IServiceProvider, FixedWindowLimit> Shared =
        static _ => new FixedWindowLimit(3, TimeSpan.FromSeconds(60));

    [Fact]
    public void The_registry_refuses_an_invalid_shared_window_or_in_flight_rule()
    {
        var owner = new RateLimitPolicy(Preview, RateLimitPartitionKind.SubjectOrClientNetwork, Shared);
        var sharer = new RateLimitPolicy(Commit, RateLimitPartitionKind.SubjectOrClientNetwork, Shared)
        {
            SharesWindowOf = Preview,
            InFlightLimit = 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicyRegistry([owner, sharer with { InFlightLimit = 0 }]));
        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry([owner, sharer with { SharesWindowOf = "Unregistered" }]));
        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry([owner, sharer with { SharesWindowOf = Commit }]));
        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry(
            [owner, sharer, sharer with { Name = "Synthetic.Chained", SharesWindowOf = Commit }]));
        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry(
            [owner, sharer with { Partition = RateLimitPartitionKind.ClientNetwork }]));
        // Two lambdas with the same numbers are still two limits: the shared window must use the owner's accessor.
        Assert.Throws<ArgumentException>(() => new RateLimitPolicyRegistry(
            [owner, sharer with { Limit = static _ => new FixedWindowLimit(3, TimeSpan.FromSeconds(60)) }]));

        var plain = new RateLimitPolicy(Plain, RateLimitPartitionKind.SubjectOrClientNetwork, Shared);
        var registry = new RateLimitPolicyRegistry([owner, sharer, plain]);
        Assert.True(registry.UsesBudgets(owner));
        Assert.True(registry.UsesBudgets(sharer));
        Assert.False(registry.UsesBudgets(plain));
        Assert.Equal(Preview, RateLimitBudgets.WindowOwner(sharer));
        Assert.Equal(Plain, RateLimitBudgets.WindowOwner(plain));
    }

    [Fact]
    public void Two_policies_spend_one_shared_window_per_partition()
    {
        using var budgets = Budgets(out var owner, out var sharer);
        var preview = budgets.Limiter(owner, PartitionA, owner.Limit(null!));
        var commit = budgets.Limiter(sharer, PartitionA, sharer.Limit(null!));
        var otherUser = budgets.Limiter(owner, PartitionB, owner.Limit(null!));

        using (var first = preview.AttemptAcquire())
        using (var second = commit.AttemptAcquire())
        using (var third = preview.AttemptAcquire())
        {
            Assert.True(first.IsAcquired && second.IsAcquired && third.IsAcquired);
        }

        // The fourth request of the same partition, preview or commit, is over the shared 3.
        using var overPreview = preview.AttemptAcquire();
        using var overCommit = commit.AttemptAcquire();
        Assert.False(overPreview.IsAcquired);
        Assert.False(overCommit.IsAcquired);
        // Both rejections name the window's owner and carry the window as Retry-After.
        Assert.True(overCommit.TryGetMetadata(RateLimitLeaseMetadata.RejectedBy, out var rejectedBy));
        Assert.Equal(Preview, rejectedBy);
        Assert.True(overCommit.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.True(overPreview.TryGetMetadata(RateLimitLeaseMetadata.RejectedBy, out var previewRejectedBy));
        Assert.Equal(Preview, previewRejectedBy);

        // Another partition keeps its own budget.
        using var other = otherUser.AttemptAcquire();
        Assert.True(other.IsAcquired);
    }

    [Fact]
    public async Task A_second_commit_of_a_partition_is_rejected_while_the_first_is_in_flight()
    {
        using var budgets = Budgets(out var owner, out var sharer, permits: 10);
        var commit = budgets.Limiter(sharer, PartitionA, sharer.Limit(null!));
        var preview = budgets.Limiter(owner, PartitionA, owner.Limit(null!));
        var otherUser = budgets.Limiter(sharer, PartitionB, sharer.Limit(null!));

        var first = commit.AttemptAcquire();
        Assert.True(first.IsAcquired);
        Assert.Null(commit.IdleDuration);

        using (var second = await commit.AcquireAsync())
        {
            Assert.False(second.IsAcquired);
            Assert.True(second.TryGetMetadata(RateLimitLeaseMetadata.RejectedBy, out var rejectedBy));
            Assert.Equal(Commit, rejectedBy);
            // The in-flight rule gives no retry hint of its own: the writer uses the policy's window.
            Assert.False(second.TryGetMetadata(MetadataName.RetryAfter, out _));
        }

        // A preview is not a commit, and another user's commit is not this one.
        using (var previewing = preview.AttemptAcquire())
        using (var otherCommit = otherUser.AttemptAcquire())
        {
            Assert.True(previewing.IsAcquired);
            Assert.True(otherCommit.IsAcquired);
        }

        first.Dispose();
        first.Dispose();
        Assert.NotNull(commit.IdleDuration);
        using var afterwards = commit.AttemptAcquire();
        Assert.True(afterwards.IsAcquired);
    }

    [Fact]
    public void An_in_flight_rejection_spends_nothing_from_the_shared_window()
    {
        using var budgets = Budgets(out var owner, out var sharer, permits: 2);
        var commit = budgets.Limiter(sharer, PartitionA, sharer.Limit(null!));
        var preview = budgets.Limiter(owner, PartitionA, owner.Limit(null!));

        using var running = commit.AttemptAcquire();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var refused = commit.AttemptAcquire();
            Assert.False(refused.IsAcquired);
        }

        // One permit of 2 was spent by the running commit; the refused ones spent none.
        using var previewing = preview.AttemptAcquire();
        using var over = preview.AttemptAcquire();
        Assert.True(previewing.IsAcquired);
        Assert.False(over.IsAcquired);
    }

    [Fact]
    public async Task The_writer_counts_and_waits_by_the_policy_that_rejected()
    {
        var window = new RateLimitPolicy(Preview, RateLimitPartitionKind.SubjectOrClientNetwork, Shared);
        var inFlight = new RateLimitPolicy(
            Commit,
            RateLimitPartitionKind.SubjectOrClientNetwork,
            Shared)
        {
            SharesWindowOf = Preview,
            InFlightLimit = 1,
        };
        await using var provider = Services(window, inFlight);
        var summary = provider.GetRequiredService<RateLimitRejectionSummary>();
        var context = Rejected(provider, EndpointFor(Commit));

        await RateLimitRejectionWriter.WriteAsync(
            new OnRejectedContext { HttpContext = context, Lease = new NamedLease(Commit, retryAfter: null) },
            default);

        // In-flight rejection: counted under the commit policy, with the commit policy's window as Retry-After.
        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Contains((Commit, 1L), summary.TakeSnapshot());

        var windowContext = Rejected(provider, EndpointFor(Commit));
        await RateLimitRejectionWriter.WriteAsync(
            new OnRejectedContext { HttpContext = windowContext, Lease = new NamedLease(Preview, TimeSpan.FromSeconds(42)) },
            default);

        // Shared window rejection on the commit endpoint: counted under the window's owner, its own hint.
        Assert.Equal("42", windowContext.Response.Headers.RetryAfter.ToString());
        Assert.Contains((Preview, 1L), summary.TakeSnapshot());

        var unknown = Rejected(provider, EndpointFor(Commit));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RateLimitRejectionWriter.WriteAsync(
                new OnRejectedContext { HttpContext = unknown, Lease = new NamedLease("Unregistered", retryAfter: null) },
                default));
    }

    [Theory]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("application/json, text/html;q=0.1", true)]
    [InlineData("text/html;q=0", false)]
    [InlineData("*/*", false)]
    [InlineData("application/json", false)]
    [InlineData("application/problem+json", false)]
    [InlineData(null, false)]
    public async Task A_navigation_that_accepts_html_gets_the_page_and_any_other_request_the_problem(
        string? accept,
        bool page)
    {
        var policy = new RateLimitPolicy(Plain, RateLimitPartitionKind.ClientNetwork, Shared);
        await using var provider = Services(policy);
        var context = Rejected(provider, EndpointFor(Plain, new RateLimitRejectionPage("Primera frase. Segunda frase.")));
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        await RateLimitRejectionWriter.WriteAsync(
            new OnRejectedContext { HttpContext = context, Lease = new NamedLease(null, TimeSpan.FromSeconds(60)) },
            default);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        if (page)
        {
            Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
            Assert.Equal(RateLimitRejectionPage.ContentSecurityPolicy, context.Response.Headers.ContentSecurityPolicy.ToString());
            Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
            Assert.Contains("<p>Primera frase. Segunda frase.</p>", Body(context), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("application/problem+json", context.Response.ContentType);
            Assert.Contains("\"title\":\"Too Many Requests\"", Body(context), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_page_is_the_message_and_nothing_else()
    {
        const string message =
            "Demasiados intentos de inicio de sesión desde esta red. Espera un minuto e intenta de nuevo.";
        var html = Encoding.UTF8.GetString(new RateLimitRejectionPage(message).Body.Span);

        Assert.StartsWith("<!doctype html>\n<html lang=\"es-MX\">", html, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Demasiados intentos de inicio de sesión desde esta red.</title>", html, StringComparison.Ordinal);
        Assert.Contains($"<p>{message}</p>", html, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "<script", "<style", "<link", "<img", "<a ", "<form", "href=", "src=", "Paquetenvia" })
        {
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("&lt;b&gt;", Encoding.UTF8.GetString(new RateLimitRejectionPage("<b>").Body.Span), StringComparison.Ordinal);
        Assert.ThrowsAny<ArgumentException>(() => new RateLimitRejectionPage(" "));
    }

    [Fact]
    public void The_summary_accepts_registry_names_only_and_reports_each_policy_with_rejections()
    {
        var summary = new RateLimitRejectionSummary([Preview, Commit, Plain]);
        var logger = new CapturingLogger<RateLimitSummaryReporter>();
        var reporter = new RateLimitSummaryReporter(summary, TimeProvider.System, logger);

        Assert.Throws<ArgumentOutOfRangeException>(() => summary.Record("Unregistered"));
        Assert.Throws<ArgumentOutOfRangeException>(() => summary.Record(null!));
        Assert.Throws<ArgumentException>(() => new RateLimitRejectionSummary([]));

        // A window without rejections writes nothing.
        reporter.Report(60);
        Assert.Empty(logger.Entries);

        summary.Record(Commit);
        summary.Record(Preview);
        summary.Record(Commit);
        reporter.Report(60);

        var entries = logger.Entries.ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal(TelemetryEvents.RateLimitSummary, entry.EventId);
            Assert.Equal(4604, entry.EventId.Id);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Equal(
                ["Policy", "Rejected", "WindowSeconds"],
                entry.Values.Keys.Where(key => key != "{OriginalFormat}").Order(StringComparer.Ordinal));
            Assert.All(
                entry.Values.Keys.Where(key => key != "{OriginalFormat}"),
                key => Assert.Contains(key, TelemetryDimensions.Allowed));
            Assert.Equal(60L, entry.Values["WindowSeconds"]);
        });
        Assert.Equal(
            [(Commit, 2L), (Preview, 1L)],
            entries.Select(entry => ((string)entry.Values["Policy"]!, (long)entry.Values["Rejected"]!)));

        // The window resets.
        reporter.Report(60);
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public async Task The_registry_builds_the_summary_from_its_policy_names()
    {
        await using var provider = Services(
            new RateLimitPolicy(Preview, RateLimitPartitionKind.SubjectOrClientNetwork, Shared),
            new RateLimitPolicy(Plain, RateLimitPartitionKind.ClientNetwork, Shared));

        Assert.Equal(
            [Plain, Preview],
            provider.GetRequiredService<RateLimitRejectionSummary>().Policies.Order(StringComparer.Ordinal));
    }

    private static RateLimitBudgets Budgets(out RateLimitPolicy owner, out RateLimitPolicy sharer, int permits = 3)
    {
        Func<IServiceProvider, FixedWindowLimit> limit = permits == 3
            ? Shared
            : _ => new FixedWindowLimit(permits, TimeSpan.FromSeconds(60));
        owner = new RateLimitPolicy(Preview, RateLimitPartitionKind.SubjectOrClientNetwork, limit);
        sharer = new RateLimitPolicy(Commit, RateLimitPartitionKind.SubjectOrClientNetwork, limit)
        {
            SharesWindowOf = Preview,
            InFlightLimit = 1,
        };
        return new RateLimitBudgets(new RateLimitPolicyRegistry([owner, sharer]));
    }

    private static ServiceProvider Services(params RateLimitPolicy[] policies)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddPaqueteriaRateLimiting(policies);
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

    private static Endpoint EndpointFor(string policy, params object[] metadata) =>
        new(null, new EndpointMetadataCollection([new EnableRateLimitingAttribute(policy), .. metadata]), "synthetic");

    /// <summary>A rejected lease as the budgets give it: the rejecting policy's name and, maybe, a retry hint.</summary>
    private sealed class NamedLease(string? rejectedBy, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            new[] { rejectedBy is null ? null : RateLimitLeaseMetadata.RejectedBy.Name, retryAfter is null ? null : MetadataName.RetryAfter.Name }
                .OfType<string>();

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == RateLimitLeaseMetadata.RejectedBy.Name
                ? rejectedBy
                : metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}
