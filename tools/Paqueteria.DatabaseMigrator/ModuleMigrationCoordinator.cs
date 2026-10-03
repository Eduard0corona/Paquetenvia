using Identity.Infrastructure.Persistence;
using Identity.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Locations.Infrastructure.Persistence;
using Locations.Infrastructure.Persistence.Migrations;
using Organizations.Infrastructure.Persistence;
using Organizations.Infrastructure.Persistence.Migrations;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Persistence.Migrations;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence.Migrations;
using Drivers.Infrastructure.Persistence;
using Drivers.Infrastructure.Persistence.Migrations;
using Finance.Infrastructure.Persistence;
using Finance.Infrastructure.Persistence.Migrations;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Persistence.Migrations;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.Persistence.Migrations;
using Incidents.Infrastructure.Persistence;
using Incidents.Infrastructure.Persistence.Migrations;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Persistence.Migrations;
using Paqueteria.Infrastructure.DataProtection;
using Paqueteria.Infrastructure.DataProtection.Migrations;
using Paqueteria.Infrastructure.Database.Evolution;
using Paqueteria.Infrastructure.Database.Evolution.Migrations;

internal sealed record ModuleMigrationState(
    string Module,
    string HistoryTable,
    string MigrationId,
    string Status);

internal sealed class ModuleMigrationCoordinator
{
    private static readonly (string Module, string HistoryTable, string MigrationId, string SourcePath)[] Contracts =
    [
        ("Identity", "__ef_migrations_history_identity", AddBffSessionStore.MigrationId,
            "src/Modules/Identity/Identity.Infrastructure/Persistence/Migrations/20260927000400_AddBffSessionStore.cs"),
        ("Organizations", "__ef_migrations_history_organizations", AddPendingMemberships.MigrationId,
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260927000500_AddPendingMemberships.cs"),
        ("Locations", "__ef_migrations_history_locations", AdoptCanonicalLocationsBaseline.MigrationId,
            "src/Modules/Locations/Locations.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalLocationsBaseline.cs"),
        ("Drivers", "__ef_migrations_history_drivers", AdoptCanonicalDriverPositions.MigrationId,
            "src/Modules/Drivers/Drivers.Infrastructure/Persistence/Migrations/20260725000156_AdoptCanonicalDriverPositions.cs"),
        ("Pricing", "__ef_migrations_history_pricing", RequireMazatlanTimeZoneInMasterDataLoader.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20261002000100_RequireMazatlanTimeZoneInMasterDataLoader.cs"),
        ("Orders", "__ef_migrations_history_orders", AddTrackingLinkGenerations.MigrationId,
            "src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260929000100_AddTrackingLinkGenerations.cs"),
        ("Dispatch", "__ef_migrations_history_dispatch", AdoptCanonicalDispatchAssignmentsBaseline.MigrationId,
            "src/Modules/Dispatch/Dispatch.Infrastructure/Persistence/Migrations/20260723_AdoptCanonicalDispatchAssignmentsBaseline.cs"),
        ("Custody", "__ef_migrations_history_custody", AddBffSessionPurge.MigrationId,
            "src/Modules/Custody/Custody.Infrastructure/Persistence/Migrations/20260927000400_AddBffSessionPurge.cs"),
        ("Incidents", "__ef_migrations_history_incidents", IndexIncidentEvidenceByOrderProof.MigrationId,
            "src/Modules/Incidents/Incidents.Infrastructure/Persistence/Migrations/20260927000100_IndexIncidentEvidenceByOrderProof.cs"),
        ("Finance", "__ef_migrations_history_finance", EnforceSettlementLedgerIntegrity.MigrationId,
            "src/Modules/Finance/Finance.Infrastructure/Persistence/Migrations/20260925000100_EnforceSettlementLedgerIntegrity.cs"),
        ("Notifications", "__ef_migrations_history_notifications", FailAmbiguousWhatsAppNotifications.MigrationId,
            "src/Modules/Notifications/Notifications.Infrastructure/Persistence/Migrations/20261002000100_FailAmbiguousWhatsAppNotifications.cs"),
        ("DataProtection", PlatformDataProtectionSchema.MigrationsHistoryTable,
            AddDistributedDataProtectionKeyRing.MigrationId,
            "src/BuildingBlocks/Paqueteria.Infrastructure/DataProtection/Migrations/20260922000100_AddDistributedDataProtectionKeyRing.cs"),
        // Runs last: it re-establishes the AI-06 bootstrap functions after every module lane.
        ("PlatformEvolution", PlatformEvolutionSchema.MigrationsHistoryTable,
            BoundTrackingLinksToOrderLifecycle.MigrationId,
            "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Evolution/Migrations/20260929000200_BoundTrackingLinksToOrderLifecycle.cs"),
    ];

    public static IReadOnlyList<ModuleMigrationState> VerifySources()
    {
        var root = RepositoryRootLocator.Find();
        var result = new List<ModuleMigrationState>();
        foreach (var contract in Contracts)
        {
            var path = Path.Combine(root, contract.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new BaselineVerificationException($"{contract.Module} adoption migration is missing.");
            }

            var source = File.ReadAllText(path);
            var validEvolution = contract.Module switch
            {
                // BFF-SESSION-TABLE-SHAPE: the lane only adds the session table, its executor and five
                // functions; dropping them would break a fresh AI-06/AI-18 baseline, so rollback fails closed.
                "Identity" =>
                    source.Contains("BFF_SESSION_SCHEMA_DOWNGRADE_NOT_SUPPORTED", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal),
                // MDM-001-TZ-MAZATLAN-ONLY-2026-10-02: the lane's latest migration only replaces the loader
                // function with one derived from the VAT_INCLUDED one by a reviewed edit (a city entry is
                // America/Mazatlan only); its rollback restores the previous function. No grant, table, role or
                // row is added, dropped, deleted or rewritten.
                "Pricing" => IsMazatlanTimeZoneLoaderSource(source),
                "Notifications" =>
                    source.Contains("NTF-001 rollback blocked", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
                // SET-001: the settlement ledger is append-only, so the lane only ever adds guards to
                // the canonical tables and its rollback fails closed.
                "Finance" =>
                    source.Contains("SET-001 rollback blocked", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal),
                // TRK-002-AUTO-LINK: the lane only adds the two link-generation columns and their two partial
                // unique indexes (or adopts the AI-06 ones); generations keep revoked links from coming back, so
                // its rollback fails closed. LIF-001 is verified below with its own fail-closed rollback.
                "Orders" =>
                    source.Contains("TRK002_GENERATION_DOWNGRADE_NOT_SUPPORTED", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP INDEX", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("UPDATE orders", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal),
                // OPS-003-CLEANUP-ROLE: the lane only adds the cleanup executor, its two functions and the
                // BFF session purge; every purged row is a lifecycle fact, so its rollback fails closed.
                "Custody" =>
                    source.Contains("OPS003_BFF_SESSION_PURGE_DOWNGRADE_NOT_SUPPORTED", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal),
                // REG-002: the lane adds organizations.pending_memberships (or adopts the AI-06 one) and four
                // more registration-executor functions; its rollback removes only those functions. REG-001 is
                // verified below with its own fail-closed rollback.
                "Organizations" =>
                    source.Contains("REG002_ADMIN_REQUIRED", StringComparison.Ordinal) &&
                    source.Contains("DROP FUNCTION IF EXISTS security.apply_pending_memberships", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
                    !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal),
                // SCL-001: dropping the shared key ring invalidates every payload protected by any
                // replica, so the lane is additive and its rollback fails closed.
                "DataProtection" =>
                    source.Contains("SCL001_SCHEMA_DOWNGRADE_NOT_SUPPORTED", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
                // TRK-002-AUTO-LINK: only the tracking projection body, with the lifecycle bound; without it
                // derived links would never end, so its rollback fails closed. The pilot deltas before it are
                // verified below with their own rule.
                "PlatformEvolution" =>
                    source.Contains("TRK002_LIFECYCLE_DOWNGRADE_NOT_SUPPORTED", StringComparison.Ordinal) &&
                    IsPlatformEvolutionSource(source),
                _ =>
                    !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
                    !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal),
            };
            if (!source.Contains(contract.MigrationId, StringComparison.Ordinal) || !validEvolution)
            {
                throw new BaselineVerificationException(
                    $"{contract.Module} adoption migration is destructive or has an unexpected identifier.");
            }

            result.Add(new ModuleMigrationState(
                contract.Module,
                $"platform.{contract.HistoryTable}",
                contract.MigrationId,
                "VERIFIED"));
        }

        VerifyFailClosedSource(
            root,
            "Orders",
            AddOrderLifecycleFinalizationExecutor.MigrationId,
            "src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260925020000_AddOrderLifecycleFinalizationExecutor.cs",
            "LIF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED");
        VerifyPlatformEvolutionSource(
            root,
            ApplyPilotContractDeltas.MigrationId,
            "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Evolution/Migrations/20260927000100_ApplyPilotContractDeltas.cs");
        VerifyAdoptionSource(
            root,
            "Identity",
            AdoptCanonicalIdentityBaseline.MigrationId,
            "src/Modules/Identity/Identity.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalIdentityBaseline.cs");
        VerifyFailClosedSource(
            root,
            "Custody",
            AddOperationalCleanupExecutor.MigrationId,
            "src/Modules/Custody/Custody.Infrastructure/Persistence/Migrations/20260927000100_AddOperationalCleanupExecutor.cs",
            "OPS003_SCHEMA_DOWNGRADE_NOT_SUPPORTED");
        VerifyFailClosedSource(
            root,
            "Organizations",
            AddSelfServiceRegistration.MigrationId,
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260927000400_AddSelfServiceRegistration.cs",
            "REG001_DOWNGRADE_BLOCKED_PENDING_ORGANIZATIONS");
        VerifyAdoptionSource(
            root,
            "Organizations",
            AdoptCanonicalOrganizationsBaseline.MigrationId,
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalOrganizationsBaseline.cs");
        VerifyAdoptionSource(
            root,
            "Pricing",
            AdoptCanonicalPricingBaseline.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalPricingBaseline.cs");
        VerifyPricingSource(
            root,
            AddMasterDataLoader.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260928000100_AddMasterDataLoader.cs",
            IsMasterDataLoaderSource);
        VerifyPricingSource(
            root,
            VersionPricingPolicyPerOrganization.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260928000200_VersionPricingPolicyPerOrganization.cs",
            IsPolicyVersionColumnSource);
        VerifyPricingSource(
            root,
            StoreTariffPolicyVersionInMasterDataLoader.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260928000300_StoreTariffPolicyVersionInMasterDataLoader.cs",
            IsPricingLoaderPolicyVersionSource);
        VerifyPricingSource(
            root,
            HardenMasterDataLoaderOperatorBoundary.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260928000400_HardenMasterDataLoaderOperatorBoundary.cs",
            IsMasterDataLoaderHardeningSource);
        VerifyPricingSource(
            root,
            RequireVatIncludedTariffsInMasterDataLoader.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260929000200_RequireVatIncludedTariffsInMasterDataLoader.cs",
            IsVatIncludedLoaderSource);
        VerifyAdoptionSource(
            root,
            "Drivers",
            AdoptCanonicalDriversBaseline.MigrationId,
            "src/Modules/Drivers/Drivers.Infrastructure/Persistence/Migrations/20260723_AdoptCanonicalDriversBaseline.cs");
        VerifyAdoptionSource(
            root,
            "Orders",
            AdoptCanonicalOrdersBaseline.MigrationId,
            "src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalOrdersBaseline.cs");
        VerifyAdoptionSource(
            root,
            "Custody",
            AdoptCanonicalCustodyProofsBaseline.MigrationId,
            "src/Modules/Custody/Custody.Infrastructure/Persistence/Migrations/20260725_AdoptCanonicalCustodyProofsBaseline.cs");
        VerifyAdoptionSource(
            root,
            "Orders",
            AddRealtimeResynchronizationCursor.MigrationId,
            "src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260725010000_AddRealtimeResynchronizationCursor.cs");
        VerifyAdoptionSource(
            root,
            "Incidents",
            AdoptCanonicalIncidentsBaseline.MigrationId,
            "src/Modules/Incidents/Incidents.Infrastructure/Persistence/Migrations/20260922_AdoptCanonicalIncidentsBaseline.cs");

        return result;
    }

    public async Task<IReadOnlyList<ModuleMigrationState>> PlanAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var result = new List<ModuleMigrationState>();
        foreach (var contract in Contracts)
        {
            result.Add(await ReadStateAsync(connection, contract, cancellationToken));
        }

        return result;
    }

    public async Task ApplyAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge = false)
    {
        var before = await PlanAsync(connectionString, cancellationToken);
        var drift = before.FirstOrDefault(state => state.Status == "DRIFT");
        if (drift is not null)
        {
            throw new InvalidOperationException($"Migration history drift detected for {drift.Module}.");
        }

        if (before.Single(state => state.Module == "Identity").Status == "PENDING")
        {
            await MigrateIdentityAsync(connectionString, cancellationToken, azureOwnershipBridge);
        }

        if (before.Single(state => state.Module == "Organizations").Status == "PENDING")
        {
            await MigrateOrganizationsAsync(connectionString, cancellationToken, azureOwnershipBridge);
        }
        if (before.Single(state => state.Module == "Locations").Status == "PENDING")
        {
            await MigrateLocationsAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Drivers").Status == "PENDING")
        {
            await MigrateDriversAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Pricing").Status == "PENDING")
        {
            await MigratePricingAsync(connectionString, cancellationToken, azureOwnershipBridge);
        }
        if (before.Single(state => state.Module == "Orders").Status == "PENDING")
        {
            await MigrateOrdersAsync(connectionString, cancellationToken, azureOwnershipBridge);
        }
        if (before.Single(state => state.Module == "Dispatch").Status == "PENDING")
        {
            await MigrateDispatchAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Custody").Status == "PENDING")
        {
            await MigrateCustodyAsync(connectionString, cancellationToken, azureOwnershipBridge);
        }
        if (before.Single(state => state.Module == "Incidents").Status == "PENDING")
        {
            await MigrateIncidentsAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Finance").Status == "PENDING")
        {
            await MigrateFinanceAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Notifications").Status == "PENDING")
        {
            await MigrateNotificationsAsync(connectionString, cancellationToken, azureOwnershipBridge);
        }
        if (before.Single(state => state.Module == "DataProtection").Status == "PENDING")
        {
            await MigrateDataProtectionAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "PlatformEvolution").Status == "PENDING")
        {
            await MigratePlatformEvolutionAsync(connectionString, cancellationToken);
        }
        await AssertAsync(connectionString, cancellationToken);
    }

    public async Task<IReadOnlyList<ModuleMigrationState>> AssertAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var states = await PlanAsync(connectionString, cancellationToken);
        var invalid = states.FirstOrDefault(state => state.Status != "APPLIED");
        if (invalid is not null)
        {
            throw new InvalidOperationException(
                $"Expected {invalid.Module} migration {invalid.MigrationId} to be applied; detected {invalid.Status}.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var contract in Contracts)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_get_userbyid(c.relowner)
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname='platform' AND c.relname=@history_table;
                """,
                connection);
            command.Parameters.AddWithValue("history_table", contract.HistoryTable);
            var owner = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (owner != "paqueteria_migrator")
            {
                throw new InvalidOperationException(
                    $"platform.{contract.HistoryTable} must be owned by paqueteria_migrator; detected {owner ?? "missing"}.");
            }
        }

        await AssertDataProtectionKeyRingAsync(connection, cancellationToken);
        return states;
    }

    /// <summary>
    /// SCL-001: the applied Data Protection lane has to leave a shared key ring that the migrator
    /// owns and that both runtime roles can read and append to, otherwise API and Worker replicas
    /// silently fall back to per-node key material.
    /// </summary>
    private static async Task AssertDataProtectionKeyRingAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT pg_get_userbyid(c.relowner),
                   c.relrowsecurity AND c.relforcerowsecurity,
                   (SELECT count(*)::integer
                    FROM aclexplode(COALESCE(c.relacl, acldefault('r', c.relowner))) acl
                    JOIN pg_roles grantee ON grantee.oid = acl.grantee
                    WHERE grantee.rolname IN ('paqueteria_app','paqueteria_worker')
                      AND acl.privilege_type IN ('SELECT','INSERT'))
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='{PlatformDataProtectionSchema.Schema}'
              AND c.relname='{PlatformDataProtectionSchema.Table}';
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"{PlatformDataProtectionSchema.QualifiedTable} is missing after the Data Protection migration lane.");
        }

        var owner = reader.GetString(0);
        var forcedRowLevelSecurity = reader.GetBoolean(1);
        var runtimeGrants = reader.GetInt32(2);

        // SELECT and INSERT for paqueteria_app and paqueteria_worker: the ring is append-only at
        // runtime, so no replica may rewrite or revoke another replica's key material.
        if (owner != "paqueteria_migrator" || !forcedRowLevelSecurity || runtimeGrants != 4)
        {
            throw new InvalidOperationException(
                $"{PlatformDataProtectionSchema.QualifiedTable} must be owned by paqueteria_migrator, force row " +
                $"level security and grant SELECT and INSERT to both runtime roles; detected owner={owner}, " +
                $"forced_rls={forcedRowLevelSecurity}, runtime_grants={runtimeGrants}.");
        }
    }

    private static async Task<ModuleMigrationState> ReadStateAsync(
        NpgsqlConnection connection,
        (string Module, string HistoryTable, string MigrationId, string SourcePath) contract,
        CancellationToken cancellationToken)
    {
        await using var existsCommand = new NpgsqlCommand(
            "SELECT to_regclass('platform.' || @history_table)::text;",
            connection);
        existsCommand.Parameters.AddWithValue("history_table", contract.HistoryTable);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (exists is null or DBNull)
        {
            return new ModuleMigrationState(contract.Module, $"platform.{contract.HistoryTable}", contract.MigrationId, "PENDING");
        }

        await using var historyCommand = new NpgsqlCommand(
            $"SELECT \"MigrationId\" FROM platform.\"{contract.HistoryTable}\" ORDER BY \"MigrationId\";",
            connection);
        var ids = new List<string>();
        await using var reader = await historyCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        string[] expectedIds = contract.Module switch
        {
            "Drivers" =>
                [AdoptCanonicalDriversBaseline.MigrationId, AdoptCanonicalDriverPositions.MigrationId],
            "Pricing" =>
                [
                    AdoptCanonicalPricingBaseline.MigrationId,
                    AddMasterDataLoader.MigrationId,
                    VersionPricingPolicyPerOrganization.MigrationId,
                    StoreTariffPolicyVersionInMasterDataLoader.MigrationId,
                    HardenMasterDataLoaderOperatorBoundary.MigrationId,
                    RequireVatIncludedTariffsInMasterDataLoader.MigrationId,
                    RequireMazatlanTimeZoneInMasterDataLoader.MigrationId,
                ],
            "Orders" =>
                [
                    AdoptCanonicalOrdersBaseline.MigrationId,
                    AddRealtimeResynchronizationCursor.MigrationId,
                    AddOrderLifecycleFinalizationExecutor.MigrationId,
                    AddTrackingLinkGenerations.MigrationId,
                ],
            "Notifications" =>
                [
                    AddTenantSafeOutboxNotifications.MigrationId,
                    RouteExternalOfferRealtime.MigrationId,
                    RouteManualRouteRealtime.MigrationId,
                    AddDispatchOutboxLane.MigrationId,
                    FailAmbiguousWhatsAppNotifications.MigrationId,
                ],
            "Identity" =>
                [AdoptCanonicalIdentityBaseline.MigrationId, AddBffSessionStore.MigrationId],
            "Custody" =>
                [
                    AdoptCanonicalCustodyProofsBaseline.MigrationId,
                    AddOperationalCleanupExecutor.MigrationId,
                    AddBffSessionPurge.MigrationId,
                ],
            "Organizations" =>
                [
                    AdoptCanonicalOrganizationsBaseline.MigrationId,
                    AddSelfServiceRegistration.MigrationId,
                    AddPendingMemberships.MigrationId,
                ],
            "Incidents" =>
                [AdoptCanonicalIncidentsBaseline.MigrationId, IndexIncidentEvidenceByOrderProof.MigrationId],
            "DataProtection" =>
                [AddDistributedDataProtectionKeyRing.MigrationId],
            "PlatformEvolution" =>
                [ApplyPilotContractDeltas.MigrationId, BoundTrackingLinksToOrderLifecycle.MigrationId],
            _ => [contract.MigrationId],
        };
        var status = ids.SequenceEqual(expectedIds, StringComparer.Ordinal)
            ? "APPLIED"
            : ids.Count < expectedIds.Length &&
                ids.SequenceEqual(expectedIds.Take(ids.Count), StringComparer.Ordinal)
                ? "PENDING"
                : "DRIFT";
        return new ModuleMigrationState(contract.Module, $"platform.{contract.HistoryTable}", contract.MigrationId, status);
    }

    /// <summary>MDM-001-OPERATOR-LOADER: only the two master-data roles, their grants and the loader function;
    /// its rollback removes only the function. Loaded rows and audit rows stay.</summary>
    private static bool IsMasterDataLoaderSource(string source) =>
        source.Contains("DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,json,bytea,boolean)", StringComparison.Ordinal) &&
        source.Contains("MDM001_TENANT_CONTEXT_MISMATCH", StringComparison.Ordinal) &&
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>PRC-POLICY-VERSION-PER-ORG: only policy_version and its checks on the canonical tariff rules, no
    /// row rewritten, and a rollback that refuses while any rule carries a version.</summary>
    private static bool IsPolicyVersionColumnSource(string source) =>
        source.Contains(VersionPricingPolicyPerOrganization.DowngradeBlocked, StringComparison.Ordinal) &&
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE pricing", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>PRC-POLICY-VERSION-PER-ORG x MDM-001: two column grants and the loader function derived from
    /// the published one; nothing dropped (except the ungated legacy jsonb overload, as the published step
    /// does), truncated, deleted or rewritten.</summary>
    private static bool IsPricingLoaderPolicyVersionSource(string source) =>
        IsPricingLoaderPolicyVersionSourceWithoutLegacyDrop(
            source.Replace(StoreTariffPolicyVersionInMasterDataLoader.LegacyOverloadDrop, string.Empty, StringComparison.Ordinal));

    private static bool IsPricingLoaderPolicyVersionSourceWithoutLegacyDrop(string source) =>
        source.Contains(StoreTariffPolicyVersionInMasterDataLoader.ImmutableVersionError, StringComparison.Ordinal) &&
        source.Contains("AddMasterDataLoader.UpSql", StringComparison.Ordinal) &&
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE pricing", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>MDM-001 loader hardening (X1/X2): the platform-only operator reference table, its four
    /// executor column grants and the loader function derived from the previous one; nothing dropped (except
    /// the ungated legacy jsonb overload, as the published step does), truncated, deleted or rewritten.</summary>
    private static bool IsMasterDataLoaderHardeningSource(string source) =>
        IsMasterDataLoaderHardeningSourceWithoutLegacyDrop(
            source.Replace(StoreTariffPolicyVersionInMasterDataLoader.LegacyOverloadDrop, string.Empty, StringComparison.Ordinal));

    private static bool IsMasterDataLoaderHardeningSourceWithoutLegacyDrop(string source) =>
        source.Contains(HardenMasterDataLoaderOperatorBoundary.DeploymentPrincipalRefused, StringComparison.Ordinal) &&
        source.Contains("StoreTariffPolicyVersionInMasterDataLoader.FunctionSql", StringComparison.Ordinal) &&
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP POLICY", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE platform", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE pricing", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>GATE-011-VAT-INCLUDED-2026-09-29: the loader function derived from the hardened one by the
    /// reviewed VAT_INCLUDED edit, and nothing else: no grant, table, role, policy or row is dropped, deleted,
    /// truncated or rewritten, and no function is dropped.</summary>
    private static bool IsVatIncludedLoaderSource(string source) =>
        source.Contains(RequireVatIncludedTariffsInMasterDataLoader.TaxModeNotAllowed, StringComparison.Ordinal) &&
        source.Contains("HardenMasterDataLoaderOperatorBoundary.FunctionSql", StringComparison.Ordinal) &&
        !source.Contains("GRANT ", StringComparison.Ordinal) &&
        !source.Contains("REVOKE ", StringComparison.Ordinal) &&
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP POLICY", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE platform", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE pricing", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>MDM-001-TZ-MAZATLAN-ONLY-2026-10-02: the loader function derived from the VAT_INCLUDED one by the
    /// reviewed America/Mazatlan edit, and nothing else: no grant, table, role, policy or row is dropped, deleted,
    /// truncated or rewritten, and no function is dropped.</summary>
    private static bool IsMazatlanTimeZoneLoaderSource(string source) =>
        source.Contains(RequireMazatlanTimeZoneInMasterDataLoader.TimeZoneNotInPilot, StringComparison.Ordinal) &&
        source.Contains("RequireVatIncludedTariffsInMasterDataLoader.FunctionSql", StringComparison.Ordinal) &&
        !source.Contains("GRANT ", StringComparison.Ordinal) &&
        !source.Contains("REVOKE ", StringComparison.Ordinal) &&
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP POLICY", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE platform", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE pricing", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE locations", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>
    /// Platform evolution steps change bootstrap function bodies, bootstrap column grants and indexes only. No
    /// table, role or row is created, dropped or rewritten in either direction.
    /// </summary>
    private static bool IsPlatformEvolutionSource(string source) =>
        !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DROP FUNCTION", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
        !source.Contains("UPDATE ", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) &&
        !source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal);

    /// <summary>An earlier platform evolution migration that must keep its reviewed shape.</summary>
    private static void VerifyPlatformEvolutionSource(string root, string migrationId, string sourcePath)
    {
        var path = Path.Combine(root, sourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new BaselineVerificationException("PlatformEvolution evolution migration is missing.");
        }

        var source = File.ReadAllText(path);
        if (!source.Contains(migrationId, StringComparison.Ordinal) || !IsPlatformEvolutionSource(source))
        {
            throw new BaselineVerificationException(
                "PlatformEvolution evolution migration is destructive or has an unexpected identifier.");
        }
    }

    /// <summary>An earlier Pricing lane migration that must keep its reviewed shape.</summary>
    private static void VerifyPricingSource(string root, string migrationId, string sourcePath, Func<string, bool> isValid)
    {
        var path = Path.Combine(root, sourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new BaselineVerificationException("Pricing evolution migration is missing.");
        }

        var source = File.ReadAllText(path);
        if (!source.Contains(migrationId, StringComparison.Ordinal) || !isValid(source))
        {
            throw new BaselineVerificationException(
                "Pricing evolution migration is destructive or has an unexpected identifier.");
        }
    }

    /// <summary>An earlier lane migration that must keep its fail-closed rollback and stay non-destructive.</summary>
    private static void VerifyFailClosedSource(
        string root,
        string module,
        string migrationId,
        string sourcePath,
        string downgradeToken)
    {
        var path = Path.Combine(root, sourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new BaselineVerificationException($"{module} evolution migration is missing.");
        }

        var source = File.ReadAllText(path);
        if (!source.Contains(migrationId, StringComparison.Ordinal) ||
            !source.Contains(downgradeToken, StringComparison.Ordinal) ||
            source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("DROP ROLE", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) ||
            source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) ||
            source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal))
        {
            throw new BaselineVerificationException(
                $"{module} evolution migration is destructive or has an unexpected identifier.");
        }
    }

    private static void VerifyAdoptionSource(
        string root,
        string module,
        string migrationId,
        string sourcePath)
    {
        var path = Path.Combine(root, sourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new BaselineVerificationException($"{module} adoption migration is missing.");
        }

        var source = File.ReadAllText(path);
        if (!source.Contains(migrationId, StringComparison.Ordinal) ||
            source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) ||
            source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) ||
            source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal))
        {
            throw new BaselineVerificationException(
                $"{module} adoption migration is destructive or has an unexpected identifier.");
        }
    }

    private static async Task MigrateIdentityAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_identity", "platform");
            })
            .Options;
        if (!azureOwnershipBridge)
        {
            await using (var context = new IdentityDbContext(options, new TenantDatabaseExecutionState()))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var verification = await connection.BeginTransactionAsync(cancellationToken);
            await DatabaseBaselineAssertions.AssertSessionExecutorInstalledAsync(
                connection, verification, cancellationToken);
            await verification.RollbackAsync(cancellationToken);
            return;
        }

        // E-002/BFF-SESSION-TABLE-SHAPE: transferring the five session functions to the session executor as
        // a non-superuser needs SET on the executor and a transaction-scoped CREATE on schema security,
        // exactly like the Custody OPS-003 bridge; nothing else is granted and nothing survives the commit.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stage = "lock";
        try
        {
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                advisory.Parameters.AddWithValue("key", CanonicalBaselineContract.AdvisoryLockKey);
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "capability-gate";
            await using (var capability = new NpgsqlCommand("""
                SELECT pg_catalog.to_regrole('paqueteria_session_executor') IS NOT NULL
                   AND pg_catalog.pg_has_role(session_user,'paqueteria_session_executor','SET')
                """, connection, transaction))
            {
                if (await capability.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new InvalidOperationException(
                        "E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_session_executor; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "create-prestate";
            await using (var prestate = new NpgsqlCommand(
                "SELECT pg_catalog.has_schema_privilege('paqueteria_session_executor','security','CREATE')",
                connection, transaction))
            {
                if (await prestate.ExecuteScalarAsync(cancellationToken) is true)
                {
                    throw new InvalidOperationException(
                        "E002_CREATE_PRESTATE_PRESENT role=paqueteria_session_executor schema=security; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "grant-temporary-create";
            await using (var grant = new NpgsqlCommand(
                "GRANT CREATE ON SCHEMA security TO paqueteria_session_executor", connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "bff-session-ef-migration";
            await using (var context = new IdentityDbContext(options, new TenantDatabaseExecutionState()))
            {
                await using (var historyExists = new NpgsqlCommand(
                    "SELECT to_regclass('platform.__ef_migrations_history_identity') IS NOT NULL",
                    connection, transaction))
                {
                    if (await historyExists.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
                        await using var create = new NpgsqlCommand(createHistory, connection, transaction);
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await context.Database.UseTransactionAsync(transaction, cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            // Revoke as the schema owner that granted it, whatever role the migration left active.
            stage = "revoke-temporary-create";
            await using (var revoke = new NpgsqlCommand("""
                SET LOCAL ROLE paqueteria_migrator;
                REVOKE CREATE ON SCHEMA security FROM paqueteria_session_executor;
                RESET ROLE;
                """, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "assert-session-boundary";
            await DatabaseBaselineAssertions.AssertSessionExecutorInstalledAsync(
                connection, transaction, cancellationToken);
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(
                $"E002_IDENTITY_BRIDGE_FAILED stage={stage} SQLSTATE={exception.SqlState} message={exception.MessageText}; " +
                "STOP_FOR_CONTRACT_REVIEW",
                exception);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task MigrateOrganizationsAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
            })
            .Options;
        if (!azureOwnershipBridge)
        {
            await using (var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState()))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var verification = await connection.BeginTransactionAsync(cancellationToken);
            await DatabaseBaselineAssertions.AssertRegistrationExecutorInstalledAsync(
                connection, verification, cancellationToken);
            await verification.RollbackAsync(cancellationToken);
            return;
        }

        // E-002/REG-001/REG-002: transferring the nine registration functions to the registration executor as a
        // non-superuser needs SET on the executor and a transaction-scoped CREATE on schema security,
        // exactly like the Custody OPS-003 bridge; nothing else is granted and nothing survives the commit.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stage = "lock";
        try
        {
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                advisory.Parameters.AddWithValue("key", CanonicalBaselineContract.AdvisoryLockKey);
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "capability-gate";
            await using (var capability = new NpgsqlCommand("""
                SELECT pg_catalog.to_regrole('paqueteria_registration_executor') IS NOT NULL
                   AND pg_catalog.pg_has_role(session_user,'paqueteria_registration_executor','SET')
                """, connection, transaction))
            {
                if (await capability.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new InvalidOperationException(
                        "E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_registration_executor; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "create-prestate";
            await using (var prestate = new NpgsqlCommand(
                "SELECT pg_catalog.has_schema_privilege('paqueteria_registration_executor','security','CREATE')",
                connection, transaction))
            {
                if (await prestate.ExecuteScalarAsync(cancellationToken) is true)
                {
                    throw new InvalidOperationException(
                        "E002_CREATE_PRESTATE_PRESENT role=paqueteria_registration_executor schema=security; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "grant-temporary-create";
            await using (var grant = new NpgsqlCommand(
                "GRANT CREATE ON SCHEMA security TO paqueteria_registration_executor", connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "reg001-ef-migration";
            await using (var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState()))
            {
                await using (var historyExists = new NpgsqlCommand(
                    "SELECT to_regclass('platform.__ef_migrations_history_organizations') IS NOT NULL",
                    connection, transaction))
                {
                    if (await historyExists.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
                        await using var create = new NpgsqlCommand(createHistory, connection, transaction);
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await context.Database.UseTransactionAsync(transaction, cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            // Revoke as the schema owner that granted it, whatever role the migration left active.
            stage = "revoke-temporary-create";
            await using (var revoke = new NpgsqlCommand("""
                SET LOCAL ROLE paqueteria_migrator;
                REVOKE CREATE ON SCHEMA security FROM paqueteria_registration_executor;
                RESET ROLE;
                """, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "assert-registration-boundary";
            await DatabaseBaselineAssertions.AssertRegistrationExecutorInstalledAsync(
                connection, transaction, cancellationToken);
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(
                $"E002_ORGANIZATIONS_BRIDGE_FAILED stage={stage} SQLSTATE={exception.SqlState} message={exception.MessageText}; " +
                "STOP_FOR_CONTRACT_REVIEW",
                exception);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task MigrateLocationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(LocationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_locations", "platform");
            })
            .Options;
        await using var context = new LocationsDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigratePricingAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
            })
            .Options;
        if (!azureOwnershipBridge)
        {
            await using (var context = new PricingDbContext(options, new TenantDatabaseExecutionState()))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var verification = await connection.BeginTransactionAsync(cancellationToken);
            await DatabaseBaselineAssertions.AssertMasterDataLoaderInstalledAsync(
                connection, verification, cancellationToken);
            await verification.RollbackAsync(cancellationToken);
            return;
        }

        // E-002/MDM-001-OPERATOR-LOADER: transferring the loader function to the master data executor as a
        // non-superuser needs SET on the executor and a transaction-scoped CREATE on schema security, exactly
        // like the Organizations REG-001 bridge; nothing else is granted and nothing survives the commit.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stage = "lock";
        try
        {
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                advisory.Parameters.AddWithValue("key", CanonicalBaselineContract.AdvisoryLockKey);
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "capability-gate";
            await using (var capability = new NpgsqlCommand("""
                SELECT pg_catalog.to_regrole('paqueteria_master_data_executor') IS NOT NULL
                   AND pg_catalog.to_regrole('paqueteria_master_data_loader') IS NOT NULL
                   AND pg_catalog.pg_has_role(session_user,'paqueteria_master_data_executor','SET')
                """, connection, transaction))
            {
                if (await capability.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new InvalidOperationException(
                        "E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_master_data_executor,paqueteria_master_data_loader; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "create-prestate";
            await using (var prestate = new NpgsqlCommand(
                "SELECT pg_catalog.has_schema_privilege('paqueteria_master_data_executor','security','CREATE')",
                connection, transaction))
            {
                if (await prestate.ExecuteScalarAsync(cancellationToken) is true)
                {
                    throw new InvalidOperationException(
                        "E002_CREATE_PRESTATE_PRESENT role=paqueteria_master_data_executor schema=security; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "grant-temporary-create";
            await using (var grant = new NpgsqlCommand(
                "GRANT CREATE ON SCHEMA security TO paqueteria_master_data_executor", connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "mdm001-ef-migration";
            await using (var context = new PricingDbContext(options, new TenantDatabaseExecutionState()))
            {
                await using (var historyExists = new NpgsqlCommand(
                    "SELECT to_regclass('platform.__ef_migrations_history_pricing') IS NOT NULL",
                    connection, transaction))
                {
                    if (await historyExists.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
                        await using var create = new NpgsqlCommand(createHistory, connection, transaction);
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await context.Database.UseTransactionAsync(transaction, cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            // Revoke as the schema owner that granted it, whatever role the migration left active.
            stage = "revoke-temporary-create";
            await using (var revoke = new NpgsqlCommand("""
                SET LOCAL ROLE paqueteria_migrator;
                REVOKE CREATE ON SCHEMA security FROM paqueteria_master_data_executor;
                RESET ROLE;
                """, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "assert-master-data-boundary";
            await DatabaseBaselineAssertions.AssertMasterDataLoaderInstalledAsync(
                connection, transaction, cancellationToken);
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(
                $"E002_PRICING_BRIDGE_FAILED stage={stage} SQLSTATE={exception.SqlState} message={exception.MessageText}; " +
                "STOP_FOR_CONTRACT_REVIEW",
                exception);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task MigrateDriversAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
            })
            .Options;
        await using var context = new DriversDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateOrdersAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
            })
            .Options;
        if (!azureOwnershipBridge)
        {
            await using (var context = new OrdersDbContext(options, new TenantDatabaseExecutionState()))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var verification = await connection.BeginTransactionAsync(cancellationToken);
            await DatabaseBaselineAssertions.AssertLifecycleExecutorInstalledAsync(
                connection, verification, cancellationToken);
            await verification.RollbackAsync(cancellationToken);
            return;
        }

        // E-002/ADR-034: transferring security.finalize_expired_orders(integer) to the lifecycle executor
        // as a non-superuser needs SET on the executor and a transaction-scoped CREATE on schema security,
        // exactly like the Notifications bridge; nothing else is granted and nothing survives the commit.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stage = "lock";
        try
        {
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                advisory.Parameters.AddWithValue("key", CanonicalBaselineContract.AdvisoryLockKey);
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "capability-gate";
            await using (var capability = new NpgsqlCommand("""
                SELECT pg_catalog.to_regrole('paqueteria_lifecycle_executor') IS NOT NULL
                   AND pg_catalog.pg_has_role(session_user,'paqueteria_lifecycle_executor','SET')
                """, connection, transaction))
            {
                if (await capability.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new InvalidOperationException(
                        "E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_lifecycle_executor; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "create-prestate";
            await using (var prestate = new NpgsqlCommand(
                "SELECT pg_catalog.has_schema_privilege('paqueteria_lifecycle_executor','security','CREATE')",
                connection, transaction))
            {
                if (await prestate.ExecuteScalarAsync(cancellationToken) is true)
                {
                    throw new InvalidOperationException(
                        "E002_CREATE_PRESTATE_PRESENT role=paqueteria_lifecycle_executor schema=security; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "grant-temporary-create";
            await using (var grant = new NpgsqlCommand(
                "GRANT CREATE ON SCHEMA security TO paqueteria_lifecycle_executor", connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "lif001-ef-migration";
            await using (var context = new OrdersDbContext(options, new TenantDatabaseExecutionState()))
            {
                await using (var historyExists = new NpgsqlCommand(
                    "SELECT to_regclass('platform.__ef_migrations_history_orders') IS NOT NULL",
                    connection, transaction))
                {
                    if (await historyExists.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
                        await using var create = new NpgsqlCommand(createHistory, connection, transaction);
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await context.Database.UseTransactionAsync(transaction, cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            // Revoke as the schema owner that granted it, whatever role the migration left active.
            stage = "revoke-temporary-create";
            await using (var revoke = new NpgsqlCommand("""
                SET LOCAL ROLE paqueteria_migrator;
                REVOKE CREATE ON SCHEMA security FROM paqueteria_lifecycle_executor;
                RESET ROLE;
                """, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "assert-lifecycle-boundary";
            await DatabaseBaselineAssertions.AssertLifecycleExecutorInstalledAsync(
                connection, transaction, cancellationToken);
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(
                $"E002_ORDERS_BRIDGE_FAILED stage={stage} SQLSTATE={exception.SqlState} message={exception.MessageText}; " +
                "STOP_FOR_CONTRACT_REVIEW",
                exception);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task MigrateDispatchAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_dispatch", "platform");
            })
            .Options;
        await using var context = new DispatchDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateCustodyAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<CustodyDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(CustodyDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_custody", "platform");
            })
            .Options;
        if (!azureOwnershipBridge)
        {
            await using (var context = new CustodyDbContext(options, new TenantDatabaseExecutionState()))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await using var verification = await connection.BeginTransactionAsync(cancellationToken);
            await DatabaseBaselineAssertions.AssertCleanupExecutorInstalledAsync(
                connection, verification, cancellationToken);
            await verification.RollbackAsync(cancellationToken);
            return;
        }

        // E-002/OPS-003-CLEANUP-ROLE: transferring the two cleanup functions to the cleanup executor as a
        // non-superuser needs SET on the executor and a transaction-scoped CREATE on schema security,
        // exactly like the Orders LIF-001 bridge; nothing else is granted and nothing survives the commit.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stage = "lock";
        try
        {
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                advisory.Parameters.AddWithValue("key", CanonicalBaselineContract.AdvisoryLockKey);
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "capability-gate";
            await using (var capability = new NpgsqlCommand("""
                SELECT pg_catalog.to_regrole('paqueteria_cleanup_executor') IS NOT NULL
                   AND pg_catalog.pg_has_role(session_user,'paqueteria_cleanup_executor','SET')
                """, connection, transaction))
            {
                if (await capability.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    throw new InvalidOperationException(
                        "E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_cleanup_executor; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "create-prestate";
            await using (var prestate = new NpgsqlCommand(
                "SELECT pg_catalog.has_schema_privilege('paqueteria_cleanup_executor','security','CREATE')",
                connection, transaction))
            {
                if (await prestate.ExecuteScalarAsync(cancellationToken) is true)
                {
                    throw new InvalidOperationException(
                        "E002_CREATE_PRESTATE_PRESENT role=paqueteria_cleanup_executor schema=security; STOP_FOR_CONTRACT_REVIEW");
                }
            }

            stage = "grant-temporary-create";
            await using (var grant = new NpgsqlCommand(
                "GRANT CREATE ON SCHEMA security TO paqueteria_cleanup_executor", connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "ops003-ef-migration";
            await using (var context = new CustodyDbContext(options, new TenantDatabaseExecutionState()))
            {
                await using (var historyExists = new NpgsqlCommand(
                    "SELECT to_regclass('platform.__ef_migrations_history_custody') IS NOT NULL",
                    connection, transaction))
                {
                    if (await historyExists.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
                        await using var create = new NpgsqlCommand(createHistory, connection, transaction);
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await context.Database.UseTransactionAsync(transaction, cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            // Revoke as the schema owner that granted it, whatever role the migration left active.
            stage = "revoke-temporary-create";
            await using (var revoke = new NpgsqlCommand("""
                SET LOCAL ROLE paqueteria_migrator;
                REVOKE CREATE ON SCHEMA security FROM paqueteria_cleanup_executor;
                RESET ROLE;
                """, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "assert-cleanup-boundary";
            await DatabaseBaselineAssertions.AssertCleanupExecutorInstalledAsync(
                connection, transaction, cancellationToken);
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(
                $"E002_CUSTODY_BRIDGE_FAILED stage={stage} SQLSTATE={exception.SqlState} message={exception.MessageText}; " +
                "STOP_FOR_CONTRACT_REVIEW",
                exception);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task MigrateIncidentsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<IncidentsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(IncidentsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_incidents", "platform");
            })
            .Options;
        await using var context = new IncidentsDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateFinanceAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(FinanceDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_finance", "platform");
            })
            .Options;
        await using var context = new FinanceDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateNotificationsAsync(string connectionString, CancellationToken cancellationToken,
        bool azureOwnershipBridge)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        if (!azureOwnershipBridge)
        {
            await using var context = new NotificationsDbContext(NotificationsOptions(connection));
            await context.Database.MigrateAsync(cancellationToken);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var interceptor = new E002Ntf001HistoryInterceptor();
        var stage = "grant-temporary-create";
        try
        {
            stage = "lock-and-reconfirm-notifications-pending";
            await using (var advisory = new NpgsqlCommand(
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
            {
                advisory.Parameters.AddWithValue("key", CanonicalBaselineContract.AdvisoryLockKey);
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            }

            if (await E002NotificationStateReader.ReadAsync(connection, transaction, cancellationToken) !=
                E002NotificationState.Pending)
            {
                throw new InvalidOperationException(
                    "E002_ROUTINE_MAP_STATE_UNRESOLVED; Notifications changed before migration; STOP_FOR_CONTRACT_REVIEW");
            }

            stage = "grant-temporary-create";
            await using (var grant = new NpgsqlCommand("""
                GRANT CREATE ON SCHEMA security,notifications TO paqueteria_outbox_executor
                """, connection, transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            var options = new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
                })
                .AddInterceptors(interceptor)
                .Options;
            await using (var context = new NotificationsDbContext(options))
            {
                await using (var historyExists = new NpgsqlCommand(
                    "SELECT to_regclass('platform.__ef_migrations_history_notifications') IS NOT NULL",
                    connection, transaction))
                {
                    if (await historyExists.ExecuteScalarAsync(cancellationToken) is not true)
                    {
                        var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
                        await using var create = new NpgsqlCommand(createHistory, connection, transaction);
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                stage = "ntf001-ef-migration";
                await context.Database.UseTransactionAsync(transaction, cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
            }

            stage = "verify-transition-assertion";
            if (!interceptor.TargetAssertionExecuted)
            {
                throw new InvalidOperationException(
                    "E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID; NTF-001 history interception missing; STOP_FOR_CONTRACT_REVIEW");
            }

            stage = "revoke-temporary-create";
            await using (var revoke = new NpgsqlCommand("""
                REVOKE CREATE ON SCHEMA security,notifications FROM paqueteria_outbox_executor
                """, connection, transaction))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            stage = "assert-target-applied";
            await new E002SemanticAssertions().AssertAsync(
                connection, E002NotificationState.Applied, transaction, cancellationToken);
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw new InvalidOperationException(
                $"E002_NTF_BRIDGE_FAILED stage={stage} SQLSTATE={exception.SqlState} message={exception.MessageText} " +
                $"first_sqlstate={interceptor.FirstPostgresFailure?.SqlState ?? exception.SqlState} " +
                $"first_message={interceptor.FirstPostgresFailure?.Message ?? exception.MessageText}; STOP_FOR_CONTRACT_REVIEW",
                exception);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private static async Task MigrateDataProtectionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<PlatformDataProtectionDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PlatformDataProtectionDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable(
                    PlatformDataProtectionSchema.MigrationsHistoryTable,
                    PlatformDataProtectionSchema.Schema);
            })
            .Options;
        await using var context = new PlatformDataProtectionDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigratePlatformEvolutionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        await using var context = new PlatformEvolutionDbContext(PlatformEvolutionOptions(connection));
        await context.Database.MigrateAsync(cancellationToken);
    }

    internal static DbContextOptions<PlatformEvolutionDbContext> PlatformEvolutionOptions(NpgsqlConnection connection) =>
        new DbContextOptionsBuilder<PlatformEvolutionDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PlatformEvolutionDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable(
                    PlatformEvolutionSchema.MigrationsHistoryTable,
                    PlatformEvolutionSchema.Schema);
            })
            .Options;

    private static DbContextOptions<NotificationsDbContext> NotificationsOptions(NpgsqlConnection connection)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
            })
            .Options;
        return options;
    }

    private static async Task<NpgsqlConnection> OpenAsMigratorAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
