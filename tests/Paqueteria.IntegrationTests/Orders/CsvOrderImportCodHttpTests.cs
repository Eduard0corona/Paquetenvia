using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// D6-COD-EXPECTED over the CSV-001 endpoints: the optional <c>cod_expected_cents</c> column is reported per row in
/// the preview, rejected per row when it is not a plain integer, and committed through the ORD-001 create path.
/// </summary>
public sealed class CsvOrderImportCodHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private const string PreviewRoute = "/api/v1/orders/csv/preview";
    private const string CommitRoute = "/api/v1/orders/csv/commit";
    private const string HeaderWithCod =
        "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel,cod_expected_cents";
    private static readonly string AcceptedAt = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddHours(-1).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            TimeSpan.Zero)
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private readonly HttpClient client;
    private readonly OrderHttpWebApplicationFactory factory;

    public CsvOrderImportCodHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Preview_echoes_each_valid_rows_COD_and_reports_an_invalid_COD_cell()
    {
        factory.ResetCreateObservations();
        var csv =
            $"{HeaderWithCod}\n" +
            $"{Guid.NewGuid():D},RECIPIENT,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},ASSISTED,15050\n" +
            $"{Guid.NewGuid():D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},ASSISTED,\n" +
            $"{Guid.NewGuid():D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},ASSISTED,150.50\n";

        using var response = await SendAsync(PreviewRoute, csv);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("valid_rows").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("invalid_rows").GetInt32());
        var rows = body.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(15_050, rows[0].GetProperty("cod_expected_cents").GetInt64());
        Assert.Equal(0, rows[1].GetProperty("cod_expected_cents").GetInt64());
        Assert.False(rows[2].GetProperty("valid").GetBoolean());
        Assert.False(rows[2].TryGetProperty("cod_expected_cents", out _));
        var error = Assert.Single(rows[2].GetProperty("errors").EnumerateArray().ToArray());
        Assert.Equal("cod_expected_cents", error.GetProperty("column").GetString());
        Assert.Equal("COD_EXPECTED_CENTS_INVALID", error.GetProperty("code").GetString());
        Assert.Equal(0, factory.CreateCallCount);

        using var commit = await SendAsync(
            CommitRoute,
            csv,
            idempotencyKey: $"csv-cod-{Guid.NewGuid():N}",
            contentDigest: body.RootElement.GetProperty("content_digest").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Commit_creates_the_order_with_the_rows_COD_and_another_amount_under_the_key_conflicts()
    {
        factory.ResetCreateObservations();
        var quoteId = Guid.NewGuid();
        var csv =
            $"{HeaderWithCod}\n" +
            $"{quoteId:D},RECIPIENT,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},ASSISTED,15050\n";
        var changed = csv.Replace(",15050\n", ",15051\n", StringComparison.Ordinal);
        var batchKey = $"csv-cod-{Guid.NewGuid():N}";

        using var preview = await SendAsync(PreviewRoute, csv);
        using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        var digest = previewBody.RootElement.GetProperty("content_digest").GetString();
        using var commit = await SendAsync(CommitRoute, csv, idempotencyKey: batchKey, contentDigest: digest);

        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        using var committed = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        Assert.Equal(1, committed.RootElement.GetProperty("created_rows").GetInt32());
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(15_050, factory.LastCreateCommand!.CodExpectedCents);
        Assert.Equal(quoteId, factory.LastCreateCommand.QuoteId);

        using var changedPreview = await SendAsync(PreviewRoute, changed);
        using var changedBody = JsonDocument.Parse(await changedPreview.Content.ReadAsStringAsync());
        var changedDigest = changedBody.RootElement.GetProperty("content_digest").GetString();
        Assert.NotEqual(digest, changedDigest);
        using var conflict = await SendAsync(
            CommitRoute, changed, idempotencyKey: batchKey, contentDigest: changedDigest);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IDEMPOTENCY_CONFLICT", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, factory.CreateCallCount);
    }

    private Task<HttpResponseMessage> SendAsync(
        string route,
        string csv,
        string? idempotencyKey = null,
        string? contentDigest = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var content = new MultipartFormDataContent { { file, "file", "orders.csv" } };
        if (contentDigest is not null)
        {
            content.Add(new StringContent(contentDigest), "content_digest");
        }

        request.Content = content;
        return client.SendAsync(request);
    }
}
