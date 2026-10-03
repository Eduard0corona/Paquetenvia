using System.Text.Json;
using Locations.Application.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Auditing;
using Pricing.Application.Quotes;
using Pricing.Domain;
using Pricing.Infrastructure;
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

    // GATE-011-VAT-INCLUDED-2026-09-29: only VAT_INCLUDED is quoted, the same for every organization.
    [Theory]
    [InlineData(TaxMode.PlusVat)]
    [InlineData(TaxMode.Exempt)]
    public void Unapproved_tax_modes_fail_closed(TaxMode taxMode) =>
        Assert.Equal(TariffEvaluationFailure.TaxModeBlocked, Evaluate([Rule(taxMode: taxMode)]).Failure);

    [Fact]
    public void An_unapproved_tax_mode_on_the_most_specific_rule_is_not_skipped_for_a_less_specific_one()
    {
        var city = Rule();
        var zone = Rule(serviceArea: AreaId, operatingZone: ZoneId, taxMode: TaxMode.Exempt);
        Assert.Equal(TariffEvaluationFailure.TaxModeBlocked, Evaluate([city, zone]).Failure);
    }

    [Fact]
    public void Only_VAT_INCLUDED_is_selectable_in_the_pilot()
    {
        Assert.Equal(TaxMode.VatIncluded, PilotTaxPolicy.SelectableTaxMode);
        Assert.True(PilotTaxPolicy.IsSelectable(TaxMode.VatIncluded));
        Assert.False(PilotTaxPolicy.IsSelectable(TaxMode.PlusVat));
        Assert.False(PilotTaxPolicy.IsSelectable(TaxMode.Exempt));
    }

    // GATE-011 tests VAT_INCLUDED: the tariff amount is the total the customer pays; the pre-tax subtotal is
    // round_half_up(total / 1.16) in integer cents and the tax is the remainder.
    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(1L, 1L, 0L)]
    [InlineData(4_500L, 3_879L, 621L)]
    [InlineData(5_200L, 4_483L, 717L)]
    [InlineData(5_201L, 4_484L, 717L)]
    [InlineData(6_032L, 5_200L, 832L)]
    [InlineData(11_600L, 10_000L, 1_600L)]
    [InlineData(12_000L, 10_345L, 1_655L)]
    [InlineData(12_345L, 10_642L, 1_703L)]
    public void Vat_included_extracts_the_tax_from_the_total_in_integer_cents(long total, long subtotal, long tax)
    {
        var amounts = TariffTaxCalculator.Calculate(TaxMode.VatIncluded, total);
        Assert.Equal(subtotal, amounts.Subtotal.AmountCents);
        Assert.Equal(0, amounts.Discount.AmountCents);
        Assert.Equal(tax, amounts.Tax.AmountCents);
        Assert.Equal(total, amounts.Total.AmountCents);

        var result = Evaluate([Rule(amount: total, taxMode: TaxMode.VatIncluded)]);
        Assert.Equal(TariffEvaluationFailure.None, result.Failure);
        Assert.Equal(subtotal, result.Subtotal.AmountCents);
        Assert.Equal(tax, result.Tax.AmountCents);
        Assert.Equal(total, result.Total.AmountCents);
        // The frozen floor is the VAT-included total, never the pre-tax subtotal.
        Assert.Equal(total, result.MinimumTotal.AmountCents);
    }

    // GATE-011 tests PLUS_VAT: not selectable in the pilot, but its arithmetic stays exact and tested.
    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(1L, 0L, 1L)]
    [InlineData(3L, 0L, 3L)]
    [InlineData(4L, 1L, 5L)]
    [InlineData(4_483L, 717L, 5_200L)]
    [InlineData(5_200L, 832L, 6_032L)]
    [InlineData(10_000L, 1_600L, 11_600L)]
    [InlineData(12_345L, 1_975L, 14_320L)]
    public void Plus_vat_adds_the_tax_to_the_subtotal_in_integer_cents(long subtotal, long tax, long total)
    {
        var amounts = TariffTaxCalculator.Calculate(TaxMode.PlusVat, subtotal);
        Assert.Equal(subtotal, amounts.Subtotal.AmountCents);
        Assert.Equal(0, amounts.Discount.AmountCents);
        Assert.Equal(tax, amounts.Tax.AmountCents);
        Assert.Equal(total, amounts.Total.AmountCents);
    }

    [Fact]
    public void Tax_arithmetic_property_sample_is_exact_integer_round_half_up_and_int64_safe()
    {
        var random = new Random(20260929);
        for (var index = 0; index < 20_000; index++)
        {
            var amount = index switch
            {
                < 10_000 => (long)index,
                < 15_000 => random.NextInt64(0, 100_000_000),
                _ => random.NextInt64(0, long.MaxValue / 2),
            };

            var included = TariffTaxCalculator.Calculate(TaxMode.VatIncluded, amount);
            Assert.Equal(amount, included.Total.AmountCents);
            Assert.Equal(amount, checked(included.Subtotal.AmountCents - included.Discount.AmountCents + included.Tax.AmountCents));
            // |116 * subtotal - 100 * total| < 58: the subtotal is the nearest cent to total / 1.16, and a
            // half-cent tie never occurs, so the rounding direction never decides a result.
            var includedError = (Int128)116 * included.Subtotal.AmountCents - (Int128)100 * amount;
            Assert.True(includedError > -58 && includedError < 58);

            var plus = TariffTaxCalculator.Calculate(TaxMode.PlusVat, amount);
            Assert.Equal(amount, plus.Subtotal.AmountCents);
            Assert.Equal(plus.Total.AmountCents, checked(plus.Subtotal.AmountCents + plus.Tax.AmountCents));
            // |100 * tax - 16 * subtotal| < 50: the tax is the nearest cent to 16% of the subtotal, never a tie.
            var plusError = (Int128)100 * plus.Tax.AmountCents - (Int128)16 * amount;
            Assert.True(plusError > -50 && plusError < 50);
        }

        var maximum = TariffTaxCalculator.Calculate(TaxMode.VatIncluded, long.MaxValue);
        Assert.Equal(long.MaxValue, maximum.Total.AmountCents);
        Assert.Equal(long.MaxValue, checked(maximum.Subtotal.AmountCents + maximum.Tax.AmountCents));
        Assert.Throws<OverflowException>(() => TariffTaxCalculator.Calculate(TaxMode.PlusVat, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => TariffTaxCalculator.Calculate(TaxMode.VatIncluded, -1));
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
        Assert.Equal(first, second);
        Assert.NotEqual(first, PostgreSqlQuoteService.ComputeInputHash(command with { ConsolidatedRoute = true }));
        Assert.NotEqual(first, PostgreSqlQuoteService.ComputeInputHash(command with { ServiceType = "URGENT" }));
        Assert.NotEqual(first, PostgreSqlQuoteService.ComputeInputHash(command with
        {
            Origin = command.Origin with { Lat = command.Origin.Lat + 0.001 },
        }));
        Assert.NotEqual(first, PostgreSqlQuoteService.ComputeInputHash(command with
        {
            Packages = [command.Packages[0] with { Description = "Different synthetic parcel" }],
        }));
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void Pending_reservation_expiration_covers_quote_lifetime_and_operation_buffer()
    {
        var expiration = PostgreSqlQuoteService.CalculateReservationExpiration(
            Now,
            new PricingOptions
            {
                QuoteLifetimeMinutes = 30,
                CommandTimeoutSeconds = 20,
            });

        Assert.Equal(Now.AddMinutes(31), expiration);
        Assert.True(expiration > Now.AddMinutes(30));
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

    [Fact]
    public void Quote_freezes_the_policy_version_of_the_rule_it_selected()
    {
        var city = Rule(policyVersion: "ORG-A-city.v1");
        var area = Rule(serviceArea: AreaId, policyVersion: "ORG-A-area.v2");
        var zone = Rule(serviceArea: AreaId, operatingZone: ZoneId, policyVersion: "ORG-A-zone.v3");

        var zoneEvaluation = Evaluate([city, area, zone]);
        Assert.Same(zone, zoneEvaluation.Rule);
        Assert.Equal("ORG-A-zone.v3", CreateQuote(zoneEvaluation).PricingPolicyVersion);

        var areaEvaluation = Evaluate([city, area], zone: Guid.NewGuid());
        Assert.Same(area, areaEvaluation.Rule);
        Assert.Equal("ORG-A-area.v2", CreateQuote(areaEvaluation).PricingPolicyVersion);
    }

    [Fact]
    public void Each_organization_quotes_with_its_own_policy_version_and_never_another_tenants_rule()
    {
        var otherOrganization = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var own = Rule(policyVersion: "ORG-A-2026.09");
        var foreign = Rule(owner: otherOrganization, serviceArea: AreaId, operatingZone: ZoneId, policyVersion: "ORG-B-7");

        var evaluation = Evaluate([own, foreign]);

        Assert.Same(own, evaluation.Rule);
        Assert.Equal("ORG-A-2026.09", CreateQuote(evaluation).PricingPolicyVersion);
    }

    [Fact]
    public void A_selected_rule_without_a_policy_version_fails_closed_instead_of_falling_back()
    {
        var versionedCity = Rule(policyVersion: "ORG-A-v1");
        var legacyZone = UnversionedLegacyRule(serviceArea: AreaId, operatingZone: ZoneId);

        var evaluation = Evaluate([versionedCity, legacyZone]);

        Assert.Equal(TariffEvaluationFailure.PolicyVersionMissing, evaluation.Failure);
        Assert.Null(evaluation.Rule);
        Assert.Equal(TariffEvaluationFailure.None, Evaluate([versionedCity]).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("v1\n")]
    [InlineData("versión")]
    [InlineData("v1;DROP")]
    [InlineData("v1/2")]
    public void Tariff_rules_reject_unsafe_policy_versions(string? version)
    {
        Assert.False(PricingPolicyVersionFormat.IsValid(version));
        Assert.Throws<ArgumentException>(() => Rule(policyVersion: version!));
    }

    [Fact]
    public void Policy_version_format_accepts_the_safe_alphabet_up_to_64_characters()
    {
        Assert.True(PricingPolicyVersionFormat.IsValid("PRC-2026.09_org-A"));
        Assert.True(PricingPolicyVersionFormat.IsValid(new string('v', 64)));
        Assert.False(PricingPolicyVersionFormat.IsValid(new string('v', 65)));
        Assert.Equal("^[A-Za-z0-9._-]{1,64}$", PricingPolicyVersionFormat.SqlPattern);
    }

    [Fact]
    public void The_removed_global_pricing_policy_version_setting_fails_startup_validation()
    {
        static OptionsValidationException? Validate(Dictionary<string, string?> settings)
        {
            settings["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=unit;Username=unit";
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddPricingInfrastructure(configuration);
            using var provider = services.BuildServiceProvider();
            try
            {
                _ = provider.GetRequiredService<IOptions<PricingOptions>>().Value;
                return null;
            }
            catch (OptionsValidationException exception)
            {
                return exception;
            }
        }

        Assert.Null(Validate(new Dictionary<string, string?> { ["Pricing:Provider"] = "PostgreSql" }));
        var rejected = Validate(new Dictionary<string, string?>
        {
            ["Pricing:Provider"] = "PostgreSql",
            ["Pricing:PricingPolicyVersion"] = "PRC-001-v1",
        });
        Assert.NotNull(rejected);
        Assert.Contains("PRC-POLICY-VERSION-PER-ORG", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EF_materializes_pricing_entities_without_running_domain_validation()
    {
        // PRC-POLICY-VERSION-PER-ORG: a legacy tariff rule may be stored with policy_version NULL.
        // Loading it must never run the validating creation path; TariffRuleEvaluator alone fails closed.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<global::Pricing.Infrastructure.Persistence.PricingDbContext>()
            .UseNpgsql("Host=localhost;Database=unit;Username=unit")
            .Options;
        using var context = new global::Pricing.Infrastructure.Persistence.PricingDbContext(
            options, new global::Paqueteria.Infrastructure.Tenancy.TenantDatabaseExecutionState());
        foreach (var type in new[] { typeof(TariffRule), typeof(Quote) })
        {
            var entity = context.Model.FindEntityType(type)!;
            var binding = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.ConstructorBinding>(entity.ConstructorBinding);
            Assert.Empty(binding.ParameterBindings);
            Assert.Empty(binding.Constructor.GetParameters());
        }

        // Creation validates through the factories; neither type exposes a public constructor.
        Assert.Empty(typeof(TariffRule).GetConstructors());
        Assert.Empty(typeof(Quote).GetConstructors());
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
        TaxMode taxMode = TaxMode.VatIncluded,
        DateTimeOffset? activeFrom = null,
        DateTimeOffset? activeTo = null,
        TariffRuleStatus status = TariffRuleStatus.Active,
        string policyVersion = "ORG-UNIT-v1") => TariffRule.Create(
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
            status,
            policyVersion);

    /// <summary>A rule stored before PRC-POLICY-VERSION-PER-ORG on an upgraded installation.</summary>
    private static TariffRule UnversionedLegacyRule(Guid? serviceArea = null, Guid? operatingZone = null)
    {
        var rule = Rule(serviceArea: serviceArea, operatingZone: operatingZone);
        typeof(TariffRule).GetProperty(nameof(TariffRule.PolicyVersion))!.SetValue(rule, null);
        return rule;
    }

    private static Quote CreateQuote(TariffEvaluationResult evaluation) => Quote.Create(
        Guid.NewGuid(),
        OrganizationId,
        null,
        CityId,
        AreaId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        ServiceType.SameDay,
        PricingTier.Occasional,
        false,
        evaluation,
        [evaluation.Rule!.Id],
        "{}",
        "[]",
        "[]",
        new byte[32],
        Now.AddMinutes(30),
        Now);

    private static CreateQuoteCommand Command() => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        OrganizationId,
        "synthetic-key-0001",
        null,
        new QuoteAddressInput("Synthetic origin 100", "Synthetic Sender", "6671111111", 24.8, -107.4, null),
        new QuoteAddressInput("Synthetic destination 200", "Synthetic Receiver", "667-222-2222", 24.81, -107.41, "Synthetic gate"),
        "SAME_DAY",
        false,
        [new QuotePackageInput("Synthetic parcel", 1000, 50_00, 100, 100, 100)],
        "request-1");
}
