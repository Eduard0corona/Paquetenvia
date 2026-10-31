using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Orders.Application.Orders;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-004; AI-05 x-pilot-contract-deltas NTF-RECIPIENT-NOTICES, part NTF-004)
/// and NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N3 choices 3 and 5) over HTTP: the optional WhatsApp consent box of
/// <c>POST /api/v1/orders</c> (<c>recipient_notices</c>) and of the CSV-001 commit
/// (<c>recipient_whatsapp_consent_attested</c> and <c>recipient_whatsapp_consent_version</c>). Absent or unticked reaches the
/// create path as no consent; ticked reaches it with the one accepted version; every other shape is the uniform 409
/// before the create path; and the answer is part of the idempotent request.
/// </summary>
public sealed class RecipientNoticeConsentHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private const string Version = "NTF-RECIPIENT-WHATSAPP-CONSENT-V1";
    private const string PreviewRoute = "/api/v1/orders/csv/preview";
    private const string CommitRoute = "/api/v1/orders/csv/commit";
    private const string Ticked = "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}";
    private static readonly string RecentUtc = DateTimeOffset.UtcNow.AddHours(-1)
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private readonly HttpClient client;
    private readonly OrderHttpWebApplicationFactory factory;

    public RecipientNoticeConsentHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{\"whatsapp_consent_attested\":false}")]
    public async Task An_absent_or_unticked_box_reaches_the_create_path_as_no_consent(string? literal)
    {
        factory.ResetCreateObservations();

        using var response = await PostOrderAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(factory.LastCreateCommand!.WhatsAppConsentVersion);
        // Nothing about the box is ever returned.
        Assert.DoesNotContain("consent", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_ticked_box_reaches_the_create_path_with_the_one_accepted_version()
    {
        factory.ResetCreateObservations();

        using var response = await PostOrderAsync(Guid.NewGuid(), Key(), Ticked);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(RecipientNoticeConsentPolicy.Version, factory.LastCreateCommand!.WhatsAppConsentVersion);
        Assert.DoesNotContain("consent", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> MalformedBoxes() => new()
    {
        "{}",
        "[]",
        "true",
        "\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"",
        "{\"whatsapp_consent_attested\":true}",
        "{\"whatsapp_consent_attested\":\"true\",\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}",
        "{\"whatsapp_consent_attested\":1,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}",
        "{\"whatsapp_consent_attested\":null}",
        "{\"whatsapp_consent_attested\":false,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}",
        "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V2\"}",
        "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":\"ntf-recipient-whatsapp-consent-v1\"}",
        "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":null}",
        "{\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}",
        "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}",
        "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\",\"email_consent_attested\":true}",
        "{\"whatsapp_consent_attested\":true,\"whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\",\"phone\":\"+520000000002\"}",
    };

    [Theory]
    [MemberData(nameof(MalformedBoxes))]
    public async Task Any_other_shape_is_the_uniform_409_before_the_create_path(string literal)
    {
        factory.ResetCreateObservations();

        using var response = await PostOrderAsync(Guid.NewGuid(), Key(), literal);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Conflict.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(0, factory.CreateCallCount);
        // The refusal never echoes what was sent.
        Assert.DoesNotContain("0000000002", problem.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaying_a_key_with_the_other_answer_conflicts_and_the_same_answer_replays()
    {
        var quoteId = Guid.NewGuid();
        var key = Key();

        using var first = await PostOrderAsync(quoteId, key, Ticked);
        using var replay = await PostOrderAsync(quoteId, key, Ticked);
        using var unticked = await PostOrderAsync(quoteId, key, "{\"whatsapp_consent_attested\":false}");
        using var absent = await PostOrderAsync(quoteId, key, null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, unticked.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, absent.StatusCode);

        // Unticked and absent are the same request: either one replays the other.
        var other = Key();
        var otherQuote = Guid.NewGuid();
        using var withoutBox = await PostOrderAsync(otherQuote, other, "{\"whatsapp_consent_attested\":false}");
        using var replayedWithoutBox = await PostOrderAsync(otherQuote, other, null);
        Assert.Equal(HttpStatusCode.Created, withoutBox.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayedWithoutBox.StatusCode);
        Assert.Equal(await withoutBox.Content.ReadAsStringAsync(), await replayedWithoutBox.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ CSV-001 commit (one box for the file)

    [Fact]
    public async Task A_ticked_CSV_box_reaches_every_row_and_the_other_answer_under_the_batch_key_conflicts()
    {
        factory.ResetCreateObservations();
        var csv = Csv(Guid.NewGuid(), Guid.NewGuid());
        var digest = await PreviewAsync(csv);
        var batchKey = $"csv-consent-{Guid.NewGuid():N}";

        using var commit = await CommitAsync(csv, batchKey, digest, ["true"], [Version]);

        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        Assert.Equal(2, factory.CreateCallCount);
        Assert.Equal(RecipientNoticeConsentPolicy.Version, factory.LastCreateCommand!.WhatsAppConsentVersion);

        using var replay = await CommitAsync(csv, batchKey, digest, ["true"], [Version]);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await commit.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, factory.CreateCallCount);

        foreach (var (attested, versions) in new (string[], string[])[] { ([], []), (["false"], []) })
        {
            using var conflict = await CommitAsync(csv, batchKey, digest, attested, versions);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.Equal("IDEMPOTENCY_CONFLICT", problem.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(2, factory.CreateCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_absent_or_unticked_CSV_box_reaches_the_rows_as_no_consent(bool sendFalse)
    {
        factory.ResetCreateObservations();
        var csv = Csv(Guid.NewGuid());
        var digest = await PreviewAsync(csv);

        using var commit = await CommitAsync(
            csv, $"csv-consent-{Guid.NewGuid():N}", digest, sendFalse ? ["false"] : [], []);

        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Null(factory.LastCreateCommand!.WhatsAppConsentVersion);
    }

    public static TheoryData<string[], string[]> MalformedCsvBoxes() => new()
    {
        { ["true"], [] },
        { ["false"], [Version] },
        { [], [Version] },
        { ["true"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V2"] },
        { ["on"], [Version] },
        { ["TRUE"], [Version] },
        { [""], [] },
        { ["true", "true"], [Version] },
        { ["true"], [Version, Version] },
    };

    [Theory]
    [MemberData(nameof(MalformedCsvBoxes))]
    public async Task Any_other_CSV_box_is_the_uniform_409_before_the_batch_is_reserved(string[] attested, string[] versions)
    {
        factory.ResetCreateObservations();
        var csv = Csv(Guid.NewGuid());
        var digest = await PreviewAsync(csv);
        var batchKey = $"csv-consent-{Guid.NewGuid():N}";

        using var commit = await CommitAsync(csv, batchKey, digest, attested, versions);

        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
        using var problem = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        Assert.Equal("CONFLICT", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.CreateCallCount);

        // Nothing was reserved: the same key commits the batch afterwards.
        using var valid = await CommitAsync(csv, batchKey, digest, [], []);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    private async Task<HttpResponseMessage> PostOrderAsync(Guid quoteId, string key, string? recipientNotices)
    {
        var box = recipientNotices is null ? string.Empty : $",\"recipient_notices\":{recipientNotices}";
        var json =
            $"{{\"quote_id\":\"{quoteId:D}\",\"payer_type\":\"RECIPIENT\"," +
            "\"acceptance\":{\"terms_version\":\"terms-synthetic-v1\",\"privacy_version\":\"privacy-synthetic-v1\"," +
            $"\"accepted_at\":\"{RecentUtc}\",\"acceptance_channel\":\"ASSISTED\"}}," +
            $"\"restricted_goods_acknowledged\":true{box}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static string Csv(params Guid[] quotes) =>
        "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel\n" +
        string.Concat(quotes.Select(quote =>
            $"{quote:D},RECIPIENT,terms-synthetic-v1,privacy-synthetic-v1,{RecentUtc},ASSISTED\n"));

    private async Task<string> PreviewAsync(string csv)
    {
        using var preview = await SendCsvAsync(PreviewRoute, csv, null, null, [], []);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("content_digest").GetString()!;
    }

    private Task<HttpResponseMessage> CommitAsync(
        string csv,
        string idempotencyKey,
        string digest,
        string[] attested,
        string[] versions) =>
        SendCsvAsync(CommitRoute, csv, idempotencyKey, digest, attested, versions);

    private Task<HttpResponseMessage> SendCsvAsync(
        string route,
        string csv,
        string? idempotencyKey,
        string? contentDigest,
        string[] attested,
        string[] versions)
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
            content.Add(new StringContent("true"), "restricted_goods_acknowledged");
        }

        foreach (var value in attested)
        {
            content.Add(new StringContent(value), RecipientNoticeConsentPolicy.CsvFieldAttested);
        }

        foreach (var value in versions)
        {
            content.Add(new StringContent(value), RecipientNoticeConsentPolicy.CsvFieldVersion);
        }

        request.Content = content;
        return client.SendAsync(request);
    }

    private static string Key() => $"orders-consent-{Guid.NewGuid():N}";
}
