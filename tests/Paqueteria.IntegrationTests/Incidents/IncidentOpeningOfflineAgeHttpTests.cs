using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Incidents.Application.Incidents;
using Incidents.Infrastructure.Incidents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paqueteria.Application;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27 through the real API and a real
/// PostgreSQL store: openIncident refuses an occurred_at older than its configured maximum
/// occurrence age with 409 OFFLINE_OPERATION_EXPIRED before the incident service runs, keeps the
/// configured clock tolerance as an INVALID_REQUEST, and refuses an expired replay even when its
/// idempotency key is still stored. The server clock is fixed so every boundary is exact to the tick.
/// </summary>
public sealed class IncidentOpeningOfflineAgeHttpTests(IncidentResolutionHttpFixture fixture)
    : IClassFixture<IncidentResolutionHttpFixture>
{
    private const string OccurredAtFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    [Fact]
    public async Task A_report_exactly_the_default_72_hours_old_is_opened()
    {
        await using var host = Host(out var clock, out var calls);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await OpenAsync(host, seeded, clock.UtcNow.AddHours(-72));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task One_tick_past_the_default_72_hours_is_expired_before_the_service()
    {
        await using var host = Host(out var clock, out var calls);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var key = NewKey();
        var before = await fixture.ReadOpeningsAsync(seeded.OrderId, key);

        using var response = await OpenAsync(host, seeded, clock.UtcNow.AddHours(-72).AddTicks(-1), key);

        await AssertConflictAsync(response, "OFFLINE_OPERATION_EXPIRED");
        Assert.Equal(0, calls.Count);
        // No reservation, incident, evidence or audit: the refusal wrote nothing.
        var after = await fixture.ReadOpeningsAsync(seeded.OrderId, key);
        Assert.Equal(before, after);
        Assert.Equal(0, after.Reservations);
    }

    [Theory]
    [InlineData(24, 0)]
    [InlineData(96, 60)]
    public async Task A_configured_age_and_tolerance_move_both_boundaries(int ageHours, int skewMinutes)
    {
        await using var host = Host(out var clock, out var calls, ageHours, skewMinutes);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var now = clock.UtcNow;

        using (var oldest = await OpenAsync(host, seeded, now.AddHours(-ageHours)))
        {
            Assert.Equal(HttpStatusCode.Created, oldest.StatusCode);
        }

        using (var latest = await OpenAsync(host, seeded, now.AddMinutes(skewMinutes)))
        {
            Assert.Equal(HttpStatusCode.Created, latest.StatusCode);
        }

        Assert.Equal(2, calls.Count);
        using (var expired = await OpenAsync(host, seeded, now.AddHours(-ageHours).AddTicks(-1)))
        {
            await AssertConflictAsync(expired, "OFFLINE_OPERATION_EXPIRED");
        }

        using (var ahead = await OpenAsync(host, seeded, now.AddMinutes(skewMinutes).AddTicks(1)))
        {
            await AssertConflictAsync(ahead, "INVALID_REQUEST");
        }

        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task A_tightened_age_expires_a_report_the_default_would_accept()
    {
        await using var host = Host(out var clock, out var calls, maximumAgeHours: 24);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await OpenAsync(host, seeded, clock.UtcNow.AddHours(-48));

        await AssertConflictAsync(response, "OFFLINE_OPERATION_EXPIRED");
        Assert.Equal(0, calls.Count);
    }

    [Fact]
    public async Task A_widened_age_accepts_a_report_older_than_the_fixed_72_hours()
    {
        await using var host = Host(out var clock, out var calls, maximumAgeHours: 96);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await OpenAsync(host, seeded, clock.UtcNow.AddHours(-80));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task The_default_clock_tolerance_accepts_five_minutes_ahead_and_refuses_one_tick_more()
    {
        await using var host = Host(out var clock, out var calls);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var key = NewKey();

        using (var ahead = await OpenAsync(host, seeded, clock.UtcNow.AddMinutes(5).AddTicks(1), key))
        {
            // A clock ahead of the tolerance keeps the INVALID_REQUEST AI-05 already documented.
            await AssertConflictAsync(ahead, "INVALID_REQUEST");
        }

        Assert.Equal(0, calls.Count);
        Assert.Equal(0, (await fixture.ReadOpeningsAsync(seeded.OrderId, key)).Reservations);

        using var inside = await OpenAsync(host, seeded, clock.UtcNow.AddMinutes(5));
        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task An_expired_replay_is_refused_even_when_its_key_was_already_used()
    {
        await using var host = Host(out var clock, out var calls);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var key = NewKey();
        var occurredAt = clock.UtcNow.AddHours(-1);
        var body = Body(seeded, occurredAt);

        string original;
        using (var first = await SendAsync(host, seeded.OrderId, body, key))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            original = await first.Content.ReadAsStringAsync();
        }

        // While the report is young the same key replays the stored response.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        using (var replay = await SendAsync(host, seeded.OrderId, body, key))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        }

        var opened = await fixture.ReadOpeningsAsync(seeded.OrderId, key);
        Assert.Equal(1, opened.Reservations);
        Assert.Equal(2, calls.Count);

        // Once occurred_at is one tick older than 72 hours the identical replay never reaches the
        // service, although its key is still stored.
        clock.UtcNow = occurredAt.AddHours(72).AddTicks(1);
        using var stale = await SendAsync(host, seeded.OrderId, body, key);

        await AssertConflictAsync(stale, "OFFLINE_OPERATION_EXPIRED");
        Assert.Equal(2, calls.Count);
        Assert.Equal(opened, await fixture.ReadOpeningsAsync(seeded.OrderId, key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0001-01-01T00:00:00Z")]
    public async Task A_missing_or_default_occurred_at_stays_an_invalid_request(string? occurredAt)
    {
        await using var host = Host(out _, out var calls);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await SendAsync(host, seeded.OrderId, Body(seeded, occurredAt), NewKey());

        await AssertConflictAsync(response, "INVALID_REQUEST");
        Assert.Equal(0, calls.Count);
    }

    [Fact]
    public async Task A_malformed_report_is_an_invalid_request_before_its_age_is_judged()
    {
        await using var host = Host(out var clock, out var calls);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var occurredAt = clock.UtcNow.AddHours(-73).ToString(OccurredAtFormat, CultureInfo.InvariantCulture);

        using var response = await SendAsync(
            host,
            seeded.OrderId,
            Body(seeded, occurredAt, severity: "URGENT"),
            NewKey());

        await AssertConflictAsync(response, "INVALID_REQUEST");
        Assert.Equal(0, calls.Count);
    }

    private WebApplicationFactory<Program> Host(
        out SettableClock clock,
        out OpenCallCounter calls,
        int? maximumAgeHours = null,
        int? skewMinutes = null)
    {
        // Whole seconds near the real clock: exact tick boundaries, and the database clock the
        // seeded rows use stays within minutes of the server clock.
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var settable = new SettableClock(now);
        var counter = new OpenCallCounter();
        var settings = new Dictionary<string, string?> { ["Incidents:PiiProtector"] = "Mock" };
        if (maximumAgeHours is { } hours)
        {
            settings["Incidents:MaximumOccurrenceAgeHours"] = hours.ToString(CultureInfo.InvariantCulture);
        }

        if (skewMinutes is { } minutes)
        {
            settings["Incidents:MaximumOccurrenceSkewMinutes"] = minutes.ToString(CultureInfo.InvariantCulture);
        }

        clock = settable;
        calls = counter;
        return fixture.Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(settable);
                services.RemoveAll<IIncidentService>();
                services.AddScoped<PostgreSqlIncidentService>();
                services.AddSingleton(counter);
                services.AddScoped<IIncidentService, CountingIncidentService>();
            });
        });
    }

    private static Task<HttpResponseMessage> OpenAsync(
        WebApplicationFactory<Program> host,
        SeededIncident seeded,
        DateTimeOffset occurredAt,
        string? key = null) =>
        SendAsync(
            host,
            seeded.OrderId,
            Body(seeded, occurredAt.UtcDateTime.ToString(OccurredAtFormat, CultureInfo.InvariantCulture)),
            key ?? NewKey());

    private static string Body(SeededIncident seeded, DateTimeOffset occurredAt) =>
        Body(seeded, occurredAt.UtcDateTime.ToString(OccurredAtFormat, CultureInfo.InvariantCulture));

    private static string Body(SeededIncident seeded, string? occurredAt, string severity = "MEDIUM")
    {
        var body = new Dictionary<string, object>
        {
            ["type"] = "FAILED_DELIVERY_ATTEMPT",
            ["severity"] = severity,
            ["description"] = IncidentResolutionHttpFixture.SeededDescription,
            ["reason_code"] = "RECIPIENT_ABSENT",
            ["next_action"] = "RESCHEDULED",
            ["evidence_proof_ids"] = new[] { seeded.ProofId },
        };
        if (occurredAt is not null)
        {
            body["occurred_at"] = occurredAt;
        }

        return JsonSerializer.Serialize(body);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> host,
        Guid orderId,
        string body,
        string key)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId:D}/incidents")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", IncidentResolutionHttpFixture.TenantId.ToString("D"));
        return await client.SendAsync(request);
    }

    private static string NewKey() => $"inc001-age-{Guid.NewGuid():N}";

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal("Conflict.", root.GetProperty("title").GetString());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("detail", out _));
    }

    internal sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    internal sealed class OpenCallCounter
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Increment() => Interlocked.Increment(ref count);
    }

    /// <summary>The production service, counted, so a test can prove a refusal never reached it.</summary>
    private sealed class CountingIncidentService(PostgreSqlIncidentService inner, OpenCallCounter counter)
        : IIncidentService
    {
        public Task<IncidentResult> OpenAsync(OpenIncidentCommand command, CancellationToken cancellationToken)
        {
            counter.Increment();
            return inner.OpenAsync(command, cancellationToken);
        }

        public Task<IncidentResult> ResolveAsync(ResolveIncidentCommand command, CancellationToken cancellationToken) =>
            inner.ResolveAsync(command, cancellationToken);
    }
}
