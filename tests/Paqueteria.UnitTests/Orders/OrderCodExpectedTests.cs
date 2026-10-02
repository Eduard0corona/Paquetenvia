using System.Security.Cryptography;
using System.Text;
using Orders.Application.Csv;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// D6-COD-EXPECTED: the dispatcher declares the COD expectation in MXN integer cents on
/// <c>POST /orders</c> and on a CSV-001 row; both reach the same ORD-001 create path.
/// </summary>
public sealed class OrderCodExpectedTests
{
    private const string Header = "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel";
    private const string HeaderWithCod = Header + ",cod_expected_cents";
    private const string AcceptedAtText = "2026-07-22T12:00:00Z";
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid QuoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondQuoteId = Guid.Parse("22222222-2222-2222-2222-22222222222b");
    private static readonly DateTimeOffset AcceptedAt =
        new DateTimeOffset(2026, 7, 20, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560);

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("15050", 15_050L)]
    [InlineData("0015050", 15_050L)]
    [InlineData("1999999", 1_999_999L)]
    [InlineData("2000000", 2_000_000L)]
    [InlineData("0002000000", 2_000_000L)]
    public void Plain_non_negative_integers_parse_to_exact_cents(string text, long expected)
    {
        Assert.True(OrderInputPolicy.TryParseCodExpectedCents(text, out var cents));
        Assert.Equal(expected, cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("150.50")]
    [InlineData("150.")]
    [InlineData(".5")]
    [InlineData("150,50")]
    [InlineData("1,500")]
    [InlineData("1 500")]
    [InlineData("1_500")]
    [InlineData("+100")]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("$100")]
    [InlineData("100MXN")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("0x10")]
    [InlineData("9223372036854775808")]
    [InlineData("99999999999999999999")]
    [InlineData("١٢٣")]
    [InlineData("１２３")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Anything_but_a_plain_integer_is_rejected(string text)
    {
        Assert.False(OrderInputPolicy.TryParseCodExpectedCents(text, out var cents));
        Assert.Equal(0, cents);
    }

    [Fact]
    public void Negative_expectations_are_never_valid()
    {
        Assert.True(OrderInputPolicy.IsCodExpectedCents(0));
        Assert.True(OrderInputPolicy.IsCodExpectedCents(OrderCodExpectationPolicy.MaximumCents));
        Assert.False(OrderInputPolicy.IsCodExpectedCents(-1));
        Assert.False(OrderInputPolicy.IsCodExpectedCents(long.MinValue));
    }

    /// <summary>
    /// COD-CAP-20000-2026-10-02 (owner literal: "Tope COD 20,000 pesos"): 20,000.00 MXN is 2,000,000 cents and the
    /// cap is inclusive, so 2,000,000 is accepted and 2,000,001 is rejected, as a number and as text.
    /// </summary>
    [Theory]
    [InlineData("2000001", 2_000_001L)]
    [InlineData("0002000001", 2_000_001L)]
    [InlineData("2000100", 2_000_100L)]
    [InlineData("100000000", 100_000_000L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Expectations_above_the_20000_MXN_cap_are_rejected(string text, long value)
    {
        Assert.Equal(2_000_000L, OrderCodExpectationPolicy.MaximumCents);
        Assert.False(OrderInputPolicy.IsCodExpectedCents(value));
        Assert.False(OrderInputPolicy.TryParseCodExpectedCents(text, out var cents));
        Assert.Equal(0, cents);
        Assert.Throws<ArgumentException>(() => CreateOrder(value));
    }

    [Fact]
    public void The_order_carries_the_declared_expectation_and_rejects_a_negative_one()
    {
        Assert.Equal(0, CreateOrder(0).CodExpectedCents);
        Assert.Equal(15_050, CreateOrder(15_050).CodExpectedCents);
        Assert.Equal(2_000_000, CreateOrder(2_000_000).CodExpectedCents);
        Assert.Throws<ArgumentException>(() => CreateOrder(2_000_001));
        Assert.Throws<ArgumentException>(() => CreateOrder(-1));
    }

    [Fact]
    public void Request_hash_binds_the_declared_amount()
    {
        var withoutCod = Command(0);
        var hash = QuoteSnapshotToOrderCoordinator.ComputeRequestHash(withoutCod);

        Assert.Equal(hash, QuoteSnapshotToOrderCoordinator.ComputeRequestHash(withoutCod with { RequestId = "other" }));
        Assert.NotEqual(hash, QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(1)));
        Assert.NotEqual(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(15_050)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(15_051)));
        Assert.Equal(
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(15_050)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(15_050) with { RequestId = "other" }));
    }

    /// <summary>
    /// An order without COD keeps the exact pre-D6 fingerprint, so an idempotency key reserved before this change
    /// still replays instead of turning into a conflict, and an absent field equals an explicit zero.
    /// </summary>
    [Fact]
    public void A_request_without_COD_keeps_the_pre_COD_fingerprint_byte_for_byte()
    {
        var preCodPreImage =
            "{\"tenant\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"quote_id\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"payer_type\":\"SENDER\"," +
            "\"terms_version\":\"synthetic-v1\"," +
            "\"privacy_version\":\"synthetic-v1\"," +
            "\"accepted_at\":\"2026-07-20T12:34:56.1234560Z\"," +
            "\"acceptance_channel\":\"WEB\"}";

        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(preCodPreImage)),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(0)));
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(preCodPreImage[..^1] + ",\"cod_expected_cents\":15050}")),
            QuoteSnapshotToOrderCoordinator.ComputeRequestHash(Command(15_050)));
    }

    [Fact]
    public void A_six_column_file_still_prevalidates_with_no_COD_on_every_row()
    {
        var result = Prevalidate($"""
            {Header}
            {QuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},WEB
            """);

        Assert.True(result.IsCommittable);
        Assert.Equal(0, Assert.Single(result.ValidRows).CodExpectedCents);
        Assert.Equal(0, Assert.Single(result.Rows).CodExpectedCents);
    }

    [Fact]
    public void The_COD_column_is_read_per_row_and_an_empty_cell_is_no_COD()
    {
        var result = Prevalidate($"""
            {HeaderWithCod}
            {QuoteId:D},RECIPIENT,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},ASSISTED,15050
            {SecondQuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},ASSISTED,
            """);

        Assert.Empty(result.FileErrors);
        Assert.True(result.IsCommittable);
        Assert.Equal([15_050L, 0L], result.ValidRows.Select(row => row.CodExpectedCents));
        Assert.Equal([15_050L, 0L], result.Rows.Select(row => row.CodExpectedCents!.Value));
    }

    [Fact]
    public void The_COD_header_is_matched_like_every_other_column()
    {
        var result = Prevalidate(
            $"{Header}, COD_Expected_Cents \n" +
            $"{QuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},WEB, 250 \n");

        Assert.True(result.IsCommittable);
        Assert.Equal(250, Assert.Single(result.ValidRows).CodExpectedCents);
    }

    [Theory]
    [InlineData("150.50")]
    [InlineData("\"1,500\"")]
    [InlineData("+100")]
    [InlineData("-1")]
    [InlineData("$100")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    [InlineData("2000001")]
    [InlineData("9223372036854775807")]
    [InlineData("cien")]
    public void An_invalid_COD_cell_is_a_row_error_and_blocks_the_batch(string cell)
    {
        var result = Prevalidate($"""
            {HeaderWithCod}
            {QuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},WEB,{cell}
            """);

        var row = Assert.Single(result.Rows);
        Assert.False(row.Valid);
        Assert.Null(row.CodExpectedCents);
        Assert.Equal(QuoteId.ToString("D"), row.QuoteId);
        var error = Assert.Single(row.Errors);
        Assert.Equal("cod_expected_cents", error.Column);
        Assert.Equal("COD_EXPECTED_CENTS_INVALID", error.Code);
        Assert.Empty(result.ValidRows);
        Assert.False(result.IsCommittable);
    }

    [Fact]
    public void A_COD_cell_at_the_20000_MXN_cap_is_accepted_and_one_cent_more_blocks_the_batch()
    {
        var result = Prevalidate($"""
            {HeaderWithCod}
            {QuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},WEB,2000000
            {SecondQuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},WEB,2000001
            """);

        Assert.Equal(2_000_000, result.Rows[0].CodExpectedCents);
        Assert.True(result.Rows[0].Valid);
        Assert.False(result.Rows[1].Valid);
        Assert.Null(result.Rows[1].CodExpectedCents);
        Assert.Equal(
            ("cod_expected_cents", "COD_EXPECTED_CENTS_INVALID"),
            Assert.Single(result.Rows[1].Errors) is var error ? (error.Column, error.Code) : default);
        Assert.False(result.IsCommittable);
    }

    [Fact]
    public void An_invalid_COD_is_reported_together_with_other_column_errors()
    {
        var result = Prevalidate($"""
            {HeaderWithCod}
            {QuoteId:D},NOBODY,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},WEB,-5
            """);

        Assert.Equal(
            ["PAYER_TYPE_INVALID", "COD_EXPECTED_CENTS_INVALID"],
            Assert.Single(result.Rows).Errors.Select(error => error.Code));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void A_seven_column_file_requires_seven_fields_per_row(int columns)
    {
        var fields = Enumerable.Repeat("x", columns);
        var result = Prevalidate($"{HeaderWithCod}\n{string.Join(',', fields)}\n");

        var error = Assert.Single(Assert.Single(result.Rows).Errors);
        Assert.Equal("file", error.Column);
        Assert.Equal("COLUMN_COUNT_INVALID", error.Code);
    }

    [Theory]
    [InlineData("cod_expected_cents,quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel")]
    [InlineData("quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel,cod_expected")]
    [InlineData("quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel,cod_expected_cents,extra")]
    [InlineData("quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel,cod_expected_cents,cod_expected_cents")]
    public void Any_other_placement_or_name_of_the_COD_column_fails_the_whole_file(string header)
    {
        var result = Prevalidate($"{header}\n{QuoteId:D},SENDER,t,p,{AcceptedAtText},WEB,1\n");

        Assert.Equal(["HEADER_INVALID"], result.FileErrors);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Changing_only_a_COD_amount_changes_the_batch_digest()
    {
        var first = Prevalidate($"{HeaderWithCod}\n{QuoteId:D},SENDER,t,p,{AcceptedAtText},WEB,100\n");
        var second = Prevalidate($"{HeaderWithCod}\n{QuoteId:D},SENDER,t,p,{AcceptedAtText},WEB,101\n");

        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public async Task Commit_sends_each_row_amount_through_the_ORD001_create_path()
    {
        var prevalidation = Prevalidate($"""
            {HeaderWithCod}
            {QuoteId:D},RECIPIENT,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},ASSISTED,15050
            {SecondQuoteId:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAtText},ASSISTED,
            """);
        var orders = new RecordingOrderService();
        var service = new CsvOrderImportCommitService(orders, new PassThroughBatchStore());

        var result = await service.CommitAsync(
            new CsvOrderImportCommitCommand(
                Guid.NewGuid(),
                OrganizationId,
                "csv-cod-batch-key-0001",
                prevalidation.ContentDigest,
                prevalidation.ValidRows,
                "request-1"),
            CancellationToken.None);

        Assert.Equal(2, result.CreatedRows);
        Assert.Equal([15_050L, 0L], orders.Commands.Select(command => command.CodExpectedCents));
        Assert.Equal([QuoteId, SecondQuoteId], orders.Commands.Select(command => command.QuoteId));
    }

    private static Order CreateOrder(long codExpectedCents) => Order.Create(
        Guid.NewGuid(), "ORD_AAAAAAAAAAAAAAAAAAAAAA", QuoteId, OrganizationId, null,
        Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "SAME_DAY", "OCCASIONAL", false,
        PayerType.Recipient, 10_000, 0, 0, 10_000, 5_200, "MXN",
        "prc-001-v1", "[{\"description\":\"synthetic\",\"weight_grams\":1,\"declared_value_cents\":0}]",
        null, DateTimeOffset.UtcNow, codExpectedCents);

    private static CreateOrderCommand Command(long codExpectedCents) => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        OrganizationId,
        "orders-unit-key-0001",
        QuoteId,
        "SENDER",
        new OrderAcceptanceInput("synthetic-v1", "synthetic-v1", AcceptedAt, "WEB"),
        "request",
        codExpectedCents);

    private static CsvOrderImportPrevalidation Prevalidate(string content) =>
        CsvOrderImportPrevalidator.Prevalidate(Encoding.UTF8.GetBytes(content));

    private sealed class RecordingOrderService : IOrderService
    {
        public List<CreateOrderCommand> Commands { get; } = [];

        public Task<OrderResult> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new OrderResult(
                Guid.NewGuid(), "ORD_AAAAAAAAAAAAAAAAAAAAAA", command.OrganizationId, null, "DRAFT",
                new MoneyResult("MXN", 10_000), 1, Guid.NewGuid(), Guid.NewGuid(), "SAME_DAY",
                command.QuoteId, Guid.NewGuid(), null, "OCCASIONAL", new MoneyResult("MXN", 10_000), null, null));
        }

        public Task<OrderPageResult> ListAsync(
            Guid actorId,
            Guid organizationId,
            string? status,
            Guid? ownerOrganizationId,
            string? cursor,
            bool codPendingReconciliation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderDetailResult> GetAsync(
            Guid actorId,
            Guid organizationId,
            Guid orderId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PassThroughBatchStore : ICsvOrderImportBatchIdempotencyStore
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
