using Dispatch.Application.ExternalOffers;
using Dispatch.Domain;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class ExternalOfferPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("READY_FOR_PICKUP", true)]
    [InlineData("RESCHEDULED", true)]
    [InlineData("ASSIGNED", false)]
    public void Creation_is_limited_to_unassigned_dispatchable_orders(string status, bool expected) =>
        Assert.Equal(expected, ExternalOfferPolicy.CanCreate(status, Now.AddMinutes(1), Now));

    [Fact]
    public void Expired_or_non_open_offer_cannot_be_accepted()
    {
        Assert.False(ExternalOfferPolicy.CanAccept("OPEN", Now, Now));
        Assert.False(ExternalOfferPolicy.CanAccept("ACCEPTED", Now.AddMinutes(1), Now));
        Assert.True(ExternalOfferPolicy.CanAccept("OPEN", Now.AddTicks(1), Now));
    }

    [Fact]
    public void Constraints_apply_vehicle_service_area_and_cod_together()
    {
        var allowedArea = Guid.NewGuid();
        var constraints = new ExternalOfferConstraints(["MOTORCYCLE"], [allowedArea], true);
        Assert.True(constraints.Allows("MOTORCYCLE", allowedArea, 100));
        Assert.False(constraints.Allows("CAR", allowedArea, 100));
        Assert.False(constraints.Allows("MOTORCYCLE", Guid.NewGuid(), 100));
        Assert.False(constraints.Allows("MOTORCYCLE", allowedArea, 0));
    }

    [Fact]
    public void Cursor_is_stable_opaque_and_rejects_tampering()
    {
        var id = Guid.NewGuid();
        var cursor = ExternalOfferCursorCodec.Encode(Now, id);
        Assert.DoesNotContain(id.ToString("D"), cursor, StringComparison.Ordinal);
        Assert.True(ExternalOfferCursorCodec.TryDecode(cursor, out var createdAt, out var decodedId));
        Assert.Equal(Now, createdAt);
        Assert.Equal(id, decodedId);
        Assert.False(ExternalOfferCursorCodec.TryDecode(cursor + "x", out _, out _));
    }

    [Fact]
    public void Create_hash_is_order_independent_for_canonical_constraints_and_request_sensitive()
    {
        var command = new CreateExternalOfferCommand(
            Guid.NewGuid(), Guid.NewGuid(), "valid-idempotency-key", Guid.NewGuid(),
            5000, Now.AddHours(1),
            new ExternalOfferConstraints(["CAR", "MOTORCYCLE"], [Guid.NewGuid(), Guid.NewGuid()], false),
            false, "request");
        var reordered = command with
        {
            Constraints = new ExternalOfferConstraints(
                command.Constraints.VehicleTypes.Reverse().ToArray(),
                command.Constraints.ServiceAreaIds.Reverse().ToArray(),
                false),
        };
        Assert.Equal(
            ExternalOfferCanonicalizer.ComputeSha256(command),
            ExternalOfferCanonicalizer.ComputeSha256(reordered));
        Assert.NotEqual(
            ExternalOfferCanonicalizer.ComputeSha256(command),
            ExternalOfferCanonicalizer.ComputeSha256(command with { CommissionCents = 5001 }));
    }
}
