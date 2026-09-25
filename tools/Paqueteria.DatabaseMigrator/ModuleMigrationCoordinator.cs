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

internal sealed record ModuleMigrationState(
    string Module,
    string HistoryTable,
    string MigrationId,
    string Status);

internal sealed class ModuleMigrationCoordinator
{
    private static readonly (string Module, string HistoryTable, string MigrationId, string SourcePath)[] Contracts =
    [
        ("Identity", "__ef_migrations_history_identity", AdoptCanonicalIdentityBaseline.MigrationId,
            "src/Modules/Identity/Identity.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalIdentityBaseline.cs"),
        ("Organizations", "__ef_migrations_history_organizations", AdoptCanonicalOrganizationsBaseline.MigrationId,
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalOrganizationsBaseline.cs"),
        ("Locations", "__ef_migrations_history_locations", AdoptCanonicalLocationsBaseline.MigrationId,
            "src/Modules/Locations/Locations.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalLocationsBaseline.cs"),
        ("Drivers", "__ef_migrations_history_drivers", AdoptCanonicalDriverPositions.MigrationId,
            "src/Modules/Drivers/Drivers.Infrastructure/Persistence/Migrations/20260725000156_AdoptCanonicalDriverPositions.cs"),
        ("Pricing", "__ef_migrations_history_pricing", AdoptCanonicalPricingBaseline.MigrationId,
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalPricingBaseline.cs"),
        ("Orders", "__ef_migrations_history_orders", AddRealtimeResynchronizationCursor.MigrationId,
            "src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260725010000_AddRealtimeResynchronizationCursor.cs"),
        ("Dispatch", "__ef_migrations_history_dispatch", AdoptCanonicalDispatchAssignmentsBaseline.MigrationId,
            "src/Modules/Dispatch/Dispatch.Infrastructure/Persistence/Migrations/20260723_AdoptCanonicalDispatchAssignmentsBaseline.cs"),
        ("Custody", "__ef_migrations_history_custody", AdoptCanonicalCustodyProofsBaseline.MigrationId,
            "src/Modules/Custody/Custody.Infrastructure/Persistence/Migrations/20260725_AdoptCanonicalCustodyProofsBaseline.cs"),
        ("Incidents", "__ef_migrations_history_incidents", AdoptCanonicalIncidentsBaseline.MigrationId,
            "src/Modules/Incidents/Incidents.Infrastructure/Persistence/Migrations/20260922_AdoptCanonicalIncidentsBaseline.cs"),
        ("Finance", "__ef_migrations_history_finance", EnforceSettlementLedgerIntegrity.MigrationId,
            "src/Modules/Finance/Finance.Infrastructure/Persistence/Migrations/20260925000100_EnforceSettlementLedgerIntegrity.cs"),
        ("Notifications", "__ef_migrations_history_notifications", RouteManualRouteRealtime.MigrationId,
            "src/Modules/Notifications/Notifications.Infrastructure/Persistence/Migrations/20260829000100_RouteManualRouteRealtime.cs"),
        ("DataProtection", PlatformDataProtectionSchema.MigrationsHistoryTable,
            AddDistributedDataProtectionKeyRing.MigrationId,
            "src/BuildingBlocks/Paqueteria.Infrastructure/DataProtection/Migrations/20260922000100_AddDistributedDataProtectionKeyRing.cs"),
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
                // SCL-001: dropping the shared key ring invalidates every payload protected by any
                // replica, so the lane is additive and its rollback fails closed.
                "DataProtection" =>
                    source.Contains("SCL001_SCHEMA_DOWNGRADE_NOT_SUPPORTED", StringComparison.Ordinal) &&
                    !source.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
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
            await MigrateIdentityAsync(connectionString, cancellationToken);
        }

        if (before.Single(state => state.Module == "Organizations").Status == "PENDING")
        {
            await MigrateOrganizationsAsync(connectionString, cancellationToken);
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
            await MigratePricingAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Orders").Status == "PENDING")
        {
            await MigrateOrdersAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Dispatch").Status == "PENDING")
        {
            await MigrateDispatchAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Custody").Status == "PENDING")
        {
            await MigrateCustodyAsync(connectionString, cancellationToken);
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
            "Orders" =>
                [AdoptCanonicalOrdersBaseline.MigrationId, AddRealtimeResynchronizationCursor.MigrationId],
            "Notifications" =>
                [
                    AddTenantSafeOutboxNotifications.MigrationId,
                    RouteExternalOfferRealtime.MigrationId,
                    RouteManualRouteRealtime.MigrationId,
                ],
            "DataProtection" =>
                [AddDistributedDataProtectionKeyRing.MigrationId],
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

    private static async Task MigrateIdentityAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_identity", "platform");
            })
            .Options;
        await using var context = new IdentityDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateOrganizationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
            })
            .Options;
        await using var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
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

    private static async Task MigratePricingAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
            })
            .Options;
        await using var context = new PricingDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
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

    private static async Task MigrateOrdersAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
            })
            .Options;
        await using var context = new OrdersDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
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

    private static async Task MigrateCustodyAsync(string connectionString, CancellationToken cancellationToken)
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
        await using var context = new CustodyDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
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
