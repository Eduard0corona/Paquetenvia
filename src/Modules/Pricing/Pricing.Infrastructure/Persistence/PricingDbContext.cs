using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Domain;

namespace Pricing.Infrastructure.Persistence;

public sealed class PricingDbContext(
    DbContextOptions<PricingDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    public DbSet<TariffRule> TariffRules => Set<TariffRule>();
    public DbSet<Quote> Quotes => Set<Quote>();
    internal DbSet<ClientAccountProjection> ClientAccounts => Set<ClientAccountProjection>();
    internal DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tariff = modelBuilder.Entity<TariffRule>();
        tariff.ToTable("tariff_rules", "pricing");
        tariff.HasKey(entity => entity.Id);
        tariff.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        tariff.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        tariff.Property(entity => entity.CityId).HasColumnName("city_id").ValueGeneratedNever();
        tariff.Property(entity => entity.ServiceAreaId).HasColumnName("service_area_id");
        tariff.Property(entity => entity.OperatingZoneId).HasColumnName("operating_zone_id");
        tariff.Property(entity => entity.PricingTier).HasColumnName("pricing_tier").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParsePricingTier(value)).ValueGeneratedNever();
        tariff.Property(entity => entity.ServiceType).HasColumnName("service_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseServiceType(value)).ValueGeneratedNever();
        tariff.Property(entity => entity.AmountCents).HasColumnName("amount_cents").HasColumnType("bigint").ValueGeneratedNever();
        tariff.Property(entity => entity.TaxMode).HasColumnName("tax_mode").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseTaxMode(value)).ValueGeneratedNever();
        tariff.Property(entity => entity.ActiveFrom).HasColumnName("active_from").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        tariff.Property(entity => entity.ActiveTo).HasColumnName("active_to").HasColumnType("timestamp with time zone");
        tariff.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseTariffStatus(value)).ValueGeneratedNever();
        tariff.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));

        var quote = modelBuilder.Entity<Quote>();
        quote.ToTable("quotes", "pricing");
        quote.HasKey(entity => entity.Id);
        quote.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        quote.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        quote.Property(entity => entity.ClientAccountId).HasColumnName("client_account_id");
        quote.Property(entity => entity.CityId).HasColumnName("city_id").ValueGeneratedNever();
        quote.Property(entity => entity.ServiceAreaId).HasColumnName("service_area_id");
        quote.Property(entity => entity.OriginLocationId).HasColumnName("origin_location_id").ValueGeneratedNever();
        quote.Property(entity => entity.DestinationLocationId).HasColumnName("destination_location_id").ValueGeneratedNever();
        quote.Property(entity => entity.ServiceType).HasColumnName("service_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseServiceType(value)).ValueGeneratedNever();
        quote.Property(entity => entity.PricingTier).HasColumnName("pricing_tier").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParsePricingTier(value)).ValueGeneratedNever();
        quote.Property(entity => entity.ConsolidatedRoute).HasColumnName("consolidated_route").ValueGeneratedNever();
        quote.Property(entity => entity.SubtotalCents).HasColumnName("subtotal_cents").HasColumnType("bigint").ValueGeneratedNever();
        quote.Property(entity => entity.DiscountCents).HasColumnName("discount_cents").HasColumnType("bigint").ValueGeneratedNever();
        quote.Property(entity => entity.TaxCents).HasColumnName("tax_cents").HasColumnType("bigint").ValueGeneratedNever();
        quote.Property(entity => entity.TotalCents).HasColumnName("total_cents").HasColumnType("bigint").ValueGeneratedNever();
        quote.Property(entity => entity.MinimumTotalCentsSnapshot).HasColumnName("minimum_total_cents_snapshot").HasColumnType("bigint").ValueGeneratedNever();
        quote.Property(entity => entity.Currency).HasColumnName("currency").HasColumnType("character(3)").ValueGeneratedNever();
        quote.Property(entity => entity.PricingPolicyVersion).HasColumnName("pricing_policy_version").HasColumnType("text").ValueGeneratedNever();
        quote.Property(entity => entity.RuleIds).HasColumnName("rule_ids").HasColumnType("uuid[]").ValueGeneratedNever();
        quote.Property(entity => entity.RequestSnapshotRedacted).HasColumnName("request_snapshot_redacted").HasColumnType("jsonb").ValueGeneratedNever();
        quote.Property(entity => entity.PackageSnapshot).HasColumnName("package_snapshot").HasColumnType("jsonb").ValueGeneratedNever();
        quote.Property(entity => entity.PiiSnapshotCiphertext).HasColumnName("pii_snapshot_ciphertext").HasColumnType("bytea");
        quote.Property(entity => entity.PiiKeyVersion).HasColumnName("pii_key_version").HasColumnType("text");
        quote.Property(entity => entity.Breakdown).HasColumnName("breakdown").HasColumnType("jsonb").ValueGeneratedNever();
        quote.Property(entity => entity.InputHash).HasColumnName("input_hash").HasColumnType("bytea").ValueGeneratedNever();
        quote.Property(entity => entity.FinancialOverride).HasColumnName("financial_override").HasColumnType("jsonb");
        quote.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseQuoteStatus(value)).ValueGeneratedNever();
        quote.Property(entity => entity.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        quote.Property(entity => entity.ConsumedAt).HasColumnName("consumed_at").HasColumnType("timestamp with time zone");
        quote.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        quote.HasIndex(entity => new { entity.OwnerOrganizationId, entity.Status, entity.ExpiresAt })
            .HasDatabaseName("quotes_owner_expiry_idx");
        quote.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));

        var account = modelBuilder.Entity<ClientAccountProjection>();
        account.ToTable("client_accounts", "clients", table => table.ExcludeFromMigrations());
        account.HasKey(entity => entity.Id);
        account.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        account.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        account.Property(entity => entity.Name).HasColumnName("name").HasColumnType("text");
        account.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text");
        account.Property(entity => entity.PrivateTariffId).HasColumnName("private_tariff_id");
        account.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        account.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));

        var idempotency = modelBuilder.Entity<IdempotencyRecord>();
        idempotency.ToTable("idempotency_keys", "platform", table => table.ExcludeFromMigrations());
        idempotency.HasKey(entity => new { entity.OwnerOrganizationId, entity.Scope, entity.IdempotencyKey });
        idempotency.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        idempotency.Property(entity => entity.Scope).HasColumnName("scope").HasColumnType("text").ValueGeneratedNever();
        idempotency.Property(entity => entity.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("text").ValueGeneratedNever();
        idempotency.Property(entity => entity.RequestHash).HasColumnName("request_hash").HasColumnType("bytea").ValueGeneratedNever();
        idempotency.Property(entity => entity.ResponseStatus).HasColumnName("response_status");
        idempotency.Property(entity => entity.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        idempotency.Property(entity => entity.ResourceId).HasColumnName("resource_id");
        idempotency.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        idempotency.Property(entity => entity.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        idempotency.HasIndex(entity => entity.ExpiresAt).HasDatabaseName("idempotency_expiry_idx");
        idempotency.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));
    }

    private static ServiceType ParseServiceType(string value) => value switch
    {
        "SAME_DAY" => ServiceType.SameDay,
        "URGENT" => ServiceType.Urgent,
        "SCHEDULED_ROUTE" => ServiceType.ScheduledRoute,
        _ => throw new InvalidOperationException("Unknown service type stored in PostgreSQL."),
    };

    private static PricingTier ParsePricingTier(string value) => value switch
    {
        "OCCASIONAL" => PricingTier.Occasional,
        "BUSINESS_1_49" => PricingTier.Business1To49,
        "BUSINESS_50_199" => PricingTier.Business50To199,
        "BUSINESS_200_499" => PricingTier.Business200To499,
        "BUSINESS_500_PLUS" => PricingTier.Business500Plus,
        "CUSTOM" => PricingTier.Custom,
        _ => throw new InvalidOperationException("Unknown pricing tier stored in PostgreSQL."),
    };

    private static TaxMode ParseTaxMode(string value) => value switch
    {
        "PLUS_VAT" => TaxMode.PlusVat,
        "VAT_INCLUDED" => TaxMode.VatIncluded,
        "EXEMPT" => TaxMode.Exempt,
        _ => throw new InvalidOperationException("Unknown tax mode stored in PostgreSQL."),
    };

    private static TariffRuleStatus ParseTariffStatus(string value) => value switch
    {
        "ACTIVE" => TariffRuleStatus.Active,
        "INACTIVE" => TariffRuleStatus.Inactive,
        _ => throw new InvalidOperationException("Unknown tariff status stored in PostgreSQL."),
    };

    private static QuoteStatus ParseQuoteStatus(string value) => value switch
    {
        "ACTIVE" => QuoteStatus.Active,
        "USED" => QuoteStatus.Used,
        "EXPIRED" => QuoteStatus.Expired,
        "REVOKED" => QuoteStatus.Revoked,
        _ => throw new InvalidOperationException("Unknown quote status stored in PostgreSQL."),
    };
}
