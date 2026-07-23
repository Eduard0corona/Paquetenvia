using System.Text.Json;
using Locations.Application.Locations;
using Paqueteria.Application.Auditing;
using Pricing.Application.Quotes;
using Pricing.Domain;
using Pricing.Infrastructure.Quotes;

namespace Paqueteria.UnitTests.Pricing;

public sealed class PricingDomainTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CityId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AreaId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ZoneId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_client_account_selects_occasional()
    {
        var result = new PricingTierSelector().Select(null, null, null);
        Assert.Equal(PricingTierSelectionFailure.None, result.Failure);
        Assert.Equal(PricingTier.Occasional, result.Tier);
        Assert.Null(result.PrivateTariffId);
    }

    [Fact]
    public void Active_client_account_selects_exact_private_tariff_tier()
    {
        var rule = Rule(tier: PricingTier.Custom);
        var result = new PricingTierSelector().Select(
            Guid.NewGuid(),
            new ClientPricingProfile(true, rule.Id),
            rule);
        Assert.Equal(PricingTierSelectionFailure.None, result.Failure);
        Assert.Equal(PricingTier.Custom, result.Tier);
        Assert.Equal(rule.Id, result.PrivateTariffId);
    }

    [Theory]
    [InlineData(false, true, PricingTierSelectionFailure.ClientAccountUnavailable)]
    [InlineData(true, false, PricingTierSelectionFailure.VolumePricingUnavailable)]
    public void Client_account_without_an_applicable_private_tariff_is_rejected(
        bool active,
        bool hasPrivateTariff,
        PricingTierSelectionFailure expected)
    {
        var privateId = hasPrivateTariff ? Guid.NewGuid() : (Guid?)null;
        var result = new PricingTierSelector().Select(
            Guid.NewGuid(),
            new ClientPricingProfile(active, privateId),
            null);
        Assert.Equal(expected, result.Failure);
    }

    public static TheoryData<TariffRule> InapplicableRules => new()
    {
        Rule(status: TariffRuleStatus.Inactive),
        Rule(activeFrom: Now.AddMinutes(1)),
        Rule(activeTo: Now),
        Rule(owner: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        Rule(city: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
        Rule(serviceType: ServiceType.Urgent),
    };

    [Theory]
    [MemberData(nameof(InapplicableRules))]
    public void Inactive_future_expired_or_wrong_scope_rule_is_not_selected(TariffRule rule)
    {
        var result = Evaluate([rule]);
        Assert.Equal(TariffEvaluationFailure.NoRule, result.Failure);
    }

    [Fact]
    public void Operating_zone_precedes_service_area_and_city()
    {
        var city = Rule();
        var area = Rule(serviceArea: AreaId);
        var zone = Rule(serviceArea: AreaId, operatingZone: ZoneId);
        Assert.Equal(zone.Id, Evaluate([city, area, zone]).Rule!.Id);
    }

    [Fact]
    public void Service_area_precedes_city_and_city_is_fallback()
    {
        var city = Rule();
        var area = Rule(serviceArea: AreaId);
        Assert.Equal(area.Id, Evaluate([city, area]).Rule!.Id);
        Assert.Equal(city.Id, Evaluate([city], area: null, zone: null).Rule!.Id);
    }

    [Fact]
    public void Equal_specificity_is_ambiguous_and_missing_rule_fails()
    {
        Assert.Equal(TariffEvaluationFailure.AmbiguousRule, Evaluate([Rule(), Rule()]).Failure);
        Assert.Equal(TariffEvaluationFailure.NoRule, Evaluate([]).Failure);
    }

    [Theory]
    [InlineData(TaxMode.PlusVat)]
    [InlineData(TaxMode.VatIncluded)]
    public void Unapproved_tax_modes_fail_closed(TaxMode taxMode) =>
        Assert.Equal(TariffEvaluationFailure.TaxModeBlocked, Evaluate([Rule(taxMode: taxMode)]).Failure);

    [Fact]
    public void Exempt_calculation_uses_int64_cents_and_zero_tax()
    {
        var result = Evaluate([Rule(amount: long.MaxValue)]);
        Assert.Equal(TariffEvaluationFailure.None, result.Failure);
        Assert.Equal(long.MaxValue, result.Subtotal.AmountCents);
        Assert.Equal(0, result.Discount.AmountCents);
        Assert.Equal(0, result.Tax.AmountCents);
        Assert.Equal(long.MaxValue, result.Total.AmountCents);
        Assert.Equal(long.MaxValue, result.MinimumTotal.AmountCents);
    }

    [Fact]
    public void Geography_requires_same_city_and_only_shares_equal_area_and_zone()
    {
        var same = QuoteGeographyPolicy.Resolve(
            new PricingLocation(CityId, AreaId, ZoneId),
            new PricingLocation(CityId, AreaId, ZoneId));
        Assert.True(same.IsSameCity);
        Assert.Equal(AreaId, same.SharedServiceAreaId);
        Assert.Equal(ZoneId, same.SharedOperatingZoneId);

        var otherArea = QuoteGeographyPolicy.Resolve(
            new PricingLocation(CityId, AreaId, ZoneId),
            new PricingLocation(CityId, Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(otherArea.IsSameCity);
        Assert.Null(otherArea.SharedServiceAreaId);
        Assert.Null(otherArea.SharedOperatingZoneId);

        var otherCity = QuoteGeographyPolicy.Resolve(
            new PricingLocation(CityId, AreaId, ZoneId),
            new PricingLocation(Guid.NewGuid(), AreaId, ZoneId));
        Assert.False(otherCity.IsSameCity);
    }

    [Theory]
    [InlineData(PricingTier.Occasional)]
    [InlineData(PricingTier.Business1To49)]
    [InlineData(PricingTier.Business50To199)]
    [InlineData(PricingTier.Custom)]
    public void Low_tiers_do_not_require_consolidated_route(PricingTier tier) =>
        Assert.False(TariffRuleEvaluator.RequiresConsolidatedRoute(tier));

    [Theory]
    [InlineData(PricingTier.Business200To499)]
    [InlineData(PricingTier.Business500Plus)]
    public void High_tiers_require_consolidated_route(PricingTier tier)
    {
        Assert.True(TariffRuleEvaluator.RequiresConsolidatedRoute(tier));
        var result = Evaluate([Rule(tier: tier)], tier: tier, consolidated: false);
        Assert.Equal(TariffEvaluationFailure.ConsolidatedRouteRequired, result.Failure);
        Assert.Equal(TariffEvaluationFailure.None, Evaluate([Rule(tier: tier)], tier: tier, consolidated: true).Failure);
    }

    [Fact]
    public void Package_policy_enforces_description_weight_value_and_dimensions()
    {
        Assert.True(PricingPackagePolicy.IsValid([new PricingPackage("Synthetic parcel", 1, 0, null, null, null)]));
        Assert.False(PricingPackagePolicy.IsValid([]));
        Assert.False(PricingPackagePolicy.IsValid([new PricingPackage("", 1, 0, null, null, null)]));
        Assert.False(PricingPackagePolicy.IsValid([new PricingPackage(new string('x', 251), 1, 0, null, null, null)]));
        Assert.False(PricingPackagePolicy.IsValid([new PricingPackage("Synthetic", 0, 0, null, null, null)]));
        Assert.False(PricingPackagePolicy.IsValid([new PricingPackage("Synthetic", 1, -1, null, null, null)]));
        Assert.False(PricingPackagePolicy.IsValid([new PricingPackage("Synthetic", 1, 0, 0, 1, 1)]));
    }

    [Fact]
    public void Package_snapshot_redaction_removes_embedded_sensitive_values()
    {
        using var document = JsonDocument.Parse("""[{"description":"parcel for synthetic@example.test"},{"description":"token aaa.bbb.ccc"}]""");
        var json = new AuditPayloadRedactor().Redact(document.RootElement).Json;
        Assert.DoesNotContain("synthetic@example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("aaa.bbb.ccc", json, StringComparison.Ordinal);
        Assert.Equal(2, json.Split(AuditPayloadRedactor.Replacement, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Canonical_input_hash_is_stable_and_detects_changes()
    {
        var command = Command();
        var first = PostgreSqlQuoteService.ComputeInputHash(command);
        var second = PostgreSqlQuoteService.ComputeInputHash(command with { RequestId = "different-non-input-metadata" });
        var changed = PostgreSqlQuoteService.ComputeInputHash(command with { ConsolidatedRoute = true });
        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void Location_subkeys_are_stable_bounded_non_pii_and_role_separated()
    {
        var key = new string('k', 128);
        var origin = PostgreSqlQuoteService.CreateLocationSubkey(OrganizationId, key, QuoteLocationRole.Origin);
        var replay = PostgreSqlQuoteService.CreateLocationSubkey(OrganizationId, key, QuoteLocationRole.Origin);
        var destination = PostgreSqlQuoteService.CreateLocationSubkey(OrganizationId, key, QuoteLocationRole.Destination);
        Assert.Equal(origin, replay);
        Assert.NotEqual(origin, destination);
        Assert.InRange(origin.Length, 16, 128);
        Assert.DoesNotContain(key, origin, StringComparison.Ordinal);
    }

    [Fact]
    public void Expiration_is_positive_bounded_and_capped_by_rule()
    {
        Assert.Equal(Now.AddMinutes(30), QuoteExpirationPolicy.Calculate(Now, TimeSpan.FromMinutes(30), null));
        Assert.Equal(Now.AddMinutes(10), QuoteExpirationPolicy.Calculate(Now, TimeSpan.FromMinutes(30), Now.AddMinutes(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuoteExpirationPolicy.Calculate(Now, TimeSpan.Zero, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuoteExpirationPolicy.Calculate(Now, TimeSpan.FromDays(2), null));
    }

    [Fact]
    public void Money_property_sample_stays_non_negative_int64_exact_and_checked()
    {
        var random = new Random(20260722);
        for (var index = 0; index < 5_000; index++)
        {
            var subtotalValue = random.NextInt64(0, long.MaxValue / 4);
            var discountValue = random.NextInt64(0, subtotalValue + 1);
            var taxValue = random.NextInt64(0, long.MaxValue / 4);
            var subtotal = new Money(subtotalValue);
            var discount = new Money(discountValue);
            var tax = new Money(taxValue);
            var total = Money.Add(Money.Subtract(subtotal, discount), tax);
            Assert.True(total.AmountCents >= 0);
            Assert.Equal(checked(subtotalValue - discountValue + taxValue), total.AmountCents);
            var json = JsonSerializer.Serialize(total.AmountCents);
            Assert.Equal(total.AmountCents, JsonSerializer.Deserialize<long>(json));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-1));
        Assert.Throws<OverflowException>(() => Money.Add(new Money(long.MaxValue), new Money(1)));
    }

    private static TariffEvaluationResult Evaluate(
        IEnumerable<TariffRule> rules,
        Guid? area = null,
        Guid? zone = null,
        PricingTier tier = PricingTier.Occasional,
        bool consolidated = false) => new TariffRuleEvaluator().Evaluate(
            new TariffEvaluationContext(
                OrganizationId,
                CityId,
                area ?? AreaId,
                zone ?? ZoneId,
                tier,
                ServiceType.SameDay,
                consolidated,
                Now),
            rules);

    private static TariffRule Rule(
        Guid? owner = null,
        Guid? city = null,
        Guid? serviceArea = null,
        Guid? operatingZone = null,
        PricingTier tier = PricingTier.Occasional,
        ServiceType serviceType = ServiceType.SameDay,
        long amount = 12_345,
        TaxMode taxMode = TaxMode.Exempt,
        DateTimeOffset? activeFrom = null,
        DateTimeOffset? activeTo = null,
        TariffRuleStatus status = TariffRuleStatus.Active) => new(
            Guid.NewGuid(),
            owner ?? OrganizationId,
            city ?? CityId,
            serviceArea,
            operatingZone,
            tier,
            serviceType,
            amount,
            taxMode,
            activeFrom ?? Now.AddDays(-1),
            activeTo,
            status);

    private static CreateQuoteCommand Command() => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        OrganizationId,
        "synthetic-key-0001",
        null,
        new QuoteAddressInput("Synthetic origin 100", "Synthetic Sender", "+526671111111", 24.8, -107.4, null),
        new QuoteAddressInput("Synthetic destination 200", "Synthetic Receiver", "+526672222222", 24.81, -107.41, "Synthetic gate"),
        "SAME_DAY",
        false,
        [new QuotePackageInput("Synthetic parcel", 1000, 50_00, 100, 100, 100)],
        "request-1");
}
