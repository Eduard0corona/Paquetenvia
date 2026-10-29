using System.Security.Cryptography;
using System.Text;
using Notifications.Application.Dispatching;
using Notifications.Application.RecipientNotices;
using Orders.Application.Csv;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-004 required unit tests; AI-24 <c>recipient_notices.producer</c> and
/// <c>consent</c>) and NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N3 choices 1 to 5): the 29-transition matrix, the
/// attempt-stage rule, the topic, schema and notice names the NTF-005 consumer reads, the consent box and the
/// fingerprints that stay byte-identical while it is unticked.
/// </summary>
public sealed class Ntf004RecipientNoticeProducerTests
{
    private static readonly Guid QuoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset AcceptedAt =
        new DateTimeOffset(2026, 7, 20, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560);

    /// <summary>
    /// Every AI-04 <c>order_state_machine.transitions</c> edge (29) with the notice it requests, or none. A contract test
    /// (<c>RecipientNoticeRequestAi04CoverageTests</c>) proves this table equals AI-04 and the AI-24 triggers exactly.
    /// </summary>
    public static TheoryData<string, string, string?> Ai04Transitions => new()
    {
        // E4-P4 "Al quedar confirmada": the tracking link.
        { "DRAFT", "CONFIRMED", "TRACKING_LINK" },
        { "DRAFT", "CANCELLED", null },
        { "CONFIRMED", "READY_FOR_PICKUP", null },
        { "CONFIRMED", "CANCELLED", null },
        { "READY_FOR_PICKUP", "ASSIGNED", null },
        { "READY_FOR_PICKUP", "CANCELLED", null },
        { "ASSIGNED", "AT_PICKUP", null },
        { "ASSIGNED", "READY_FOR_PICKUP", null },
        { "ASSIGNED", "CANCELLED", null },
        { "AT_PICKUP", "PICKED_UP", null },
        // A failed pickup at the sender never tells the recipient (E4-P5).
        { "AT_PICKUP", "FAILED_ATTEMPT", null },
        { "AT_PICKUP", "CANCELLED", null },
        // E4-P3 "Al iniciar traslado".
        { "PICKED_UP", "IN_TRANSIT", "IN_TRANSIT" },
        { "PICKED_UP", "RETURNING", null },
        { "IN_TRANSIT", "DELIVERING", null },
        // E4-P5 "Sin motivo, con enlace": both delivery attempts.
        { "IN_TRANSIT", "FAILED_ATTEMPT", "DELIVERY_FAILED" },
        { "IN_TRANSIT", "RETURNING", null },
        { "DELIVERING", "DELIVERED", "DELIVERED" },
        { "DELIVERING", "FAILED_ATTEMPT", "DELIVERY_FAILED" },
        { "FAILED_ATTEMPT", "RESCHEDULED", null },
        { "FAILED_ATTEMPT", "RETURNING", null },
        { "FAILED_ATTEMPT", "DELIVERING", null },
        { "RESCHEDULED", "READY_FOR_PICKUP", null },
        { "RESCHEDULED", "ASSIGNED", null },
        { "RETURNING", "RETURNED", null },
        { "DELIVERED", "CLOSED", null },
        { "DELIVERED", "CLAIM_OPEN", null },
        { "CLOSED", "CLAIM_OPEN", null },
        { "CLAIM_OPEN", "CLAIM_RESOLVED", null },
    };

    [Theory]
    [MemberData(nameof(Ai04Transitions))]
    public void Every_AI04_transition_requests_exactly_its_AI24_notice(string previous, string next, string? notice)
    {
        Assert.True(OrderContractValues.TryParseOrderStatus(previous, out var source));
        Assert.True(OrderContractValues.TryParseOrderStatus(next, out var target));
        Assert.Equal(notice, RecipientNoticeRequestPolicy.NoticeFor(source, target));
        Assert.Equal(notice is not null, RecipientNoticeRequestPolicy.Requires(source, target));
    }

