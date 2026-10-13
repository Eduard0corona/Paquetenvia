using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Identity.Infrastructure.Mock;
using Npgsql;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// ORD-ORDER-CREATED-AT-2026-10-12 (owner, "En dos pasos (Recommended)", step 2) end to end: the real API (mock
/// authentication, PostgreSQL identity, tenancy and Orders providers on the least-privilege runtime role under RLS)
/// over a real PostgreSQL baseline. createOrder, listOrders, getOrder and transitionOrder each answer the Order's
/// created_at equal to orders.orders.created_at, written as a strict UTC instant the web accepts
/// (apps/web/src/lib/utc-instant.ts). An idempotent replay answers the same value, also when its response was stored
/// before Order.created_at existed.
/// </summary>
public sealed class OrderCreatedAtPostgreSqlHttpTests(OrderTransitionGuardsHttpFixture fixture)
    : IClassFixture<OrderTransitionGuardsHttpFixture>
{
    private static readonly Guid CityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly HttpClient client = fixture.Api.CreateClient();

    [Fact]
    public async Task Every_Order_operation_answers_the_row_created_at_as_a_strict_UTC_instant()
    {
        var web = WebUtcInstantPattern();
        var quoteId = await SeedActiveQuoteAsync();
        var createKey = NewKey();

        using var created = await client.SendAsync(Create(quoteId, createKey));
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"createOrder: {(int)created.StatusCode} {createdBody}");
        var (orderId, publicId, createdAt) = ReadOrder(createdBody);
        var row = await ReadRowCreatedAtAsync(orderId);
        Assert.Matches(web, createdAt);
        Assert.Equal(row, Parse(createdAt));

        // listOrders (the exact guía and the unfiltered list) and getOrder read the same row.
        foreach (var path in new[] { $"/api/v1/orders?public_id={Uri.EscapeDataString(publicId)}", "/api/v1/orders" })
        {
            using var list = await client.SendAsync(Get(path));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var item = Assert.Single(
                page.RootElement.GetProperty("items").EnumerateArray(),
                candidate => candidate.GetProperty("id").GetGuid() == orderId);
            var listed = item.GetProperty("created_at").GetString()!;
            Assert.Matches(web, listed);
            Assert.Equal(row, Parse(listed));
            Assert.All(
                page.RootElement.GetProperty("items").EnumerateArray(),
                order => Assert.Matches(web, order.GetProperty("created_at").GetString()!));
        }

        using (var detail = await client.SendAsync(Get($"/api/v1/orders/{orderId:D}")))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var (_, _, read) = ReadOrder(await detail.Content.ReadAsStringAsync());
            Assert.Matches(web, read);
            Assert.Equal(row, Parse(read));
        }

        // transitionOrder answers the Order after the change; created_at does not move.
        var transitionKey = NewKey();
        using (var cancelled = await client.SendAsync(Cancel(orderId, transitionKey)))
        {
            var body = await cancelled.Content.ReadAsStringAsync();
            Assert.True(cancelled.StatusCode == HttpStatusCode.OK, $"transitionOrder: {(int)cancelled.StatusCode} {body}");
            using var json = JsonDocument.Parse(body);
            Assert.Equal("CANCELLED", json.RootElement.GetProperty("status").GetString());
            var transitioned = json.RootElement.GetProperty("created_at").GetString()!;
            Assert.Matches(web, transitioned);
            Assert.Equal(row, Parse(transitioned));
        }

        Assert.Equal(row, await ReadRowCreatedAtAsync(orderId));

        // Replays answer the stored response, created_at included.
        await AssertReplayAsync(() => Create(quoteId, createKey), HttpStatusCode.Created, orderId, row, web);
        await AssertReplayAsync(() => Cancel(orderId, transitionKey), HttpStatusCode.OK, orderId, row, web);

        // A response stored before Order.created_at existed has no created_at; its replay reads the row's value.
        Assert.Equal(1, await RemoveStoredCreatedAtAsync(createKey));
        Assert.Equal(1, await RemoveStoredCreatedAtAsync(transitionKey));
        await AssertReplayAsync(() => Create(quoteId, createKey), HttpStatusCode.Created, orderId, row, web);
        await AssertReplayAsync(() => Cancel(orderId, transitionKey), HttpStatusCode.OK, orderId, row, web);
    }

    private async Task AssertReplayAsync(
        Func<HttpRequestMessage> request,
        HttpStatusCode status,
        Guid orderId,
        DateTimeOffset row,
        Regex web)
    {
        using var message = request();
        using var replay = await client.SendAsync(message);
        var body = await replay.Content.ReadAsStringAsync();
        Assert.True(replay.StatusCode == status, $"replay: {(int)replay.StatusCode} {body}");
        var (id, _, createdAt) = ReadOrder(body);
        Assert.Equal(orderId, id);
        Assert.Matches(web, createdAt);
        Assert.Equal(row, Parse(createdAt));
    }

    private static (Guid Id, string PublicId, string CreatedAt) ReadOrder(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var createdAt = root.GetProperty("created_at");
        Assert.Equal(JsonValueKind.String, createdAt.ValueKind);
        return (root.GetProperty("id").GetGuid(), root.GetProperty("public_id").GetString()!, createdAt.GetString()!);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);

    /// <summary>The web's strict UTC instant pattern, read from its single definition.</summary>
    private static Regex WebUtcInstantPattern()
    {
        var source = File.ReadAllText(
            Path.Combine(DriverStopsNextServer.FindRepositoryRoot(), "apps", "web", "src", "lib", "utc-instant.ts"));
        var match = Regex.Match(source, @"const utcInstantPattern = /(?<pattern>.+)/;");
        Assert.True(match.Success, "apps/web/src/lib/utc-instant.ts no longer defines utcInstantPattern.");
        return new Regex(match.Groups["pattern"].Value, RegexOptions.ECMAScript);
    }

    private static HttpRequestMessage Create(Guid quoteId, string key) =>
        Post(
            "/api/v1/orders",
            key,
            JsonSerializer.Serialize(new
            {
                quote_id = quoteId,
                payer_type = "SENDER",
                restricted_goods_acknowledged = true,
                acceptance = new
                {
                    terms_version = "terms-synthetic-v1",
                    privacy_version = "privacy-synthetic-v1",
                    accepted_at = AcceptedAt,
                    acceptance_channel = "WEB",
                },
            }));

    private static HttpRequestMessage Cancel(Guid orderId, string key) =>
        Post(
            $"/api/v1/orders/{orderId:D}/transitions",
            key,
            JsonSerializer.Serialize(new
            {
                target_status = "CANCELLED",
                reason = "Cancelación sintética ORD-ORDER-CREATED-AT.",
                expected_version = 1,
                metadata = new { },
            }));

    /// <summary>One acceptance instant for the whole test class, so a replay sends the very same request.</summary>
    private static readonly string AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static HttpRequestMessage Post(string path, string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request);
        return request;
    }

    private static void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", OrderTransitionGuardsHttpFixture.TenantId.ToString("D"));
    }

    private static string NewKey() => $"ord-created-at-{Guid.NewGuid():N}";

    /// <summary>An ACTIVE quote of the tenant, with its own locations and one package, ready to become an order.</summary>
    private async Task<Guid> SeedActiveQuoteAsync()
    {
        var quoteId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO locations.locations(
              id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version)
            VALUES
              (@origin,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),
               decode('00','hex'),'Synthetic origin','test-v1'),
              (@destination,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),
               decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,
              package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (
              @quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,10000,0,0,10000,
              10000,'MXN','ord-created-at-v1','{}',
              '[{"description":"synthetic package","weight_grams":500,"declared_value_cents":0}]',
              '{}',@input_hash,'ACTIVE',clock_timestamp()+interval '1 day');
            """,
            connection);
        command.Parameters.AddWithValue("origin", Guid.NewGuid());
        command.Parameters.AddWithValue("destination", Guid.NewGuid());
        command.Parameters.AddWithValue("org", OrderTransitionGuardsHttpFixture.TenantId);
        command.Parameters.AddWithValue("city", CityId);
        command.Parameters.AddWithValue("quote", quoteId);
        command.Parameters.AddWithValue("input_hash", RandomNumberGenerator.GetBytes(32));
        await command.ExecuteNonQueryAsync();
        return quoteId;
    }

    private async Task<DateTimeOffset> ReadRowCreatedAtAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(fixture.Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT created_at FROM orders.orders WHERE id=@order", connection);
        command.Parameters.AddWithValue("order", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>Turns the completed response stored under the key into one written before Order.created_at existed.</summary>
    private async Task<int> RemoveStoredCreatedAtAsync(string key)
    {
        await using var connection = new NpgsqlConnection(fixture.Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE platform.idempotency_keys SET response_body=response_body-'created_at'
            WHERE idempotency_key=@key AND response_body ? 'created_at'
            """,
            connection);
        command.Parameters.AddWithValue("key", key);
        return await command.ExecuteNonQueryAsync();
    }
}
