using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Orders.Application.Orders;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02 over HTTP: <c>service_window</c> on <c>POST /api/v1/orders</c> is optional,
/// offset-qualified, normalized to UTC, bounded against server time, part of the idempotent request and echoed
/// as <c>Order.service_window</c>. Every malformed window is the uniform 409 before the create path is reached.
/// </summary>
public sealed class OrderServiceWindowHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    /// <summary>
    /// The server checks the window against its own clock when the request arrives, so every instant is computed
    /// from the clock at the moment it is used, never from a value captured when the class or its member data were
    /// first initialized (which can be minutes before the test runs).
    /// </summary>
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>
    /// How far inside the 5-minute clock tolerance the accepted early start lies. The exact boundaries are pinned
    /// with a fixed clock in <c>OrderServiceWindowTests</c>; over HTTP the margin absorbs the time between building
    /// the request and the server reading its clock.
    /// </summary>
    private static readonly TimeSpan ToleranceMargin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// One acceptance instant for the whole class, an hour back: replays must resend the same request, and it stays
    /// inside the 72-hour acceptance window however long the run takes.
    /// </summary>
    private static readonly string RecentUtc = Text(WholeSeconds(DateTimeOffset.UtcNow.AddHours(-1)));

    private readonly HttpClient client;
    private readonly OrderHttpWebApplicationFactory factory;

    public OrderServiceWindowHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task An_absent_window_reaches_the_create_path_as_no_window(string? literal)
    {
        factory.ResetCreateObservations();

        using var response = await PostAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(factory.LastCreateCommand!.ServiceWindow);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("service_window").ValueKind);
    }

    [Fact]
    public async Task An_offset_qualified_window_is_normalized_to_UTC_and_echoed()
    {
        factory.ResetCreateObservations();
        var from = WholeSeconds(Now.AddHours(2));
        var to = from.AddHours(3);
        // The dispatcher's Mazatlan wall-clock times, written with the pilot zone's -07:00 offset.
        var mazatlan = TimeSpan.FromHours(-7);
        var literal = Window(Text(from.ToOffset(mazatlan)), Text(to.ToOffset(mazatlan)));

        using var response = await PostAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var window = factory.LastCreateCommand!.ServiceWindow!;
        Assert.Equal(from, window.From);
        Assert.Equal(to, window.To);
        Assert.Equal(TimeSpan.Zero, window.From.Offset);
        Assert.Equal(TimeSpan.Zero, window.To.Offset);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var echoed = body.RootElement.GetProperty("service_window");
        Assert.Equal(from, echoed.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(to, echoed.GetProperty("to").GetDateTimeOffset());
        Assert.Equal(["from", "to"], echoed.EnumerateObject().Select(member => member.Name));
    }

    [Fact]
    public async Task A_window_of_exactly_twelve_hours_starting_within_the_clock_tolerance_is_accepted()
    {
        factory.ResetCreateObservations();
        var from = WholeSeconds(Now - OrderServiceWindowPolicy.ClockTolerance + ToleranceMargin);
        Assert.True(from < Now);

        using var response = await PostAsync(Guid.NewGuid(), Key(), Window(Text(from), Text(from.AddHours(12))));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public static TheoryData<string> MalformedWindows()
    {
        var from = WholeSeconds(Now.AddHours(2));
        var to = from.AddHours(2);
        var local = from.ToOffset(TimeSpan.FromHours(-7)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return new TheoryData<string>
        {
            "{}",
            "[]",
            "\"2026-10-02T10:00:00Z\"",
            "true",
            $"{{\"from\":\"{Text(from)}\"}}",
            $"{{\"to\":\"{Text(to)}\"}}",
            $"{{\"from\":\"{Text(from)}\",\"to\":\"{Text(to)}\",\"zone\":\"America/Mazatlan\"}}",
            $"{{\"from\":\"{Text(from)}\",\"from\":\"{Text(from)}\",\"to\":\"{Text(to)}\"}}",
            $"{{\"from\":null,\"to\":\"{Text(to)}\"}}",
            $"{{\"from\":1,\"to\":\"{Text(to)}\"}}",
            // A local time without offset is ambiguous.
            Window(local, Text(to)),
            Window(Text(from).Replace('T', ' '), Text(to)),
            Window(Text(from).ToLowerInvariant(), Text(to)),
            Window(from.ToString("yyyy-MM-dd'T'HH:mm:ss.123'Z'", CultureInfo.InvariantCulture), Text(to)),
            Window(from.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture), Text(to)),
            Window(Text(from), Text(from)),
            Window(Text(to), Text(from)),
            // Longer than 12 hours.
            Window(Text(from), Text(from.AddHours(12).AddSeconds(1))),
            // Already over, started before the tolerance, or too far ahead.
            Window(Text(WholeSeconds(Now.AddHours(-3))), Text(WholeSeconds(Now.AddMinutes(-1)))),
            Window(Text(WholeSeconds(Now.AddMinutes(-10))), Text(WholeSeconds(Now.AddHours(1)))),
            Window(Text(WholeSeconds(Now.AddDays(31))), Text(WholeSeconds(Now.AddDays(31).AddHours(1)))),
        };
    }

    [Theory]
    [MemberData(nameof(MalformedWindows))]
    public async Task Any_other_window_is_the_uniform_409_before_the_create_path(string literal)
    {
        factory.ResetCreateObservations();

        using var response = await PostAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Conflict.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Replaying_a_key_with_another_window_is_a_conflict_and_the_same_window_replays()
    {
        var quoteId = Guid.NewGuid();
        var key = Key();
        var from = WholeSeconds(Now.AddHours(2));
        var window = Window(Text(from), Text(from.AddHours(2)));
        // The same instants written with another offset are the same window.
        var sameInstants = Window(
            Text(from.ToOffset(TimeSpan.FromHours(-7))),
            Text(from.AddHours(2).ToOffset(TimeSpan.FromHours(-7))));

        using var first = await PostAsync(quoteId, key, window);
        using var replay = await PostAsync(quoteId, key, sameInstants);
        using var otherWindow = await PostAsync(quoteId, key, Window(Text(from), Text(from.AddHours(3))));
        using var noWindow = await PostAsync(quoteId, key, null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, otherWindow.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, noWindow.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(Guid quoteId, string key, string? windowLiteral)
    {
        var window = windowLiteral is null ? string.Empty : $",\"service_window\":{windowLiteral}";
        var json =
            $"{{\"quote_id\":\"{quoteId:D}\",\"payer_type\":\"RECIPIENT\"," +
            "\"acceptance\":{\"terms_version\":\"terms-synthetic-v1\",\"privacy_version\":\"privacy-synthetic-v1\"," +
            $"\"accepted_at\":\"{RecentUtc}\",\"acceptance_channel\":\"ASSISTED\"}}{window}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static string Window(string from, string to) => $"{{\"from\":\"{from}\",\"to\":\"{to}\"}}";

    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static string Text(DateTimeOffset value) => value.Offset == TimeSpan.Zero
        ? value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        : value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Key() => $"orders-window-{Guid.NewGuid():N}";
}
