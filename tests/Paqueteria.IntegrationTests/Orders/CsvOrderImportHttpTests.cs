using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Orders.Application.Csv;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// CSV-001 end-to-end: upload, preview, explicit commit. The order service behind these endpoints
/// is the same ORD-001 abstraction the create endpoint uses, so the guarantees exercised here
/// (nothing created before confirmation, per-row errors, idempotent replay) are the real ones.
/// </summary>
public sealed class CsvOrderImportHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private const string PreviewRoute = "/api/v1/orders/csv/preview";
    private const string CommitRoute = "/api/v1/orders/csv/commit";
    private const string Header = "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel";
    private const string AcceptedAt = "2026-07-22T12:00:00.1234567Z";
    private readonly HttpClient client;
    private readonly OrderHttpWebApplicationFactory factory;

    public CsvOrderImportHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Preview_and_commit_require_authentication()
    {
        using var preview = await client.PostAsync(PreviewRoute, Upload(ValidCsv(Guid.NewGuid())));
        using var commitRequest = new HttpRequestMessage(HttpMethod.Post, CommitRoute)
        {
            Content = Upload(ValidCsv(Guid.NewGuid())),
        };
        commitRequest.Headers.Add("Idempotency-Key", BatchKey());
        using var commit = await client.SendAsync(commitRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, commit.StatusCode);
    }

    [Fact]
    public async Task Preview_and_commit_refuse_a_tenant_the_caller_cannot_see()
    {
        factory.ResetCreateObservations();
        var csv = ValidCsv(Guid.NewGuid());

        using var preview = await SendAsync(
            PreviewRoute, csv, organizationId: MockIdentityProfiles.ForeignOrganizationId);
        using var commit = await SendAsync(
            CommitRoute,
            csv,
            organizationId: MockIdentityProfiles.ForeignOrganizationId,
            idempotencyKey: BatchKey(),
            contentDigest: Digest(csv));

        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, commit.StatusCode);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Preview_reports_valid_and_invalid_rows_and_creates_no_order()
    {
        factory.ResetCreateObservations();
        var valid = Guid.NewGuid();
        var csv =
            $"{Header}\n" +
            $"{valid:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n" +
            $"not-a-guid,SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n" +
            $"{Guid.NewGuid():D},INVALID,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},MOBILE\n";

        using var response = await SendAsync(PreviewRoute, csv);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(Digest(csv), root.GetProperty("content_digest").GetString());
        Assert.Equal(3, root.GetProperty("total_rows").GetInt32());
        Assert.Equal(1, root.GetProperty("valid_rows").GetInt32());
        Assert.Equal(2, root.GetProperty("invalid_rows").GetInt32());
        Assert.Empty(root.GetProperty("file_errors").EnumerateArray());

        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal([2, 3, 4], rows.Select(row => row.GetProperty("row_number").GetInt32()));
        Assert.True(rows[0].GetProperty("valid").GetBoolean());
        Assert.Equal(valid.ToString("D"), rows[0].GetProperty("quote_id").GetString());
        Assert.Equal(
            ["QUOTE_ID_INVALID"],
            rows[1].GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("code").GetString()));
        Assert.Equal(
            ["PAYER_TYPE_INVALID", "ACCEPTANCE_CHANNEL_INVALID"],
            rows[2].GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("code").GetString()));

        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Preview_reports_a_file_level_error_without_row_noise()
    {
        factory.ResetCreateObservations();

        using var response = await SendAsync(
            PreviewRoute,
            $"quote_id,payer_type\n{Guid.NewGuid():D},SENDER\n");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["HEADER_INVALID"],
            body.RootElement.GetProperty("file_errors").EnumerateArray()
                .Select(error => error.GetString()));
        Assert.Empty(body.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Commit_without_a_usable_idempotency_key_creates_nothing()
    {
        factory.ResetCreateObservations();
        var csv = ValidCsv(Guid.NewGuid());

        using var missing = await SendAsync(CommitRoute, csv, contentDigest: Digest(csv));
        using var tooShort = await SendAsync(
            CommitRoute, csv, idempotencyKey: "short", contentDigest: Digest(csv));

        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, tooShort.StatusCode);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-previewed-digest")]
    public async Task Commit_refuses_a_file_that_does_not_match_the_previewed_digest(string? digest)
    {
        factory.ResetCreateObservations();
        var csv = ValidCsv(Guid.NewGuid());

        using var response = await SendAsync(
            CommitRoute, csv, idempotencyKey: BatchKey(), contentDigest: digest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Commit_refuses_a_batch_that_changed_after_preview()
    {
        factory.ResetCreateObservations();
        var previewed = ValidCsv(Guid.NewGuid());
        var tampered = ValidCsv(Guid.NewGuid());

        using var response = await SendAsync(
            CommitRoute, tampered, idempotencyKey: BatchKey(), contentDigest: Digest(previewed));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Commit_rejects_a_file_with_any_invalid_row_and_creates_nothing()
    {
        factory.ResetCreateObservations();
        var csv =
            $"{Header}\n" +
            $"{Guid.NewGuid():D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n" +
            $"{Guid.NewGuid():D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,not-a-date,WEB\n";

        using var response = await SendAsync(
            CommitRoute, csv, idempotencyKey: BatchKey(), contentDigest: Digest(csv));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("invalid_rows").GetInt32());
        Assert.Equal(
            ["ACCEPTED_AT_INVALID"],
            body.RootElement.GetProperty("rows").EnumerateArray().ElementAt(1)
                .GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("code").GetString()));
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task Preview_then_commit_creates_one_order_per_row_and_a_replay_creates_none()
    {
        factory.ResetCreateObservations();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var csv =
            $"{Header}\n" +
            $"{first:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n" +
            $"{second:D},BUSINESS_ACCOUNT,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},API\n";

        using var preview = await SendAsync(PreviewRoute, csv);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        var digest = previewBody.RootElement.GetProperty("content_digest").GetString();
        Assert.Equal(2, previewBody.RootElement.GetProperty("valid_rows").GetInt32());
        Assert.Equal(0, factory.CreateCallCount);

        var batchKey = BatchKey();
        using var commit = await SendAsync(
            CommitRoute, csv, idempotencyKey: batchKey, contentDigest: digest);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var commitBody = await commit.Content.ReadAsStringAsync();
        using var committed = JsonDocument.Parse(commitBody);
        Assert.Equal(digest, committed.RootElement.GetProperty("content_digest").GetString());
        Assert.Equal(2, committed.RootElement.GetProperty("total_rows").GetInt32());
        Assert.Equal(2, committed.RootElement.GetProperty("created_rows").GetInt32());
        Assert.Equal(0, committed.RootElement.GetProperty("failed_rows").GetInt32());
        var rows = committed.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal([2, 3], rows.Select(row => row.GetProperty("row_number").GetInt32()));
        Assert.Equal([first.ToString("D"), second.ToString("D")],
            rows.Select(row => row.GetProperty("quote_id").GetString()));
        Assert.All(rows, row => Assert.Equal("CREATED", row.GetProperty("status").GetString()));
        var orderIds = rows.Select(row => row.GetProperty("order_id").GetGuid()).ToArray();
        Assert.Equal(2, orderIds.Distinct().Count());

        var createCallsAfterCommit = factory.CreateCallCount;
        using var replay = await SendAsync(
            CommitRoute, csv, idempotencyKey: batchKey, contentDigest: digest);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(commitBody, await replay.Content.ReadAsStringAsync());

        // The batch reservation answers the replay on its own: ORD-001 is not reached at all.
        Assert.Equal(createCallsAfterCommit, factory.CreateCallCount);
        foreach (var orderId in orderIds)
        {
            using var detail = await SendAsync(HttpMethod.Get, $"/api/v1/orders/{orderId:D}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        }

        Assert.Equal(2, await CountOrdersForQuotesAsync(first, second));
    }

    [Fact]
    public async Task A_second_batch_key_over_the_same_quotes_reports_per_row_failures_instead_of_duplicating()
    {
        factory.ResetCreateObservations();
        var quoteId = Guid.NewGuid();
        var csv = ValidCsv(quoteId);
        var digest = Digest(csv);

        using var first = await SendAsync(
            CommitRoute, csv, idempotencyKey: BatchKey(), contentDigest: digest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await SendAsync(
            CommitRoute, csv, idempotencyKey: BatchKey(), contentDigest: digest);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("created_rows").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("failed_rows").GetInt32());
        var row = Assert.Single(body.RootElement.GetProperty("rows").EnumerateArray().ToArray());
        Assert.Equal("FAILED", row.GetProperty("status").GetString());
        Assert.Equal("QUOTE_UNAVAILABLE", row.GetProperty("error_code").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("order_id").ValueKind);
        Assert.Equal(1, await CountOrdersForQuotesAsync(quoteId));
    }

    [Fact]
    public async Task An_identical_batch_key_in_another_tenant_never_replays_the_first_tenants_orders()
    {
        factory.ResetCreateObservations();
        var quoteId = Guid.NewGuid();
        var csv = ValidCsv(quoteId);
        var digest = Digest(csv);
        var batchKey = BatchKey();

        using var owner = await SendAsync(
            CommitRoute,
            csv,
            organizationId: MockIdentityProfiles.ViewerOrganizationId,
            idempotencyKey: batchKey,
            contentDigest: digest);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        using var ownerBody = JsonDocument.Parse(await owner.Content.ReadAsStringAsync());
        var ownerOrderId = ownerBody.RootElement.GetProperty("rows").EnumerateArray()
            .Single().GetProperty("order_id").GetGuid();

        using var otherTenant = await SendAsync(
            CommitRoute,
            csv,
            organizationId: MockIdentityProfiles.OperationsOrganizationId,
            bearer: MockIdentityProfiles.ActiveMultiOrganization,
            idempotencyKey: batchKey,
            contentDigest: digest);

        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        var otherBody = await otherTenant.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ownerOrderId.ToString("D"), otherBody, StringComparison.OrdinalIgnoreCase);
        using var other = JsonDocument.Parse(otherBody);
        Assert.Equal(0, other.RootElement.GetProperty("created_rows").GetInt32());
        Assert.Equal(
            "QUOTE_UNAVAILABLE",
            other.RootElement.GetProperty("rows").EnumerateArray().Single()
                .GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Reusing_a_batch_key_for_another_file_is_an_idempotency_conflict_and_creates_nothing()
    {
        factory.ResetCreateObservations();
        var committed = ValidCsv(Guid.NewGuid());
        var other = ValidCsv(Guid.NewGuid());
        var batchKey = BatchKey();

        using var first = await SendAsync(
            CommitRoute, committed, idempotencyKey: batchKey, contentDigest: Digest(committed));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var createCallsAfterCommit = factory.CreateCallCount;

        using var reused = await SendAsync(
            CommitRoute, other, idempotencyKey: batchKey, contentDigest: Digest(other));

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        using var body = JsonDocument.Parse(await reused.Content.ReadAsStringAsync());
        Assert.Equal("IDEMPOTENCY_CONFLICT", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(createCallsAfterCommit, factory.CreateCallCount);
    }

    [Fact]
    public async Task Concurrent_commits_of_one_batch_key_produce_one_batch_and_one_order_per_row()
    {
        factory.ResetCreateObservations();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var csv =
            $"{Header}\n" +
            $"{first:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n" +
            $"{second:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},API\n";
        var digest = Digest(csv);
        var batchKey = BatchKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            SendAsync(CommitRoute, csv, idempotencyKey: batchKey, contentDigest: digest)));
        var bodies = new List<string>(responses.Length);
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
            response.Dispose();
        }

        // Every caller is answered with the same batch, and the batch created each quote once.
        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        using var body = JsonDocument.Parse(bodies[0]);
        Assert.Equal(2, body.RootElement.GetProperty("created_rows").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("failed_rows").GetInt32());
        Assert.Equal(2, await CountOrdersForQuotesAsync(first, second));
    }

    [Fact]
    public async Task One_batch_key_reserved_in_two_tenants_keeps_the_batches_apart()
    {
        factory.ResetCreateObservations();
        var ownerQuote = Guid.NewGuid();
        var otherQuote = Guid.NewGuid();
        var ownerCsv = ValidCsv(ownerQuote);
        var otherCsv = ValidCsv(otherQuote);
        var batchKey = BatchKey();

        using var owner = await SendAsync(
            CommitRoute,
            ownerCsv,
            organizationId: MockIdentityProfiles.ViewerOrganizationId,
            idempotencyKey: batchKey,
            contentDigest: Digest(ownerCsv));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        // A different file under the same key is a conflict inside one tenant, so if the
        // reservation leaked across tenants this second commit could not succeed.
        using var otherTenant = await SendAsync(
            CommitRoute,
            otherCsv,
            organizationId: MockIdentityProfiles.OperationsOrganizationId,
            bearer: MockIdentityProfiles.ActiveMultiOrganization,
            idempotencyKey: batchKey,
            contentDigest: Digest(otherCsv));

        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        using var body = JsonDocument.Parse(await otherTenant.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("created_rows").GetInt32());
        Assert.Equal(
            otherQuote.ToString("D"),
            body.RootElement.GetProperty("rows").EnumerateArray().Single()
                .GetProperty("quote_id").GetString());
    }

    private async Task<int> CountOrdersForQuotesAsync(params Guid[] quoteIds)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Count(item => quoteIds.Contains(item.GetProperty("quote_id").GetGuid()));
    }

    private Task<HttpResponseMessage> SendAsync(
        string route,
        string csv,
        Guid? organizationId = null,
        string? bearer = null,
        string? idempotencyKey = null,
        string? contentDigest = null)
    {
        var request = Authenticated(HttpMethod.Post, route, organizationId, bearer);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        request.Content = Upload(csv, contentDigest);
        return client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route)
    {
        using var request = Authenticated(method, route);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Authenticated(
        HttpMethod method,
        string route,
        Guid? organizationId = null,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", bearer ?? MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add(
            "X-Organization-Id",
            (organizationId ?? MockIdentityProfiles.ViewerOrganizationId).ToString("D"));
        return request;
    }

    private static MultipartFormDataContent Upload(string csv, string? contentDigest = null)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var content = new MultipartFormDataContent { { file, "file", "orders.csv" } };
        if (contentDigest is not null)
        {
            content.Add(new StringContent(contentDigest), "content_digest");
        }

        return content;
    }

    private static string ValidCsv(Guid quoteId) =>
        $"{Header}\n{quoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n";

    private static string Digest(string csv) =>
        CsvOrderImportPrevalidator.ComputeContentDigest(Encoding.UTF8.GetBytes(csv));

    private static string BatchKey() => $"csv-batch-{Guid.NewGuid():N}";
}
