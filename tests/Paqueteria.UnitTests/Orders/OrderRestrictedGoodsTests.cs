using Orders.Application.Csv;
using Orders.Application.Orders;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: the dispatcher's no-prohibited-goods confirmation is required on the
/// ORD-001 create path and is part of its request fingerprint.
/// </summary>
public sealed class OrderRestrictedGoodsTests
{
    private static readonly Guid QuoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset AcceptedAt = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_create_without_the_confirmation_is_rejected_before_public_ID_or_transaction_side_effects()
    {
        var generator = new CountingPublicIdGenerator();
        var service = new QuoteSnapshotToOrderCoordinator(null!, generator, null!, null!, null!, null!, null!);

        var exception = await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CreateAsync(Command(acknowledged: false), CancellationToken.None));

        Assert.Equal(OrderConflictCode.InvalidRequest, exception.Code);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public void The_confirmation_defaults_to_absent_and_is_part_of_the_request_hash()
    {
        var unconfirmed = new CreateOrderCommand(
            ActorId, OrganizationId, "orders-unit-key-0001", QuoteId, "SENDER",
            new OrderAcceptanceInput("synthetic-v1", "synthetic-v1", AcceptedAt, "WEB"), "request");

        Assert.False(unconfirmed.RestrictedGoodsAcknowledged);
        Assert.NotEqual(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(acknowledged: true)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(acknowledged: false)));
    }

    [Fact]
    public void The_CSV_commit_command_defaults_to_unconfirmed_and_the_form_field_is_not_a_column()
    {
        var command = new CsvOrderImportCommitCommand(ActorId, OrganizationId, "csv-batch-key-0001", "digest", [], null);

        Assert.False(command.RestrictedGoodsAcknowledged);
        Assert.Equal("restricted_goods_acknowledged", CsvOrderImportContract.FieldRestrictedGoodsAcknowledged);
        Assert.Equal("true", CsvOrderImportContract.RestrictedGoodsAcknowledgedValue);
        Assert.DoesNotContain(CsvOrderImportContract.FieldRestrictedGoodsAcknowledged, CsvOrderImportContract.Header);
        Assert.DoesNotContain(CsvOrderImportContract.FieldRestrictedGoodsAcknowledged, CsvOrderImportContract.HeaderWithCod);
    }

    private static CreateOrderCommand Command(bool acknowledged) => new(
        ActorId,
        OrganizationId,
        "orders-unit-key-0001",
        QuoteId,
        "SENDER",
        new OrderAcceptanceInput("synthetic-v1", "synthetic-v1", AcceptedAt, "WEB"),
        "request",
        RestrictedGoodsAcknowledged: acknowledged);

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
