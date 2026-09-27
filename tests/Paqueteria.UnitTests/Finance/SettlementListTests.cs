using Finance.Application.Settlements;

namespace Paqueteria.UnitTests.Finance;

/// <summary>AI05-LIST-SETTLEMENTS: request shape and the opaque keyset cursor.</summary>
public sealed class SettlementListTests
{
    private static readonly Guid Actor = Guid.Parse("7e1c3b56-9ad3-4bd7-8a9e-9b07f1a0c001");
    private static readonly Guid Tenant = Guid.Parse("7e1c3b56-9ad3-4bd7-8a9e-9b07f1a0c002");

    [Fact]
    public void The_page_size_is_owned_by_the_server()
    {
        Assert.Equal(50, SettlementListPolicy.PageSize);
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("DRAFT", null, null, true)]
    [InlineData("CALCULATED", "2026-09-01", "2026-09-30", true)]
    [InlineData("APPROVED", "2026-09-30", "2026-09-30", true)]
    [InlineData("PAID", null, "2026-09-30", true)]
    [InlineData("VOID", "2026-09-01", null, true)]
    [InlineData("draft", null, null, false)]
    [InlineData("SETTLED", null, null, false)]
    [InlineData("", null, null, false)]
    [InlineData(null, "2026-10-01", "2026-09-30", false)]
    public void A_list_query_is_valid_only_with_a_known_status_and_an_ordered_period(
        string? status,
        string? from,
        string? to,
        bool expected)
    {
        var query = new ListSettlementsQuery(
            Actor,
            Tenant,
            status,
            from is null ? null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            null,
            false);

        Assert.Equal(expected, SettlementInputPolicy.IsValid(query));
    }

    [Fact]
    public void A_list_query_needs_an_actor_a_tenant_and_a_real_cursor_identity()
    {
        Assert.False(SettlementInputPolicy.IsValid(new ListSettlementsQuery(Guid.Empty, Tenant, null, null, null, null, false)));
        Assert.False(SettlementInputPolicy.IsValid(new ListSettlementsQuery(Actor, Guid.Empty, null, null, null, null, false)));
        Assert.False(SettlementInputPolicy.IsValid(new ListSettlementsQuery(
            Actor, Tenant, null, null, null, new SettlementCursor(DateTimeOffset.UnixEpoch, Guid.Empty), false)));
    }

    [Fact]
    public void A_payee_filter_must_name_a_real_payee()
    {
        Assert.True(SettlementInputPolicy.IsValid(
            new ListSettlementsQuery(Actor, Tenant, null, null, null, null, false, Guid.NewGuid())));
        Assert.False(SettlementInputPolicy.IsValid(
            new ListSettlementsQuery(Actor, Tenant, null, null, null, null, false, Guid.Empty)));
    }

    [Fact]
    public void The_cursor_round_trips_exactly_at_microsecond_precision()
    {
        var cursor = new SettlementCursor(
            new DateTimeOffset(2026, 9, 27, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560),
            Guid.Parse("0d8b0c0e-4f84-4d77-9f3c-5b2c9f2f8a11"));

        var encoded = SettlementCursorCodec.Encode(cursor);

        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.True(SettlementCursorCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("MTIzOm5vdC1hLWd1aWQ")]
    [InlineData("OjBkOGIwYzBlLTRmODQtNGQ3Ny05ZjNjLTViMmM5ZjJmOGExMQ")]
    [InlineData("LTE6MGQ4YjBjMGUtNGY4NC00ZDc3LTlmM2MtNWIyYzlmMmY4YTEx")]
    [InlineData("MTIzOjAwMDAwMDAwLTAwMDAtMDAwMC0wMDAwLTAwMDAwMDAwMDAwMA")]
    public void A_cursor_that_was_not_issued_by_the_codec_is_rejected(string? value)
    {
        Assert.False(SettlementCursorCodec.TryDecode(value, out var cursor));
        Assert.Null(cursor);
    }

    [Fact]
    public void A_padded_or_oversized_cursor_is_rejected_rather_than_reinterpreted()
    {
        var encoded = SettlementCursorCodec.Encode(new SettlementCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

        Assert.False(SettlementCursorCodec.TryDecode(encoded + "=", out _));
        Assert.False(SettlementCursorCodec.TryDecode(new string('A', 129), out _));
    }
}
