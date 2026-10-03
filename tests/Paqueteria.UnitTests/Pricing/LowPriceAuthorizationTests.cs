using Locations.Application.Locations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Organizations.Endpoints.Authorization;
using Paqueteria.Domain.Tenancy;
using Pricing.Application.Quotes;
using Pricing.Domain;
using Pricing.Infrastructure;
using Pricing.Infrastructure.Quotes;

namespace Paqueteria.UnitTests.Pricing;

/// <summary>
/// LOW-PRICE-MANUAL-AUTH-2026-10-02 (project owner, "Sí, con autorización"; "En la cotización"): a shipment of
/// 52 MXN or less, IVA included, that is not on a consolidated route passes with a manual authorization stored as the
/// quote's financial_override {actor_id, reason, valid_until = quote expiry}.
/// </summary>
public sealed class LowPriceAuthorizationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CityId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = Now.AddMinutes(30);

    [Theory]
    [InlineData(PricingTier.Business200To499, false, 5_200, true)]
    [InlineData(PricingTier.Business500Plus, false, 4_500, true)]
    [InlineData(PricingTier.Business200To499, false, 9_999, true)]
    [InlineData(PricingTier.Occasional, false, 5_200, true)]
    [InlineData(PricingTier.Business1To49, false, 1, true)]
    [InlineData(PricingTier.Occasional, false, 5_201, false)]
    [InlineData(PricingTier.Business50To199, false, 5_900, false)]
    [InlineData(PricingTier.Business200To499, true, 5_200, false)]
    [InlineData(PricingTier.Business500Plus, true, 4_500, false)]
    [InlineData(PricingTier.Occasional, true, 5_200, false)]
    public void The_guard_needs_an_authorization_only_without_a_consolidated_route_for_the_52_and_45_tiers_or_a_total_of_52_MXN_or_less(
        PricingTier tier,
        bool consolidated,
        long totalCents,
        bool expected) =>
        Assert.Equal(expected, LowPriceGuardPolicy.RequiresAuthorization(tier, consolidated, totalCents));

    [Fact]
    public void The_threshold_is_52_MXN_with_IVA_included() =>
        Assert.Equal(5_200, LowPriceGuardPolicy.ThresholdTotalCents);

    [Theory]
    [InlineData("Cliente ancla, ruta en consolidación", "Cliente ancla, ruta en consolidación")]
    [InlineData("  Promoción autorizada por gerencia  ", "Promoción autorizada por gerencia")]
    [InlineData("x", "x")]
    public void A_reason_is_trimmed_and_kept(string raw, string expected) =>
        Assert.Equal(expected, LowPriceAuthorization.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("línea uno\nlínea dos")]
    [InlineData("tab\tinside")]
    public void An_empty_or_control_character_reason_is_refused(string? raw)
    {
        Assert.Null(LowPriceAuthorization.Normalize(raw));
        Assert.Null(QuoteLowPriceAuthorizationPolicy.NormalizeReason(raw));
    }

    [Fact]
    public void A_reason_holds_at_most_200_characters()
    {
        Assert.Equal(200, LowPriceAuthorization.MaximumReasonLength);
        Assert.NotNull(LowPriceAuthorization.Normalize(new string('a', 200)));
        Assert.NotNull(LowPriceAuthorization.Normalize("  " + new string('a', 200) + "  "));
        Assert.Null(LowPriceAuthorization.Normalize(new string('a', 201)));
        Assert.False(LowPriceAuthorization.IsValidReason(" untrimmed"));
        Assert.Throws<ArgumentException>(() => new LowPriceAuthorization(ActorId, " untrimmed", ExpiresAt));
        Assert.Throws<ArgumentException>(() => new LowPriceAuthorization(Guid.Empty, "reason", ExpiresAt));
        Assert.Throws<ArgumentException>(() =>
            new LowPriceAuthorization(ActorId, "reason", new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(-7))));
    }

    [Theory]
    [InlineData(PricingTier.Business200To499)]
    [InlineData(PricingTier.Business500Plus)]
    public void An_authorization_stands_in_for_the_consolidated_route_of_the_52_and_45_tiers(PricingTier tier)
    {
        var refused = Evaluate(tier, 5_200, authorized: false);
        Assert.Equal(TariffEvaluationFailure.ConsolidatedRouteRequired, refused.Failure);

        var authorized = Evaluate(tier, 5_200, authorized: true);
        Assert.Equal(TariffEvaluationFailure.None, authorized.Failure);
        Assert.Equal(5_200, authorized.Total.AmountCents);
        Assert.Equal(5_200, authorized.MinimumTotal.AmountCents);
    }

    [Fact]
    public void An_authorized_52_peso_quote_stores_the_override_and_keeps_the_frozen_amounts()
    {
        var evaluation = Evaluate(PricingTier.Business200To499, 5_200, authorized: true);
        var authorization = new LowPriceAuthorization(ActorId, "Cliente ancla, ruta en consolidación", ExpiresAt);
        var financialOverride = PostgreSqlQuoteService.SerializeFinancialOverride(authorization);

        var quote = CreateQuote(evaluation, PricingTier.Business200To499, false, authorization, financialOverride);

        Assert.Equal(financialOverride, quote.FinancialOverride);
        Assert.Equal(5_200, quote.TotalCents);
        Assert.Equal(5_200, quote.MinimumTotalCentsSnapshot);
        Assert.False(quote.ConsolidatedRoute);
        Assert.Equal(
            new QuoteLowPriceAuthorizationResult(ActorId, "Cliente ancla, ruta en consolidación", ExpiresAt),
            PostgreSqlQuoteService.ReadLowPriceAuthorization(quote.FinancialOverride));
    }

    [Fact]
    public void An_occasional_quote_of_52_MXN_or_less_without_route_accepts_an_authorization_and_one_above_refuses_it()
    {
        var atThreshold = Evaluate(PricingTier.Occasional, 5_200, authorized: true);
        var authorization = new LowPriceAuthorization(ActorId, "Envío promocional autorizado", ExpiresAt);
        var quote = CreateQuote(
            atThreshold,
            PricingTier.Occasional,
            false,
            authorization,
            PostgreSqlQuoteService.SerializeFinancialOverride(authorization));
        Assert.NotNull(quote.FinancialOverride);

        var above = Evaluate(PricingTier.Occasional, 5_201, authorized: true);
        Assert.Throws<ArgumentException>(() => CreateQuote(
            above,
            PricingTier.Occasional,
            false,
            authorization,
            PostgreSqlQuoteService.SerializeFinancialOverride(authorization)));
    }

    [Fact]
    public void A_quote_on_a_consolidated_route_never_carries_an_authorization()
    {
        var evaluation = Evaluate(PricingTier.Business500Plus, 4_500, authorized: false, consolidated: true);
        var authorization = new LowPriceAuthorization(ActorId, "No hace falta", ExpiresAt);
        Assert.Throws<ArgumentException>(() => CreateQuote(
            evaluation,
            PricingTier.Business500Plus,
            true,
            authorization,
            PostgreSqlQuoteService.SerializeFinancialOverride(authorization)));
        Assert.Null(CreateQuote(evaluation, PricingTier.Business500Plus, true, null, null).FinancialOverride);
    }

    [Fact]
    public void The_authorization_is_valid_exactly_until_the_quote_expires_and_travels_with_its_serialization()
    {
        var evaluation = Evaluate(PricingTier.Business200To499, 5_200, authorized: true);
        var early = new LowPriceAuthorization(ActorId, "Vigencia distinta", ExpiresAt.AddMinutes(-1));
        Assert.Throws<ArgumentException>(() => CreateQuote(
            evaluation,
            PricingTier.Business200To499,
            false,
            early,
            PostgreSqlQuoteService.SerializeFinancialOverride(early)));

        var authorization = new LowPriceAuthorization(ActorId, "Sin serialización", ExpiresAt);
        Assert.Throws<ArgumentException>(() =>
            CreateQuote(evaluation, PricingTier.Business200To499, false, authorization, null));
        Assert.Throws<ArgumentException>(() =>
            CreateQuote(evaluation, PricingTier.Business200To499, false, null, "{}"));
        Assert.Throws<ArgumentException>(() =>
            CreateQuote(evaluation, PricingTier.Business200To499, false, null, null));
    }

    [Fact]
    public void The_financial_override_carries_exactly_the_three_AI06_keys_with_microsecond_validity()
    {
        var validUntil = PostgreSqlQuoteService.TruncateToMicroseconds(new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero).AddTicks(1_234_567));
        Assert.Equal(1_234_560, validUntil.Ticks % TimeSpan.TicksPerSecond);
        var json = PostgreSqlQuoteService.SerializeFinancialOverride(new LowPriceAuthorization(ActorId, "Motivo \"citado\"", validUntil));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            ["actor_id", "reason", "valid_until"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(ActorId, document.RootElement.GetProperty("actor_id").GetGuid());
        Assert.Equal("Motivo \"citado\"", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal("2026-10-02T12:30:00.123456Z", document.RootElement.GetProperty("valid_until").GetString());
    }

    [Theory]
    [InlineData("""{"actor_id":"55555555-5555-5555-5555-555555555555","reason":"ok"}""")]
    [InlineData("""{"actor_id":"not-a-uuid","reason":"ok","valid_until":"2026-10-02T12:30:00Z"}""")]
    [InlineData("""{"actor_id":"55555555-5555-5555-5555-555555555555","reason":"","valid_until":"2026-10-02T12:30:00Z"}""")]
    [InlineData("""{"actor_id":"55555555-5555-5555-5555-555555555555","reason":"ok","valid_until":"tomorrow"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void A_stored_override_that_is_not_a_valid_authorization_fails_closed(string financialOverride) =>
        Assert.Throws<QuoteServiceUnavailableException>(() =>
            PostgreSqlQuoteService.ReadLowPriceAuthorization(financialOverride));

    [Fact]
    public void The_authorization_and_its_actor_are_part_of_the_idempotency_fingerprint_and_absence_keeps_the_old_hash()
    {
        var command = Command();
        var withoutAuthorization = PostgreSqlQuoteService.ComputeInputHash(command);
        // Pinned: the field is written only when present, so a request without it keeps the fingerprint it had
        // before LOW-PRICE-MANUAL-AUTH-2026-10-02 (stored idempotency records stay replayable).
        Assert.Equal(
            "9B14DDC5F24D9322C66CCD4E01FC77BB03AFFD9562581B3C4280FED631955C0B",
            Convert.ToHexString(withoutAuthorization));

        var authorized = command with { LowPriceAuthorization = new QuoteLowPriceAuthorizationInput("Cliente ancla") };
        var withAuthorization = PostgreSqlQuoteService.ComputeInputHash(authorized);
        Assert.NotEqual(withoutAuthorization, withAuthorization);
        Assert.Equal(withAuthorization, PostgreSqlQuoteService.ComputeInputHash(authorized with { RequestId = "other" }));
        Assert.NotEqual(withAuthorization, PostgreSqlQuoteService.ComputeInputHash(
            authorized with { LowPriceAuthorization = new QuoteLowPriceAuthorizationInput("Otro motivo") }));
        Assert.NotEqual(withAuthorization, PostgreSqlQuoteService.ComputeInputHash(
            authorized with { ActorId = Guid.Parse("66666666-6666-6666-6666-666666666666") }));
        Assert.Equal(withoutAuthorization, PostgreSqlQuoteService.ComputeInputHash(
            command with { ActorId = Guid.Parse("66666666-6666-6666-6666-666666666666") }));
    }

    [Fact]
    public void Only_a_dispatcher_or_a_platform_admin_with_mfa_may_send_the_authorization()
    {
        var capability = TenantFieldCapabilities.CreateQuoteLowPriceAuthorization;
        Assert.Equal("createQuote", capability.OperationId);
        Assert.Equal(TenantCapabilityDecision.Allowed, capability.Evaluate([OrganizationRole.Dispatcher], false));
        Assert.Equal(TenantCapabilityDecision.Allowed, capability.Evaluate([OrganizationRole.PlatformAdmin], true));
        Assert.Equal(TenantCapabilityDecision.MfaRequired, capability.Evaluate([OrganizationRole.PlatformAdmin], false));
        Assert.Equal(
            TenantCapabilityDecision.Allowed,
            capability.Evaluate([OrganizationRole.PlatformAdmin, OrganizationRole.Dispatcher], false));
        foreach (var role in new[]
                 {
                     OrganizationRole.Viewer, OrganizationRole.Finance, OrganizationRole.Driver,
                     OrganizationRole.AllyAdmin, OrganizationRole.AllyOperator, OrganizationRole.BusinessAdmin,
                     OrganizationRole.BusinessOperator,
                 })
        {
            Assert.Equal(TenantCapabilityDecision.Forbidden, capability.Evaluate([role], true));
        }

        // A field capability is not an operation: TenantCapabilities.All keeps one capability per operationId.
        Assert.NotSame(capability, TenantCapabilities.All["createQuote"]);
        Assert.Same(TenantCapabilities.CreateQuote, TenantCapabilities.All["createQuote"]);
    }

    [Fact]
    public async Task The_PostgreSQL_quote_service_resolves_with_the_append_only_audit_writer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Provider"] = "PostgreSql",
                ["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=unit;Username=unit",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IQuoteLocationResolver, UnusedQuoteLocationResolver>();
        services.AddPricingInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<PostgreSqlQuoteService>(scope.ServiceProvider.GetRequiredService<IQuoteService>());
        Assert.IsType<global::Paqueteria.Infrastructure.Auditing.PostgreSqlAppendOnlyAuditWriter>(
            scope.ServiceProvider.GetRequiredService<global::Paqueteria.Application.Auditing.IAppendOnlyAuditWriter>());
    }

    private sealed class UnusedQuoteLocationResolver : IQuoteLocationResolver
    {
        public Task<ResolveQuoteLocationResult> ResolveAsync(
            ResolveQuoteLocationCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static TariffEvaluationResult Evaluate(
        PricingTier tier,
        long amountCents,
        bool authorized,
        bool consolidated = false) => new TariffRuleEvaluator().Evaluate(
            new TariffEvaluationContext(
                OrganizationId,
                CityId,
                null,
                null,
                tier,
                ServiceType.SameDay,
                consolidated,
                Now,
                LowPriceAuthorized: authorized),
            [
                TariffRule.Create(
                    Guid.NewGuid(),
                    OrganizationId,
                    CityId,
                    null,
                    null,
                    tier,
                    ServiceType.SameDay,
                    amountCents,
                    TaxMode.VatIncluded,
                    Now.AddDays(-1),
                    null,
                    TariffRuleStatus.Active,
                    "ORG-UNIT-v1"),
            ]);

    private static Quote CreateQuote(
        TariffEvaluationResult evaluation,
        PricingTier tier,
        bool consolidated,
        LowPriceAuthorization? authorization,
        string? financialOverride) => Quote.Create(
            Guid.NewGuid(),
            OrganizationId,
            null,
            CityId,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ServiceType.SameDay,
            tier,
            consolidated,
            evaluation,
            [evaluation.Rule!.Id],
            "{}",
            "[]",
            "[]",
            new byte[32],
            ExpiresAt,
            Now,
            authorization,
            financialOverride);

    private static CreateQuoteCommand Command() => new(
        ActorId,
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