    [Fact]
    public void The_matrix_lists_the_29_ORD002_edges_and_only_five_write_a_row()
    {
        var listed = Ai04Transitions.Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        var edges = OrderTransitionMatrix.AllowedTransitions
            .SelectMany(pair => pair.Value.Select(target => (pair.Key.ToContractValue(), target.ToContractValue())))
            .ToHashSet();
        Assert.Equal(29, listed.Count);
        Assert.True(listed.SetEquals(edges));

        var writing = Ai04Transitions.Where(row => row[2] is not null).Select(row => $"{row[0]}->{row[1]}").ToArray();
        Assert.Equal(
            ["DRAFT->CONFIRMED", "PICKED_UP->IN_TRANSIT", "IN_TRANSIT->FAILED_ATTEMPT", "DELIVERING->DELIVERED", "DELIVERING->FAILED_ATTEMPT"],
            writing);

        // No pair outside the state machine ever writes a row, whatever its source and target.
        foreach (var source in Enum.GetValues<OrderStatus>())
        {
            foreach (var target in Enum.GetValues<OrderStatus>())
            {
                if (!edges.Contains((source.ToContractValue(), target.ToContractValue())))
                {
                    Assert.Null(RecipientNoticeRequestPolicy.NoticeFor(source, target));
                }
            }
        }
    }

    [Theory]
    [InlineData(OrderStatus.AtPickup, null)]
    [InlineData(OrderStatus.InTransit, "DELIVERY_FAILED")]
    [InlineData(OrderStatus.Delivering, "DELIVERY_FAILED")]
    public void The_attempt_stage_of_a_failed_attempt_decides_whether_the_recipient_is_told(
        OrderStatus attemptStage,
        string? notice) =>
        Assert.Equal(notice, RecipientNoticeRequestPolicy.NoticeFor(attemptStage, OrderStatus.FailedAttempt));

    [Fact]
    public void Producer_and_consumer_share_the_topic_schema_and_notice_names()
    {
        Assert.Equal("orders.recipient-notice-requested", RecipientNoticeRequestPolicy.Topic);
        Assert.Equal(NotificationOutboxTopics.RecipientNoticeRequested, RecipientNoticeRequestPolicy.Topic);
        Assert.Equal("recipient-notice-requested-v1", RecipientNoticeRequestPolicy.SchemaVersion);
        Assert.Equal(RecipientNoticeRequestParser.SchemaVersion, RecipientNoticeRequestPolicy.SchemaVersion);
        Assert.Equal(
            RecipientNoticeCatalog.All.Select(RecipientNoticeCatalog.Notice),
            RecipientNoticeRequestPolicy.Notices);
        foreach (var notice in RecipientNoticeRequestPolicy.Notices)
        {
            Assert.True(RecipientNoticeCatalog.TryFromNotice(notice, out var kind));
            Assert.Equal(notice, RecipientNoticeCatalog.Notice(kind));
        }

        // The routing the NTF-003 lane step installs: the row is one more Notifications lane row, not a new consumer.
        Assert.Equal(OutboxConsumer.Notifications, NotificationOutboxTopics.Resolve(RecipientNoticeRequestPolicy.Topic));
        Assert.NotEqual(DispatchReactionRequestPolicy.Topic, RecipientNoticeRequestPolicy.Topic);
    }

    // ------------------------------------------------------------------ the WhatsApp consent box (E4-P1, N3)

