using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// D6-COD-EXPECTED over HTTP: <c>cod_expected_cents</c> on <c>POST /api/v1/orders</c> is optional, a plain
/// non-negative int64 literal, part of the idempotent request, and never echoed by the Order response.
/// </summary>
public sealed class OrderCodExpectedHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private static readonly string RecentUtc = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddHours(-1).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            TimeSpan.Zero)
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private readonly HttpClient client;
    private readonly OrderHttpWebApplicationFactory factory;

    public OrderCodExpectedHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData(null, 0L)]
    [InlineData("null", 0L)]
    [InlineData("0", 0L)]
    [InlineData("15050", 15_050L)]
    [InlineData("2000000", 2_000_000L)]
    public async Task POST_carries_the_declared_COD_to_the_create_path(string? literal, long expected)
    {
        factory.ResetCreateObservations();

        using var response = await PostAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(expected, factory.LastCreateCommand!.CodExpectedCents);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cod", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("150.5")]
    [InlineData("150.0")]
    [InlineData("1e3")]
    [InlineData("1E+3")]
    [InlineData("9223372036854775808")]
    [InlineData("2000001")]
    [InlineData("9223372036854775807")]
    [InlineData("\"150\"")]
    [InlineData("\"150.50\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task POST_rejects_any_COD_literal_that_is_not_a_plain_non_negative_integer(string literal)
    {
        factory.ResetCreateObservations();

        using var response = await PostAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Conflict.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task POST_replaying_a_key_with_another_COD_is_a_conflict_and_the_same_COD_replays()
    {
        var quoteId = Guid.NewGuid();
        var key = Key();

        using var first = await PostAsync(quoteId, key, "15050");
        using var replay = await PostAsync(quoteId, key, "15050");
        using var otherAmount = await PostAsync(quoteId, key, "15051");
        using var noAmount = await PostAsync(quoteId, key, null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, otherAmount.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, noAmount.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(Guid quoteId, string key, string? codLiteral)
    {
        var cod = codLiteral is null ? string.Empty : $",\"cod_expected_cents\":{codLiteral}";
        var json =
            $"{{\"quote_id\":\"{quoteId:D}\",\"payer_type\":\"RECIPIENT\"," +
            "\"acceptance\":{\"terms_version\":\"terms-synthetic-v1\",\"privacy_version\":\"privacy-synthetic-v1\"," +
            $"\"accepted_at\":\"{RecentUtc}\",\"acceptance_channel\":\"ASSISTED\"}}," +
            $"\"restricted_goods_acknowledged\":true{cod}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static string Key() => $"orders-cod-{Guid.NewGuid():N}";
}
