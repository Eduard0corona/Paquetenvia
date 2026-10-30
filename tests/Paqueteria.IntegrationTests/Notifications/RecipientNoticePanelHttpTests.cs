using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007 required tests "authorization matrix (dispatcher, admin with
/// MFA, other roles, foreign organization)" and "no recipient in the answer; idempotent stop") over the assembled API,
/// with the real service, the synthetic Locations openers (<c>Locations:PiiProtector=Mock</c>) and a recording store in
/// place of PostgreSQL: the stop's shape first, then the capability (DISPATCHER, PLATFORM_ADMIN only with MFA, every other
/// role the generic 403) before the store is touched, the uniform 404 for a malformed, missing, foreign or operated-only
/// order, the coded 409s, 503, <c>Cache-Control: no-store</c>, the exact answer keys, and that no phone, e-mail or hash
/// reaches an answer, a problem or a log line. Every value is synthetic.
/// </summary>
public sealed class RecipientNoticePanelHttpTests : IClassFixture<RecipientNoticePanelHttpTests.Factory>
{
    private const string SyntheticPhone = "+520000000002";
    private const string SyntheticEmail = "destinatario@example.invalid";
    private static readonly Guid ActiveDispatcherId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10");

    private readonly Factory factory;

    public RecipientNoticePanelHttpTests(Factory factory)
    {
        this.factory = factory;
    }

    public static TheoryData<string> OtherRoles => new()
    {
        MockIdentityProfiles.ActiveViewer,
        MockIdentityProfiles.ActiveDriver,
        MockIdentityProfiles.ActiveFinance,
        MockIdentityProfiles.ActiveFinanceMfa,
        MockIdentityProfiles.ActiveAllyAdmin,
        MockIdentityProfiles.ActiveAllyOperator,
        MockIdentityProfiles.ActiveBusinessAdmin,
        MockIdentityProfiles.ActiveBusinessOperator,
    };