    [Fact]
    public void The_consent_constants_are_the_AI24_text_version_and_channel()
    {
        var runtime = File.ReadAllText(TestPath("docs/normative/v0.6/specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        Assert.Contains($"    text: {RecipientNoticeConsentPolicy.Text}\n", runtime, StringComparison.Ordinal);
        Assert.Contains($"    version: {RecipientNoticeConsentPolicy.Version}\n", runtime, StringComparison.Ordinal);
        Assert.Equal("WHATSAPP", RecipientNoticeConsentPolicy.Channel);
        Assert.True(RecipientNoticeConsentPolicy.IsAcceptable(null));
        Assert.True(RecipientNoticeConsentPolicy.IsAcceptable("NTF-RECIPIENT-WHATSAPP-CONSENT-V1"));
        Assert.False(RecipientNoticeConsentPolicy.IsAcceptable("NTF-RECIPIENT-WHATSAPP-CONSENT-V2"));
        Assert.False(RecipientNoticeConsentPolicy.IsAcceptable("ntf-recipient-whatsapp-consent-v1"));
        Assert.False(RecipientNoticeConsentPolicy.IsAcceptable(string.Empty));
    }

    [Fact]
    public async Task Another_consent_version_is_refused_before_a_public_id_or_any_transaction()
    {
        var generator = new CountingPublicIdGenerator();
        var service = new QuoteSnapshotToOrderCoordinator(null!, generator, null!, null!, null!, null!, null!);

        var exception = await Assert.ThrowsAsync<OrderConflictException>(() => service.CreateAsync(
            Command() with { WhatsAppConsentVersion = "NTF-RECIPIENT-WHATSAPP-CONSENT-V2" },
            CancellationToken.None));

        Assert.Equal(OrderConflictCode.InvalidRequest, exception.Code);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void The_create_order_hash_carries_the_consent_only_when_ticked()
    {
        // The fingerprint every createOrder request had before NTF-004 (OrderServiceWindowTests' vector).
        const string PreConsentPreImage =
            "{\"tenant\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"quote_id\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"payer_type\":\"SENDER\"," +
            "\"terms_version\":\"synthetic-v1\"," +
            "\"privacy_version\":\"synthetic-v1\"," +
            "\"accepted_at\":\"2026-07-20T12:34:56.1234560Z\"," +
            "\"acceptance_channel\":\"WEB\"," +
            "\"restricted_goods_acknowledged\":true}";

        var unticked = Command();
        Assert.Null(unticked.WhatsAppConsentVersion);
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(PreConsentPreImage)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(unticked));
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                PreConsentPreImage[..^1] + ",\"recipient_whatsapp_consent_version\":\"NTF-RECIPIENT-WHATSAPP-CONSENT-V1\"}")),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(
                unticked with { WhatsAppConsentVersion = RecipientNoticeConsentPolicy.Version }));
    }

    public static TheoryData<string[], string[], bool, string?> CsvFields => new()
    {
        { [], [], true, null },
        { ["false"], [], true, null },
        { ["true"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], true, "NTF-RECIPIENT-WHATSAPP-CONSENT-V1" },
        // Every other shape is the uniform conflict before the batch is reserved (N3 choice 5).
        { ["true"], [], false, null },
        { ["false"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], false, null },
        { [], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], false, null },
        { ["true"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V2"], false, null },
        { ["true"], [""], false, null },
        { ["True"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], false, null },
        { ["on"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], false, null },
        { ["1"], [], false, null },
        { [""], [], false, null },
        { ["true", "true"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], false, null },
        { ["true"], ["NTF-RECIPIENT-WHATSAPP-CONSENT-V1", "NTF-RECIPIENT-WHATSAPP-CONSENT-V1"], false, null },
        { ["false", "false"], [], false, null },
    };

    [Theory]
    [MemberData(nameof(CsvFields))]
    public void The_CSV_commit_fields_read_as_one_box_for_the_file(
        string[] attested,
        string[] versions,
        bool valid,
        string? version)
    {
        Assert.Equal(valid, RecipientNoticeConsentPolicy.TryReadCsvFields(attested, versions, out var read));
        Assert.Equal(version, read);
    }

    [Fact]
    public void The_CSV_batch_fingerprint_carries_the_consent_only_when_ticked_and_each_row_inherits_it()
    {
        var unticked = new CsvOrderImportBatchIdentity(ActorId, OrganizationId, "csv-batch-key-0001", "digest", 3);
        // The pre-image every batch had before NTF-004.
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                "CSV-001:COMMIT_ORDER_CSV\n33333333-3333-3333-3333-333333333333\ndigest\n3")),
            CsvOrderImportIdempotency.ComputeBatchRequestHash(unticked));
        var ticked = unticked with { WhatsAppConsentVersion = RecipientNoticeConsentPolicy.Version };
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                "CSV-001:COMMIT_ORDER_CSV\n33333333-3333-3333-3333-333333333333\ndigest\n3\n" +
                "recipient_whatsapp_consent_version\nNTF-RECIPIENT-WHATSAPP-CONSENT-V1")),
            CsvOrderImportIdempotency.ComputeBatchRequestHash(ticked));
        Assert.NotEqual(CsvOrderImportIdempotency.DeriveBatchId(unticked), CsvOrderImportIdempotency.DeriveBatchId(ticked));

        var command = new CsvOrderImportCommitCommand(ActorId, OrganizationId, "csv-batch-key-0001", "digest", [], null);
        Assert.Null(command.WhatsAppConsentVersion);
        Assert.Equal("recipient_whatsapp_consent_attested", RecipientNoticeConsentPolicy.CsvFieldAttested);
        Assert.Equal("recipient_whatsapp_consent_version", RecipientNoticeConsentPolicy.CsvFieldVersion);
        Assert.DoesNotContain(RecipientNoticeConsentPolicy.CsvFieldAttested, CsvOrderImportContract.HeaderWithCod);
        Assert.DoesNotContain(RecipientNoticeConsentPolicy.CsvFieldVersion, CsvOrderImportContract.HeaderWithCod);
    }

    [Fact]
    public async Task Each_CSV_row_is_created_with_the_consent_of_the_commit()
    {
        var orders = new RecordingOrderService();
        var service = new CsvOrderImportCommitService(orders, new NoReplayBatches());
        var row = new CsvOrderImportOrderRow(2, QuoteId, "SENDER", "synthetic-v1", "synthetic-v1", AcceptedAt, "WEB", 0);
        var second = row with { RowNumber = 3, QuoteId = Guid.Parse("55555555-5555-5555-5555-555555555555") };

        await service.CommitAsync(
            new CsvOrderImportCommitCommand(ActorId, OrganizationId, "csv-batch-key-0001", "digest", [row, second], null, true,
                RecipientNoticeConsentPolicy.Version),
            CancellationToken.None);
        await service.CommitAsync(
            new CsvOrderImportCommitCommand(ActorId, OrganizationId, "csv-batch-key-0002", "digest", [row], null, true),
            CancellationToken.None);

        Assert.Equal(
            [RecipientNoticeConsentPolicy.Version, RecipientNoticeConsentPolicy.Version, null],
            orders.Commands.Select(command => command.WhatsAppConsentVersion));
    }

    private static CreateOrderCommand Command() => new(
        ActorId,
        OrganizationId,
        "orders-unit-key-0001",
        QuoteId,
        "SENDER",
        new OrderAcceptanceInput("synthetic-v1", "synthetic-v1", AcceptedAt, "WEB"),
        "request",
        RestrictedGoodsAcknowledged: true);

    private static string TestPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("repository root"), relative);
    }

    private sealed class CountingPublicIdGenerator : IOrderPublicIdGenerator
    {
        internal int CallCount { get; private set; }

        public string Create()
        {
            CallCount++;
            return "ORD_AAAAAAAAAAAAAAAAAAAAAA";
        }
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public List<CreateOrderCommand> Commands { get; } = [];

        public Task<OrderResult> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new OrderResult(
                Guid.NewGuid(), "ORD_AAAAAAAAAAAAAAAAAAAAAA", command.OrganizationId, null, "DRAFT",
                new MoneyResult("MXN", 0), 1, Guid.NewGuid(), Guid.NewGuid(), "SAME_DAY", command.QuoteId, Guid.NewGuid(),
                null, "OCCASIONAL", new MoneyResult("MXN", 0), null, null, DateTimeOffset.UnixEpoch));
        }

        public Task<OrderPageResult> ListAsync(
            Guid actorId, Guid organizationId, string? status, Guid? ownerOrganizationId, string? cursor,
            bool codPendingReconciliation, bool mfaSatisfied, string? publicId, OrderListCreatedRange createdRange,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderDetailResult> GetAsync(
            Guid actorId, Guid organizationId, Guid orderId, bool mfaSatisfied, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoReplayBatches : ICsvOrderImportBatchIdempotencyStore
    {
        public Task<CsvOrderImportCommitResult?> ReserveOrReplayAsync(
            CsvOrderImportBatchIdentity identity,
            CancellationToken cancellationToken) => Task.FromResult<CsvOrderImportCommitResult?>(null);

        public Task<CsvOrderImportCommitResult> CompleteAsync(
            CsvOrderImportBatchIdentity identity,
            CsvOrderImportCommitResult result,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
