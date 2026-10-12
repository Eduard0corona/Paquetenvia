using System.Text;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Orders;
using Paqueteria.Contracts.Legal;

namespace Paqueteria.UnitTests.Orders;

public sealed class OrderDomainTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QuoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset AcceptedAt =
        new DateTimeOffset(2026, 7, 20, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560);

    [Theory]
    [InlineData("SENDER", PayerType.Sender)]
    [InlineData("RECIPIENT", PayerType.Recipient)]
    [InlineData("BUSINESS_ACCOUNT", PayerType.BusinessAccount)]
    public void Payer_types_are_exact(string value, PayerType expected)
    {
        Assert.True(OrderInputPolicy.TryParsePayerType(value, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(value, actual.ToContractValue());
    }

    [Fact]
    public void New_order_has_DRAFT_version_one_and_int64_money()
    {
        var order = CreateOrder(3_000_000_100L, 100L, 3_000_000_000L);
        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Equal(1, order.Version);
        Assert.Null(order.OperatorOrganizationId);
        Assert.Equal(0, order.CodExpectedCents);
        Assert.True(order.TotalCents > int.MaxValue);
        Assert.Null(order.ClaimWindowEndsAt);
        Assert.Null(order.FinalizedAt);
        Assert.Null(order.ArchivedAt);
    }

    [Fact]
    public void Public_ID_has_stable_url_safe_128_bit_format_and_no_internal_IDs()
    {
        var generator = new CryptographicOrderPublicIdGenerator();
        var values = Enumerable.Range(0, 128).Select(_ => generator.Create()).ToArray();
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, value => Assert.True(OrderPublicIdPolicy.IsValid(value)));
        Assert.All(values, value =>
        {
            Assert.DoesNotContain(OrganizationId.ToString("N"), value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(QuoteId.ToString("N"), value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("2026", value, StringComparison.Ordinal);
        });
        Assert.Equal(16, OrderPublicIdPolicy.EntropyBytes);
    }

    [Fact]
    public void Acceptance_matches_permanent_vector_and_exact_canonical_rules()
    {
        var evidence = Evidence();
        var bytes = OrderAcceptanceCanonicalizer.Canonicalize(evidence).ToArray();
        var json = Encoding.UTF8.GetString(bytes);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Contains("\"order_id\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"accepted_at_client\":\"2026-07-20T12:34:56.1234560Z\"", json, StringComparison.Ordinal);
        Assert.EndsWith("\"acceptance_channel\":\"PWA\"}", json, StringComparison.Ordinal);
        var hash = OrderAcceptanceCanonicalizer.ComputeSha256(evidence);
        Assert.Equal("2a09176e270ddcc52e0fee157f3d5bd869f36047f7f946daa7caed4816ae0b37",
            Convert.ToHexString(hash).ToLowerInvariant());
        Assert.Equal("KgkXbicN3MUuD+4Vfz1b2GnzYEf3+Ubap8rtSBauCzc=", Convert.ToBase64String(hash));
    }

    [Fact]
    public void Acceptance_hash_changes_for_every_field_and_unicode_is_deterministic()
    {
        var source = Evidence();
        var hashes = new[]
        {
            source with { OrderId = Guid.NewGuid() },
            source with { QuoteId = Guid.NewGuid() },
            source with { OwnerOrganizationId = Guid.NewGuid() },
            source with { ActorId = Guid.NewGuid() },
            source with { TermsVersion = "términos-ñ" },
            source with { PrivacyVersion = "privacidad-β" },
            source with { AcceptedAtClient = source.AcceptedAtClient.AddTicks(1) },
            source with { AcceptanceChannel = "api" },
        }.Select(OrderAcceptanceCanonicalizer.ComputeSha256).ToArray();
        var original = OrderAcceptanceCanonicalizer.ComputeSha256(source);
        Assert.All(hashes, hash => Assert.NotEqual(original, hash));
        var unicode = source with { TermsVersion = "términos-ñ" };
        Assert.Equal(
            OrderAcceptanceCanonicalizer.Canonicalize(unicode).ToArray(),
            OrderAcceptanceCanonicalizer.Canonicalize(unicode).ToArray());
    }

    [Fact]
    public void Request_hash_uses_only_normalized_contract_fields()
    {
        var command = Command(ActorId, "request-a");
        var sameContract = Command(Guid.NewGuid(), "request-b");
        Assert.Equal(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(command),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(sameContract));
        Assert.NotEqual(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(command),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(
                command with { Acceptance = command.Acceptance with { TermsVersion = "synthetic-v2" } }));
    }

    [Fact]
    public void Acceptance_policy_rejects_default_and_accepts_UTC_or_offset_timestamps()
    {
        Assert.False(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1", "privacy-synthetic-v1", default, "WEB"));
        Assert.True(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            DateTimeOffset.Parse("2026-07-22T12:00:00.1234567Z", System.Globalization.CultureInfo.InvariantCulture),
            "WEB"));
        Assert.True(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            DateTimeOffset.Parse("2026-07-22T05:00:00.1234567-07:00", System.Globalization.CultureInfo.InvariantCulture),
            "WEB"));
    }

    [Fact]
    public async Task Invalid_internal_acceptance_is_rejected_before_public_ID_or_transaction_side_effects()
    {
        var generator = new CountingPublicIdGenerator();
        var service = new QuoteSnapshotToOrderCoordinator(
            null!,
            generator,
            null!,
            null!,
            null!,
            null!,
            null!);
        var invalid = Command(ActorId, "request-default-accepted-at") with
        {
            Acceptance = new OrderAcceptanceInput(
                "terms-synthetic-v1",
                "privacy-synthetic-v1",
                default,
                "WEB"),
        };

        var exception = await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CreateAsync(invalid, CancellationToken.None));

        Assert.Equal(OrderConflictCode.InvalidRequest, exception.Code);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void Request_hash_normalizes_equivalent_offsets_and_changes_for_another_instant()
    {
        var command = Command(ActorId, "request-hash-offset");
        var utc = command with
        {
            Acceptance = command.Acceptance with
            {
                AcceptedAt = DateTimeOffset.Parse(
                    "2026-07-22T12:00:00.1234567Z",
                    System.Globalization.CultureInfo.InvariantCulture),
            },
        };
        var offset = command with
        {
            Acceptance = command.Acceptance with
            {
                AcceptedAt = DateTimeOffset.Parse(
                    "2026-07-22T05:00:00.1234567-07:00",
                    System.Globalization.CultureInfo.InvariantCulture),
            },
        };
        var different = utc with
        {
            Acceptance = utc.Acceptance with { AcceptedAt = utc.Acceptance.AcceptedAt.AddTicks(1) },
        };

        Assert.Equal(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(utc),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(offset));
        Assert.NotEqual(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(utc),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(different));
    }

    [Fact]
    public void Cursor_round_trips_and_invalid_values_fail_closed()
    {
        var createdAt = new DateTimeOffset(2026, 7, 22, 1, 2, 3, TimeSpan.Zero).AddTicks(4567);
        var id = Guid.NewGuid();
        var binding = OrderCursorCodec.Binding(OrganizationId, null, null, false, null, default);
        var cursor = OrderCursorCodec.Encode(createdAt, id, binding);
        Assert.True(OrderCursorCodec.TryDecode(cursor, binding, out var decodedAt, out var decodedId));
        Assert.Equal(createdAt, decodedAt);
        Assert.Equal(id, decodedId);
        Assert.False(OrderCursorCodec.TryDecode("not/a/cursor", binding, out _, out _));
        Assert.DoesNotContain("2026", cursor, StringComparison.Ordinal);
        Assert.True(cursor.Length <= OrderCursorCodec.MaximumLength);
    }

    /// <summary>
    /// ORD-LIST-DATE-FILTER-2026-10-12: a cursor decodes only for the organization and the filters it was issued
    /// for; another value of any filter, another organization, an earlier unbound cursor, a non-canonical spelling
    /// or an oversized value never decodes.
    /// </summary>
    [Fact]
    public void Cursor_is_bound_to_the_organization_and_every_filter()
    {
        var from = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 8, 6, 59, 59, TimeSpan.Zero).AddTicks(9_999_990);
        var range = new OrderListCreatedRange(from, to);
        var binding = OrderCursorCodec.Binding(OrganizationId, "DELIVERED", OrganizationId, true, null, range);
        var cursor = OrderCursorCodec.Encode(from.AddDays(2), OrderId, binding);
        Assert.True(OrderCursorCodec.TryDecode(cursor, binding, out _, out _));

        // The same binding is computed for the same values, whatever offset an instant was written with.
        Assert.Equal(
            binding,
            OrderCursorCodec.Binding(
                OrganizationId,
                "DELIVERED",
                OrganizationId,
                true,
                null,
                new OrderListCreatedRange(from.ToOffset(TimeSpan.FromHours(-7)), to)));

        foreach (var other in new[]
                 {
                     OrderCursorCodec.Binding(QuoteId, "DELIVERED", OrganizationId, true, null, range),
                     OrderCursorCodec.Binding(OrganizationId, null, OrganizationId, true, null, range),
                     OrderCursorCodec.Binding(OrganizationId, "CLOSED", OrganizationId, true, null, range),
                     OrderCursorCodec.Binding(OrganizationId, "DELIVERED", null, true, null, range),
                     OrderCursorCodec.Binding(OrganizationId, "DELIVERED", OrganizationId, false, null, range),
                     OrderCursorCodec.Binding(
                         OrganizationId, "DELIVERED", OrganizationId, true, "ORD_AAAAAAAAAAAAAAAAAAAAAA", range),
                     OrderCursorCodec.Binding(OrganizationId, "DELIVERED", OrganizationId, true, null, default),
                     OrderCursorCodec.Binding(
                         OrganizationId, "DELIVERED", OrganizationId, true, null, range with { From = null }),
                     OrderCursorCodec.Binding(
                         OrganizationId, "DELIVERED", OrganizationId, true, null, range with { To = null }),
                     OrderCursorCodec.Binding(
                         OrganizationId, "DELIVERED", OrganizationId, true, null, range with { To = to.AddTicks(10) }),
                 })
        {
            Assert.NotEqual(binding, other);
            Assert.False(OrderCursorCodec.TryDecode(cursor, other, out _, out _));
        }

        // The unbound cursor of the earlier version ("ticks:id") and non-canonical spellings never decode.
        var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{from.UtcTicks}:{OrderId:D}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(OrderCursorCodec.TryDecode(legacy, binding, out _, out _));
        Assert.False(OrderCursorCodec.TryDecode(cursor + "=", binding, out _, out _));
        Assert.False(OrderCursorCodec.TryDecode(cursor + "A", binding, out _, out _));
        Assert.False(OrderCursorCodec.TryDecode(string.Empty, binding, out _, out _));
        Assert.False(OrderCursorCodec.TryDecode(
            new string('A', OrderCursorCodec.MaximumLength + 4), binding, out _, out _));
        var empty = OrderCursorCodec.Encode(from, Guid.Empty, binding);
        Assert.False(OrderCursorCodec.TryDecode(empty, binding, out _, out _));
    }

    /// <summary>
    /// ORD-LIST-DATE-FILTER-2026-10-12: created_from and created_to follow the dashboard's rule, an RFC3339 UTC
    /// instant ending in Z, inclusive bounds, created_from not after created_to and at most 31 days between them.
    /// </summary>
    [Fact]
    public void Created_range_parses_utc_instants_and_bounds_the_window()
    {
        Assert.True(OrderListCreatedRange.TryParseInstant("2026-10-01T07:00:00Z", out var from));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero), from);
        Assert.True(OrderListCreatedRange.TryParseInstant("2026-11-01T06:59:59.999999Z", out var to));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 59, 59, TimeSpan.Zero).AddTicks(9_999_990), to);
        foreach (var invalid in new[]
                 {
                     null, string.Empty, "2026-10-01", "2026-10-01T07:00:00", "2026-10-01T00:00:00-07:00",
                     "2026-10-01T07:00:00+00:00", "ayer", "2026-13-01T00:00:00Z", "Z",
                 })
        {
            Assert.False(OrderListCreatedRange.TryParseInstant(invalid, out _), invalid);
        }

        Assert.True(default(OrderListCreatedRange).IsValid);
        Assert.True(new OrderListCreatedRange(from, null).IsValid);
        Assert.True(new OrderListCreatedRange(null, to).IsValid);
        Assert.True(new OrderListCreatedRange(from, from).IsValid);
        // 31 Mazatlán days (October) is the longest window; one more microsecond is not.
        Assert.True(new OrderListCreatedRange(from, to).IsValid);
        Assert.True(new OrderListCreatedRange(from, from + OrderListCreatedRange.MaximumSpan).IsValid);
        Assert.False(new OrderListCreatedRange(from, from + OrderListCreatedRange.MaximumSpan + TimeSpan.FromTicks(10)).IsValid);
        Assert.False(new OrderListCreatedRange(to, from).IsValid);
        Assert.Equal(TimeSpan.FromDays(31), OrderListCreatedRange.MaximumSpan);
    }

    [Fact]
    public void Result_collections_are_immutable_snapshots()
    {
        var mutable = new List<OrderResult> { Result() };
        var page = new OrderPageResult(mutable, null);
        mutable.Clear();
        Assert.Single(page.Items);
        Assert.Throws<NotSupportedException>(() => ((IList<OrderResult>)page.Items).Add(Result()));
    }

    private static Order CreateOrder(long subtotal, long discount, long total) => Order.Create(
        OrderId, "ORD_AAAAAAAAAAAAAAAAAAAAAA", QuoteId, OrganizationId, null,
        Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "SAME_DAY", "OCCASIONAL", false,
        PayerType.Sender, subtotal, discount, 0, total, 2_000_000_000L, "MXN",
        "prc-001-v1", "[{\"description\":\"synthetic\",\"weight_grams\":1,\"declared_value_cents\":0}]",
        null, DateTimeOffset.UtcNow);

    private static OrderAcceptanceEvidence Evidence() => new(
        OrderId, QuoteId, OrganizationId, ActorId, "terms-2026-07", "privacy-2026-07", AcceptedAt, "pwa");

    private static CreateOrderCommand Command(Guid actorId, string requestId) => new(
        actorId, OrganizationId, "orders-unit-key-0001", QuoteId, "SENDER",
        new OrderAcceptanceInput("synthetic-v1", "synthetic-v1", AcceptedAt, "WEB"), requestId,
        RestrictedGoodsAcknowledged: true);

    private static OrderResult Result() => new(
        OrderId, "ORD_AAAAAAAAAAAAAAAAAAAAAA", OrganizationId, null, "DRAFT",
        new MoneyResult("MXN", 3_000_000_000L), 1, Guid.NewGuid(), Guid.NewGuid(), "SAME_DAY",
        QuoteId, Guid.NewGuid(), null, "OCCASIONAL", new MoneyResult("MXN", 3_000_000_000L), null, null);

    private sealed class CountingPublicIdGenerator : IOrderPublicIdGenerator
    {
        internal int CallCount { get; private set; }

        public string Create()
        {
            CallCount++;
            return "ORD_AAAAAAAAAAAAAAAAAAAAAA";
        }
    }
}
