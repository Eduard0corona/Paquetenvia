using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure.Persistence;

public sealed class OrdersDbContext(
    DbContextOptions<OrdersDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PackageItem> PackageItems => Set<PackageItem>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<OrderAcceptance> OrderAcceptances => Set<OrderAcceptance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureOrder(modelBuilder.Entity<Order>());
        ConfigurePackageItem(modelBuilder.Entity<PackageItem>());
        ConfigureOrderEvent(modelBuilder.Entity<OrderEvent>());
        ConfigureOrderAcceptance(modelBuilder.Entity<OrderAcceptance>());
    }

    private void ConfigureOrder(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Order> entity)
    {
        entity.ToTable("orders", "orders");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(value => value.PublicId).HasColumnName("public_id").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.QuoteId).HasColumnName("quote_id").ValueGeneratedNever();
        entity.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        entity.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id");
        entity.Property(value => value.ClientAccountId).HasColumnName("client_account_id");
        entity.Property(value => value.CityId).HasColumnName("city_id").ValueGeneratedNever();
        entity.Property(value => value.ServiceAreaId).HasColumnName("service_area_id");
        entity.Property(value => value.OriginLocationId).HasColumnName("origin_location_id").ValueGeneratedNever();
        entity.Property(value => value.DestinationLocationId).HasColumnName("destination_location_id").ValueGeneratedNever();
        entity.Property(value => value.ServiceType).HasColumnName("service_type").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.PricingTier).HasColumnName("pricing_tier").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.ConsolidatedRoute).HasColumnName("consolidated_route").ValueGeneratedNever();
        entity.Property(value => value.PayerType).HasColumnName("payer_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParsePayerType(value)).ValueGeneratedNever();
        entity.Property(value => value.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseOrderStatus(value)).ValueGeneratedNever();
        entity.Property(value => value.SubtotalCents).HasColumnName("subtotal_cents").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.DiscountCents).HasColumnName("discount_cents").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.TaxCents).HasColumnName("tax_cents").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.TotalCents).HasColumnName("total_cents").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.MinimumTotalCentsSnapshot).HasColumnName("minimum_total_cents_snapshot").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.Currency).HasColumnName("currency").HasColumnType("character(3)").ValueGeneratedNever();
        entity.Property(value => value.PricingPolicyVersion).HasColumnName("pricing_policy_version").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.PackageSnapshot).HasColumnName("package_snapshot").HasColumnType("jsonb").ValueGeneratedNever();
        entity.Property(value => value.FinancialOverride).HasColumnName("financial_override").HasColumnType("jsonb");
        entity.Property(value => value.CodExpectedCents).HasColumnName("cod_expected_cents").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.Version).HasColumnName("version").ValueGeneratedNever();
        entity.Property(value => value.ClaimWindowEndsAt).HasColumnName("claim_window_ends_at").HasColumnType("timestamp with time zone");
        entity.Property(value => value.FinalizedAt).HasColumnName("finalized_at").HasColumnType("timestamp with time zone");
        entity.Property(value => value.ArchivedAt).HasColumnName("archived_at").HasColumnType("timestamp with time zone");
        entity.Property(value => value.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        entity.HasIndex(value => value.PublicId).IsUnique();
        entity.HasIndex(value => value.QuoteId).IsUnique();
        entity.HasIndex(value => new { value.OwnerOrganizationId, value.Status, value.CreatedAt })
            .HasDatabaseName("orders_owner_status_idx");
        entity.HasIndex(value => new { value.OperatorOrganizationId, value.Status, value.CreatedAt })
            .HasDatabaseName("orders_operator_status_idx");
        entity.HasIndex(value => new { value.CityId, value.Status, value.CreatedAt })
            .HasDatabaseName("orders_city_status_idx");
        entity.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));
    }

    private void ConfigurePackageItem(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PackageItem> entity)
    {
        entity.ToTable("package_items", "orders");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        entity.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        entity.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id");
        entity.Property(value => value.Description).HasColumnName("description").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.WeightGrams).HasColumnName("weight_grams").ValueGeneratedNever();
        entity.Property(value => value.DeclaredValueCents).HasColumnName("declared_value_cents").HasColumnType("bigint").ValueGeneratedNever();
        entity.Property(value => value.DimensionsMm).HasColumnName("dimensions_mm").HasColumnType("jsonb").ValueGeneratedNever();
        entity.HasIndex(value => value.OrderId).HasDatabaseName("package_items_order_idx");
        entity.HasOne<Order>().WithMany().HasForeignKey(value => value.OrderId);
        entity.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));
    }

    private void ConfigureOrderEvent(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<OrderEvent> entity)
    {
        entity.ToTable("order_events", "orders");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        entity.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        entity.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id");
        entity.Property(value => value.AggregateVersion).HasColumnName("aggregate_version").ValueGeneratedNever();
        entity.Property(value => value.EventType).HasColumnName("event_type").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.PublicEventCode).HasColumnName("public_event_code").HasColumnType("text");
        entity.Property(value => value.Payload).HasColumnName("payload").HasColumnType("jsonb").ValueGeneratedNever();
        entity.Property(value => value.ActorId).HasColumnName("actor_id");
        entity.Property(value => value.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        entity.HasIndex(value => new { value.OrderId, value.AggregateVersion }).IsUnique();
        entity.HasIndex(value => new { value.OwnerOrganizationId, value.OrderId, value.OccurredAt })
            .HasDatabaseName("order_events_tenant_order_time_idx");
        entity.HasOne<Order>().WithMany().HasForeignKey(value => value.OrderId);
        entity.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));
    }

    private void ConfigureOrderAcceptance(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<OrderAcceptance> entity)
    {
        entity.ToTable("order_acceptances", "orders");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        entity.Property(value => value.QuoteId).HasColumnName("quote_id").ValueGeneratedNever();
        entity.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        entity.Property(value => value.ActorId).HasColumnName("actor_id");
        entity.Property(value => value.TermsVersion).HasColumnName("terms_version").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.PrivacyVersion).HasColumnName("privacy_version").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.AcceptedAtClient).HasColumnName("accepted_at_client").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        entity.Property(value => value.RecordedAtServer).HasColumnName("recorded_at_server").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        entity.Property(value => value.AcceptanceChannel).HasColumnName("acceptance_channel").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.EvidenceSchemaVersion).HasColumnName("evidence_schema_version").HasColumnType("text").ValueGeneratedNever();
        entity.Property(value => value.EvidenceHash).HasColumnName("evidence_hash").HasColumnType("bytea").ValueGeneratedNever();
        entity.HasIndex(value => value.OrderId).IsUnique();
        entity.HasIndex(value => new { value.OwnerOrganizationId, value.RecordedAtServer })
            .HasDatabaseName("order_acceptances_tenant_time_idx");
        entity.HasOne<Order>().WithOne().HasForeignKey<OrderAcceptance>(value => value.OrderId);
        entity.HasQueryFilter(value => tenantState.OrganizationIds.Contains(value.OwnerOrganizationId));
    }

    private static PayerType ParsePayerType(string value) => value switch
    {
        "SENDER" => PayerType.Sender,
        "RECIPIENT" => PayerType.Recipient,
        "BUSINESS_ACCOUNT" => PayerType.BusinessAccount,
        _ => throw new InvalidOperationException("Unknown payer type."),
    };

    private static OrderStatus ParseOrderStatus(string value) => value switch
    {
        "DRAFT" => OrderStatus.Draft,
        _ => throw new InvalidOperationException("ORD-001 can only materialize DRAFT orders."),
    };
}