    [Fact]
    public async Task Both_operations_require_authentication()
    {
        using var client = factory.CreateClient();
        using var list = await client.GetAsync(ListPath(Store.OwnedOrderId));
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, StopPath(Store.OwnedOrderId));
        request.Headers.Add("Idempotency-Key", Key());
        using var stop = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, stop.StatusCode);
        Assert.Equal(0, factory.Store.CallsFor(Store.OwnedOrderId));
    }

    [Theory]
    [MemberData(nameof(OtherRoles))]
    public async Task Every_other_role_gets_the_generic_403_before_any_order_is_read(string profile)
    {
        var orderId = Guid.NewGuid();
        using var list = await SendAsync(profile, HttpMethod.Get, ListPath(orderId));
        using var stop = await SendAsync(profile, HttpMethod.Post, StopPath(orderId), Key());
        foreach (var response in new[] { list, stop })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("MFA_REQUIRED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        Assert.Equal(0, factory.Store.CallsFor(orderId));
    }

    [Fact]
    public async Task A_platform_admin_without_MFA_gets_MFA_REQUIRED_before_any_order_is_read()
    {
        var orderId = Guid.NewGuid();
        using var list = await SendAsync(MockIdentityProfiles.ActivePlatformAdminNoMfa, HttpMethod.Get, ListPath(orderId));
        using var stop = await SendAsync(MockIdentityProfiles.ActivePlatformAdminNoMfa, HttpMethod.Post, StopPath(orderId), Key());
        foreach (var response in new[] { list, stop })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("MFA_REQUIRED", json.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(0, factory.Store.CallsFor(orderId));
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    public async Task Dispatcher_and_platform_admin_with_MFA_read_the_list_with_exactly_its_keys_and_no_store(string profile)
    {
        using var response = await SendAsync(profile, HttpMethod.Get, ListPath(Store.OwnedOrderId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(
            ["email_on_file", "items", "whatsapp_consent_attested"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(root.GetProperty("whatsapp_consent_attested").GetBoolean());
        Assert.True(root.GetProperty("email_on_file").GetBoolean());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["attempts", "channel", "delivered_at", "last_code", "notice", "notification_id", "sent_at", "status", "updated_at"],
            item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("TRACKING_LINK", item.GetProperty("notice").GetString());
        Assert.Equal("CANCELLED", item.GetProperty("status").GetString());
        Assert.Equal("RECIPIENT_OPTED_OUT", item.GetProperty("last_code").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("sent_at").ValueKind);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, factory.Store.LastListOrganization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task A_missing_or_malformed_key_is_409_INVALID_REQUEST_before_the_capability_or_any_read(string? key)
    {
        var orderId = Guid.NewGuid();
        foreach (var profile in new[] { MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ActiveViewer })
        {
            using var response = await SendAsync(profile, HttpMethod.Post, StopPath(orderId), key);
            await AssertConflictAsync(response, "INVALID_REQUEST");
        }

        Assert.Equal(0, factory.Store.CallsFor(orderId));
    }

    [Fact]
    public async Task A_stop_with_a_body_a_query_or_two_keys_is_409_INVALID_REQUEST()
    {
        var orderId = Guid.NewGuid();
        using (var body = await SendAsync(
                   MockIdentityProfiles.ActiveDispatcher,
                   HttpMethod.Post,
                   StopPath(orderId),
                   Key(),
                   content: new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            await AssertConflictAsync(body, "INVALID_REQUEST");
        }

        using (var query = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId) + "?channel=EMAIL", Key()))
        {
            await AssertConflictAsync(query, "INVALID_REQUEST");
        }

        using var client = factory.CreateClient();
        using var request = Request(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", [Key(), Key()]);
        using var twoKeys = await client.SendAsync(request);
        await AssertConflictAsync(twoKeys, "INVALID_REQUEST");
        Assert.Equal(0, factory.Store.CallsFor(orderId));
    }

    [Fact]
    public async Task Malformed_missing_foreign_and_operated_only_orders_are_the_same_uniform_404()
    {
        var bodies = new List<string>();
        foreach (var path in new[]
                 {
                     "/api/v1/orders/not-a-uuid/recipient-notices",
                     "/api/v1/orders/00000000-0000-0000-0000-000000000000/recipient-notices",
                     ListPath(Store.MissingOrderId),
                     ListPath(Store.OperatedOnlyOrderId),
                 })
        {
            using var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            bodies.Add(Uniform(await response.Content.ReadAsStringAsync()));
        }

        foreach (var orderId in new[] { Store.MissingOrderId, Store.OperatedOnlyOrderId })
        {
            using var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId), Key());
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(Uniform(await response.Content.ReadAsStringAsync()));
        }

        // A dispatcher of another organization asking for this organization's order: the same 404.
        using var foreign = await SendAsync(
            MockIdentityProfiles.ActiveMultiOrganization,
            HttpMethod.Get,
            ListPath(Store.OwnedOrderId),
            organizationId: MockIdentityProfiles.OperationsOrganizationId);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        bodies.Add(Uniform(await foreign.Content.ReadAsStringAsync()));
        using var foreignStop = await SendAsync(
            MockIdentityProfiles.ActiveMultiOrganization,
            HttpMethod.Post,
            StopPath(Store.OwnedOrderId),
            Key(),
            organizationId: MockIdentityProfiles.OperationsOrganizationId);
        Assert.Equal(HttpStatusCode.NotFound, foreignStop.StatusCode);
        bodies.Add(Uniform(await foreignStop.Content.ReadAsStringAsync()));

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_stop_records_both_channels_under_every_key_version_and_a_replay_answers_the_same_and_writes_nothing()
    {
        var orderId = Store.NewOwnedOrder();
        var key = Key();
        using var first = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId), key);
        using var replay = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId), key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        var body = await first.Content.ReadAsStringAsync();
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["channels", "order_id", "recorded_at"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(orderId, json.RootElement.GetProperty("order_id").GetGuid());
        Assert.Equal(
            ["WHATSAPP", "EMAIL"],
            json.RootElement.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()));

        var write = Assert.Single(factory.Store.WritesFor(orderId));
        Assert.Equal(ActiveDispatcherId, write.Command.ActorId);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, write.Command.OrganizationId);
        Assert.Equal(key, write.Command.IdempotencyKey);
        // The synthetic placeholders, hashed under the one configured version.
        Assert.Equal(
            Factory.Hash("WHATSAPP", SyntheticPhone),
            Convert.ToHexString(Assert.Single(write.OptOuts[0].Hashes).Hash));
        Assert.Equal(
            Factory.Hash("EMAIL", SyntheticEmail),
            Convert.ToHexString(Assert.Single(write.OptOuts[1].Hashes).Hash));

        // Another press is a new key: recorded (and audited) again, the same channels.
        using var again = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId), Key());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, factory.Store.WritesFor(orderId).Count);
    }

    [Fact]
    public async Task The_coded_409s_and_503_carry_no_value()
    {
        using var noContact = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(Store.NoContactOrderId), Key());
        await AssertConflictAsync(noContact, "RECIPIENT_CONTACT_UNAVAILABLE");
        Assert.Empty(factory.Store.WritesFor(Store.NoContactOrderId));

        var orderId = Store.NewOwnedOrder();
        var key = Key();
        using var first = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(orderId), key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var reused = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(Store.NewOwnedOrder()), key);
        await AssertConflictAsync(reused, "IDEMPOTENCY_CONFLICT");

        using var unavailableList = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Get, ListPath(Store.UnavailableOrderId));
        using var unavailableStop = await SendAsync(MockIdentityProfiles.ActiveDispatcher, HttpMethod.Post, StopPath(Store.UnavailableOrderId), Key());
        foreach (var response in new[] { unavailableList, unavailableStop })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task No_phone_e_mail_or_hash_reaches_an_answer_a_problem_or_a_log_line()
    {
        var orderId = Store.NewOwnedOrder();
        var answers = new StringBuilder();
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, ListPath(Store.OwnedOrderId)),
                     (HttpMethod.Post, StopPath(orderId)),
                     (HttpMethod.Post, StopPath(Store.NoContactOrderId)),
                     (HttpMethod.Post, StopPath(Store.UnavailableOrderId)),
                     (HttpMethod.Post, StopPath(Store.MissingOrderId)),
                 })
        {
            using var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, method, path, method == HttpMethod.Post ? Key() : null);
            answers.Append(await response.Content.ReadAsStringAsync());
        }

        var hashes = factory.Store.WritesFor(orderId)
            .SelectMany(write => write.OptOuts)
            .SelectMany(optOut => optOut.Hashes)
            .SelectMany(hash => new[] { Convert.ToHexString(hash.Hash), Convert.ToHexStringLower(hash.Hash), Convert.ToBase64String(hash.Hash) })
            .ToArray();
        Assert.NotEmpty(hashes);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("lane PANEL_STOP, outcome RECORDED", StringComparison.Ordinal));
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("lane PANEL_STOP, outcome NO_USABLE_CONTACT", StringComparison.Ordinal));
        foreach (var value in hashes.Concat([SyntheticPhone, "0000000002", SyntheticEmail, Factory.LookupKeyBase64]))
        {
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(value, StringComparison.Ordinal));
            Assert.DoesNotContain(value, answers.ToString(), StringComparison.Ordinal);
        }
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    /// <summary>The problem without its per-request traceId.</summary>
    private static string Uniform(string body)
    {
        using var json = JsonDocument.Parse(body);
        return string.Join(
            ',',
            json.RootElement.EnumerateObject()
                .Where(property => property.Name != "traceId")
                .Select(property => $"{property.Name}={property.Value}"));
    }

    private async Task<HttpResponseMessage> SendAsync(
        string profile,
        HttpMethod method,
        string path,
        string? idempotencyKey = null,
        Guid? organizationId = null,
        HttpContent? content = null)
    {
        using var client = factory.CreateClient();
        using var request = Request(profile, method, path, organizationId);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        request.Content = content;
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Request(string profile, HttpMethod method, string path, Guid? organizationId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", (organizationId ?? MockIdentityProfiles.ViewerOrganizationId).ToString("D"));
        return request;
    }

    private static string ListPath(Guid orderId) => $"/api/v1/orders/{orderId:D}/recipient-notices";

    private static string StopPath(Guid orderId) => $"/api/v1/orders/{orderId:D}/recipient-notices/stop";

    private static string Key() => $"ntf007-http-{Guid.NewGuid():N}";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private static readonly byte[] LookupKey = RandomNumberGenerator.GetBytes(32);

        internal static string LookupKeyBase64 { get; } = Convert.ToBase64String(LookupKey);

        internal Store Store { get; } = new();

        internal CapturingLoggerProvider Logs { get; } = new();

        internal static string Hash(string channel, string value) =>
            Convert.ToHexString(HMACSHA256.HashData(LookupKey, Encoding.UTF8.GetBytes($"paquetenvia-contact-v1|{channel}|{value}")));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    // T6 choice 11: the synthetic openers answer the AI-24 placeholders and decrypt nothing.
                    ["Locations:PiiProtector"] = "Mock",
                    ["ContactLookup:CurrentKeyVersion"] = "1",
                    ["ContactLookup:Keys:1"] = LookupKeyBase64,
                    // Everything the host can log, framework categories included, is captured and searched.
                    ["Logging:LogLevel:Default"] = "Trace",
                    ["Logging:LogLevel:Microsoft"] = "Trace",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                }));
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .SetMinimumLevel(LogLevel.Trace)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRecipientNoticePanelStore>();
                services.AddSingleton<IRecipientNoticePanelStore>(Store);
            });
        }
    }

    /// <summary>
    /// The store in place of PostgreSQL: the owner organization's orders, an order it only operates (which the real store
    /// filters out by owner_org_id), stored answers per key and the writes the service asked for.
    /// </summary>
    internal sealed class Store : IRecipientNoticePanelStore
    {
        internal static readonly Guid OwnedOrderId = Guid.Parse("93000000-0000-0000-0000-000000000001");
        internal static readonly Guid MissingOrderId = Guid.Parse("93000000-0000-0000-0000-000000000404");
        internal static readonly Guid OperatedOnlyOrderId = Guid.Parse("93000000-0000-0000-0000-000000000303");
        internal static readonly Guid NoContactOrderId = Guid.Parse("93000000-0000-0000-0000-000000000409");
        internal static readonly Guid UnavailableOrderId = Guid.Parse("93000000-0000-0000-0000-000000000503");

        private static readonly ConcurrentDictionary<Guid, bool> Owned = new() { [OwnedOrderId] = true };
        private readonly ConcurrentDictionary<Guid, int> calls = new();
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<RecipientNoticeStopWrite>> writes = new();
        private readonly ConcurrentDictionary<string, (byte[] Hash, RecipientNoticeStopResult Result)> stored = new();

        internal Guid LastListOrganization { get; private set; }

        internal static Guid NewOwnedOrder()
        {
            var orderId = Guid.NewGuid();
            Owned[orderId] = true;
            return orderId;
        }

        internal int CallsFor(Guid orderId) => calls.GetValueOrDefault(orderId);

        internal IReadOnlyList<RecipientNoticeStopWrite> WritesFor(Guid orderId) =>
            writes.TryGetValue(orderId, out var queue) ? queue.ToArray() : [];

        public Task<RecipientNoticeListResult?> ListAsync(
            Guid actorId,
            Guid organizationId,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            Touch(orderId);
            LastListOrganization = organizationId;
            Fail(orderId);
            if (!IsOwned(organizationId, orderId))
            {
                return Task.FromResult<RecipientNoticeListResult?>(null);
            }

            return Task.FromResult<RecipientNoticeListResult?>(new RecipientNoticeListResult(
                [
                    new RecipientNoticeListItem(
                        Guid.Parse("94000000-0000-0000-0000-000000000001"),
                        "TRACKING_LINK",
                        "WHATSAPP",
                        "CANCELLED",
                        0,
                        "RECIPIENT_OPTED_OUT",
                        null,
                        null,
                        new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero)),
                ],
                true,
                true));
        }

        public Task<RecipientNoticeStopRead?> ReadStopAsync(
            StopOrderRecipientNoticesCommand command,
            byte[] requestHash,
            CancellationToken cancellationToken)
        {
            Touch(command.OrderId);
            Fail(command.OrderId);
            if (Stored(command, requestHash) is { } replay)
            {
                return Task.FromResult<RecipientNoticeStopRead?>(RecipientNoticeStopRead.ForReplay(replay));
            }

            if (command.OrderId == NoContactOrderId)
            {
                return Task.FromResult<RecipientNoticeStopRead?>(RecipientNoticeStopRead.ForContact(null));
            }

            return Task.FromResult(IsOwned(command.OrganizationId, command.OrderId)
                ? RecipientNoticeStopRead.ForContact(new RecipientNoticeStopContact(
                    command.OrganizationId,
                    Guid.NewGuid(),
                    "mock-v1",
                    [1, 2, 3],
                    [4, 5, 6]))
                : null);
        }

        public Task<RecipientNoticeStopResult> RecordStopAsync(RecipientNoticeStopWrite write, CancellationToken cancellationToken)
        {
            if (Stored(write.Command, write.RequestHash) is { } replay)
            {
                return Task.FromResult(replay);
            }

            writes.GetOrAdd(write.Command.OrderId, _ => new ConcurrentQueue<RecipientNoticeStopWrite>()).Enqueue(write);
            var result = new RecipientNoticeStopResult(
                write.Command.OrderId,
                write.OptOuts.Select(optOut => optOut.Channel).ToArray(),
                write.RecordedAt);
            stored[$"{write.Command.OrganizationId:D}:{write.Command.IdempotencyKey}"] = (write.RequestHash, result);
            return Task.FromResult(result);
        }

        private RecipientNoticeStopResult? Stored(StopOrderRecipientNoticesCommand command, byte[] requestHash)
        {
            if (!stored.TryGetValue($"{command.OrganizationId:D}:{command.IdempotencyKey}", out var entry))
            {
                return null;
            }

            return CryptographicOperations.FixedTimeEquals(entry.Hash, requestHash)
                ? entry.Result
                : throw new RecipientNoticeStopConflictException(RecipientNoticeStopConflictCodes.IdempotencyConflict);
        }

        private static bool IsOwned(Guid organizationId, Guid orderId) =>
            organizationId == MockIdentityProfiles.ViewerOrganizationId && Owned.ContainsKey(orderId);

        private void Touch(Guid orderId) => calls.AddOrUpdate(orderId, 1, (_, value) => value + 1);

        private static void Fail(Guid orderId)
        {
            if (orderId == UnavailableOrderId)
            {
                throw new RecipientNoticePanelUnavailableException();
            }
        }
    }
}
