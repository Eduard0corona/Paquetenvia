using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Persistence.Migrations;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// MDM-001-OPERATOR-LOADER against real PostgreSQL/PostGIS: the executor and operator-grantee boundary,
/// idempotent reload, cross-tenant isolation, one audit row per load, dry-run, strict rejection with no
/// partial write, the GATE-007 driver-profile gate and the Pricing lane down and up on an isolated database.
/// Every test uses its own organizations and city names, so the shared database needs no cleanup.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class MasterDataLoaderPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Executor = AddMasterDataLoader.ExecutorRole;
    private const string Loader = AddMasterDataLoader.LoaderRole;
    private const string OperatorLogin = "paqueteria_mdm_operator_login_test";
    private static readonly string OperatorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private static readonly MasterDataEnvironment Testing = new("Testing", null);
    private static readonly MasterDataEnvironment Pilot = new("Production", "PILOT_REAL_PEOPLE");

    [PostgreSqlContractFact]
    public async Task Executor_and_loader_roles_function_and_grants_match_the_MDM001_contract_exactly()
    {
        Assert.Equal("f|t|f|f|f|f|t", await RoleFlagsAsync(Executor));
        Assert.Equal("f|f|f|f|f|f|t", await RoleFlagsAsync(Loader));
        foreach (var runtime in new[] { "paqueteria_app", "paqueteria_worker", PostgreSqlContractFixture.AppLogin, PostgreSqlContractFixture.WorkerLogin })
        {
            foreach (var role in new[] { Executor, Loader })
            {
                Assert.False(await ScalarAsync<bool>(
                    "SELECT pg_has_role(@runtime,@role,'MEMBER') OR pg_has_role(@runtime,@role,'SET')",
                    ("runtime", runtime), ("role", role)));
            }
        }

        Assert.Equal(AddMasterDataLoader.FunctionSignature, await ScalarAsync<string>(
            "SELECT string_agg(oid::regprocedure::text, ',') FROM pg_proc WHERE proowner=@executor::regrole",
            ("executor", Executor)));
        Assert.Equal("t|f|f|f|t|t", await ScalarAsync<string>(
            """
            SELECT concat_ws('|',p.prosecdef,
              has_function_privilege('public',p.oid,'EXECUTE'),
              has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
              has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
              has_function_privilege(@loader,p.oid,'EXECUTE'),
              @search_path = ANY(p.proconfig))
            FROM pg_proc p WHERE p.oid=@function::regprocedure
            """,
            ("loader", Loader), ("function", AddMasterDataLoader.FunctionSignature),
            ("search_path", AddMasterDataLoader.SearchPath)));

        // Exactly the AI-18 column grants; no table-wide grant, no DELETE, nothing for the loader but EXECUTE.
        Assert.Equal(
            string.Join(',', AddMasterDataLoader.ExecutorColumnGrants.Order(StringComparer.Ordinal)),
            await ScalarAsync<string>(
                """
                SELECT string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type, ','
                  ORDER BY table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type COLLATE "C")
                FROM information_schema.column_privileges WHERE grantee=@executor
                """,
                ("executor", Executor)));
        Assert.Equal(
            string.Join(',', AddMasterDataLoader.ExecutorColumnGrants.Order(StringComparer.Ordinal)),
            string.Join(',', DatabaseBaselineAssertions.MasterDataExecutorGrants.Order(StringComparer.Ordinal)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.table_privileges WHERE grantee IN (@executor,@loader)",
            ("executor", Executor), ("loader", Loader)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.column_privileges WHERE grantee=@loader", ("loader", Loader)));
        Assert.Equal("drivers,identity,locations,organizations,platform,pricing", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'USAGE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));
        Assert.Equal("security", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@loader,oid,'USAGE')
            """,
            ("loader", Loader), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));

        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertMasterDataLoaderInstalledAsync(connection, transaction);
        await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Runtime_roles_gain_no_master_data_write_capability()
    {
        foreach (var runtime in new[] { "paqueteria_app", "paqueteria_worker" })
        {
            Assert.False(await ScalarAsync<bool>(
                "SELECT has_function_privilege(@runtime,@function,'EXECUTE')",
                ("runtime", runtime), ("function", AddMasterDataLoader.FunctionSignature)));
            // Global cities stay read-only at runtime (AI-18).
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@runtime,'locations.cities','INSERT')
                    OR has_table_privilege(@runtime,'locations.cities','UPDATE')
                    OR has_table_privilege(@runtime,'locations.cities','DELETE')
                """,
                ("runtime", runtime)));
        }

        // The API login calling the function directly is refused by PostgreSQL itself.
        var organization = await NewOrganizationAsync();
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [organization]);
        await using var call = new NpgsqlCommand(
            "SELECT security.load_master_data(@org, @load, '{}'::jsonb, decode(repeat('00',32),'hex'), true)",
            tenant.Connection, tenant.Transaction);
        call.Parameters.AddWithValue("org", organization);
        call.Parameters.AddWithValue("load", Guid.NewGuid());
        var denied = await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteScalarAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Shipped_synthetic_example_loads_and_a_reload_changes_nothing_but_the_audit()
    {
        var organization = await NewOrganizationAsync();
        var fixtureFile = Path.Combine(RepositoryRootLocator.Find(), "tests", "fixtures", "mdm-001", "synthetic-master-data.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(fixtureFile))!.AsObject();
        Assert.Equal("SYNTHETIC", document["classification"]!.GetValue<string>());
        document["owner_org_id"] = organization.ToString("D");
        // Cities are global: a unique city name keeps this test independent of every other load.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        RenameCity(document, "Synthetic MDM City", $"Synthetic MDM City {suffix}");

        var first = await LoadAsync(document, organization);
        Assert.Contains("cities: created=1 updated=0 unchanged=0", first.Output);
        Assert.Contains("service_areas: created=1 updated=0 unchanged=0", first.Output);
        Assert.Contains("operating_zones: created=2 updated=0 unchanged=0", first.Output);
        Assert.Contains("tariff_rules: created=2 updated=0 unchanged=0", first.Output);
        var snapshot = await SnapshotAsync(organization);

        var second = await LoadAsync(document, organization);
        Assert.Contains("cities: created=0 updated=0 unchanged=1", second.Output);
        Assert.Contains("service_areas: created=0 updated=0 unchanged=1", second.Output);
        Assert.Contains("operating_zones: created=0 updated=0 unchanged=2", second.Output);
        Assert.Contains("tariff_rules: created=0 updated=0 unchanged=2", second.Output);
        Assert.Equal(snapshot, await SnapshotAsync(organization));

        // One append-only audit row per load, in the loaded organization, with counts and the file hash only.
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_LOADED' AND entity_type='MASTER_DATA_LOAD'",
            ("org", organization)));
        var payloads = await ScalarAsync<string>(
            "SELECT string_agg(payload_redacted::text, '|') FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_LOADED'",
            ("org", organization));
        Assert.Contains(Convert.ToHexStringLower(first.Sha256), payloads);
        Assert.DoesNotContain("Synthetic", payloads);
        Assert.DoesNotContain("-107.", payloads);
        Assert.Null(await ScalarAsync<object>(
            "SELECT max(actor_id::text) FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_LOADED'",
            ("org", organization)) as string);
    }

    [PostgreSqlContractFact]
    public async Task A_load_for_one_tenant_is_invisible_to_another_and_each_tenant_owns_its_copy()
    {
        var tenantA = await NewOrganizationAsync();
        var tenantB = await NewOrganizationAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await LoadAsync(Document(tenantA, suffix), tenantA);

        Assert.Equal("1|2|2", await VisibleCountsAsync(tenantA));
        Assert.Equal("0|0|0", await VisibleCountsAsync(tenantB));

        // The same natural keys for tenant B create B's own rows; the global city is shared and unchanged.
        var loadB = await LoadAsync(Document(tenantB, suffix), tenantB);
        Assert.Contains("cities: created=0 updated=0 unchanged=1", loadB.Output);
        Assert.Contains("service_areas: created=1 updated=0 unchanged=0", loadB.Output);
        Assert.Equal("1|2|2", await VisibleCountsAsync(tenantB));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM locations.service_areas a JOIN locations.service_areas b
              ON a.city_id=b.city_id AND a.name=b.name AND a.id=b.id
            WHERE a.owner_org_id=@a AND b.owner_org_id=@b
            """,
            ("a", tenantA), ("b", tenantB)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@b AND entity_id IN (SELECT entity_id FROM platform.audit_logs WHERE org_id=@a)",
            ("a", tenantA), ("b", tenantB)));

        // A caller whose tenant context names another organization is refused before any read or write.
        await using var connection = new NpgsqlConnection(OperatorConnectionString());
        await EnsureOperatorLoginAsync();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            $"SET LOCAL ROLE {Loader}; SELECT set_config('app.current_org_ids', @orgs::uuid[]::text, true);",
            connection, transaction))
        {
            context.Parameters.AddWithValue("orgs", new[] { tenantB });
            await context.ExecuteNonQueryAsync();
        }

        await using var call = new NpgsqlCommand(
            "SELECT security.load_master_data(@org, @load, @doc::jsonb, decode(repeat('00',32),'hex'), false)",
            connection, transaction);
        call.Parameters.AddWithValue("org", tenantA);
        call.Parameters.AddWithValue("load", Guid.NewGuid());
        call.Parameters.AddWithValue("doc", Document(tenantA, suffix).ToJsonString());
        var refused = await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteScalarAsync());
        Assert.Equal("MDM001_TENANT_CONTEXT_MISMATCH", refused.MessageText);
    }

    [PostgreSqlContractFact]
    public async Task Dry_run_prints_counts_and_reference_diffs_and_writes_nothing()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var document = Document(organization, suffix);

        var dryRun = await LoadAsync(document, organization, dryRun: true);
        Assert.Contains("MDM001_DRY_RUN", dryRun.Output);
        Assert.Contains("service_areas: created=1 updated=0 unchanged=0", dryRun.Output);
        Assert.Contains("CREATE operating_zones[2]", dryRun.Output);
        Assert.DoesNotContain(suffix, dryRun.Output);
        Assert.DoesNotContain(organization.ToString("D"), dryRun.Output);
        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM locations.cities WHERE name=@name", ("name", $"MDM City {suffix}")));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));

        // After a real load, a dry run of an edited document reports field-level diffs only.
        await LoadAsync(document, organization);
        document["operating_zones"]![0]!["zone_type"] = "STANDARD";
        document["tariff_rules"]![1]!["status"] = "INACTIVE";
        var diff = await LoadAsync(document, organization, dryRun: true);
        Assert.Contains("UPDATE operating_zones[1] fields=zone_type", diff.Output);
        Assert.Contains("UPDATE tariff_rules[2] fields=status", diff.Output);
        Assert.Equal("CORE", await ScalarAsync<string>(
            "SELECT zone_type FROM locations.operating_zones WHERE owner_org_id=@org AND name=@name",
            ("org", organization), ("name", $"Core {suffix}")));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));

        // The applied update changes exactly those fields.
        var applied = await LoadAsync(document, organization);
        Assert.Contains("operating_zones: created=0 updated=1 unchanged=1", applied.Output);
        Assert.Equal("STANDARD", await ScalarAsync<string>(
            "SELECT zone_type FROM locations.operating_zones WHERE owner_org_id=@org AND name=@name",
            ("org", organization), ("name", $"Core {suffix}")));
    }

    [PostgreSqlContractFact]
    public async Task Invalid_files_are_rejected_before_the_database_with_integer_cents_only()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        foreach (var (mutate, expected) in new (Action<JsonObject>, string)[]
                 {
                     (d => d["tariff_rules"]![0]!["amount_cents"] = JsonValue.Create(120.5), "MDM001_AMOUNT_NOT_INTEGER_CENTS"),
                     (d => d["tariff_rules"]![0]!["amount_cents"] = "12000", "MDM001_AMOUNT_NOT_INTEGER_CENTS"),
                     (d => d["tariff_rules"]![0]!["amount_cents"] = -1, "MDM001_AMOUNT_NOT_INTEGER_CENTS"),
                     (d => d["cities"]![0]!.AsObject()["extra"] = 1, "MDM001_ENTRY_SHAPE"),
                     (d => d.Remove("driver_profiles"), "MDM001_ENTRY_SHAPE"),
                     (d => d["owner_org_id"] = Guid.NewGuid().ToString("D"), "MDM001_OWNER_ORGANIZATION_MISMATCH"),
                     (d => d["service_areas"]![0]!["polygon"]!["coordinates"]![0]![0]!.AsArray().RemoveAt(4), "MDM001_GEOMETRY_RING_NOT_CLOSED"),
                     (d => d["service_areas"]![0]!["polygon"]!["type"] = "Polygon", "MDM001_GEOMETRY_TYPE"),
                     (d => d["operating_zones"]!.AsArray().Add(d["operating_zones"]![0]!.DeepClone()), "MDM001_DUPLICATE_NATURAL_KEY"),
                     (d => d["tariff_rules"]![0]!["active_from"] = "2026-01-01", "MDM001_INSTANT"),
                     (d => d["tariff_rules"]![0]!["policy_version"] = "v 1", "MDM001_POLICY_VERSION"),
                     (d => d["tariff_rules"]![0]!.AsObject().Remove("policy_version"), "MDM001_ENTRY_SHAPE"),
                 })
        {
            var document = Document(organization, suffix);
            mutate(document);
            var rejected = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(document, organization));
            Assert.Equal(MasterDataLoader.ValidationExitCode, rejected.ExitCode);
            Assert.Contains(expected, rejected.CapturedOutput(), StringComparison.Ordinal);
        }

        // Exponent and fractional spellings of an integer are refused from the raw token.
        foreach (var spelling in new[] { "12000.0", "1.2e4", "1.2E4" })
        {
            var raw = Document(organization, suffix).ToJsonString()
                .Replace("\"amount_cents\":11100", $"\"amount_cents\":{spelling}", StringComparison.Ordinal);
            var validation = MasterDataDocumentValidator.Validate(System.Text.Encoding.UTF8.GetBytes(raw), organization);
            Assert.Contains(validation.Errors, error => error.EndsWith("MDM001_AMOUNT_NOT_INTEGER_CENTS", StringComparison.Ordinal));
        }

        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));
    }

    [PostgreSqlContractFact]
    public async Task Database_rejections_roll_back_the_whole_load_with_no_partial_write()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A self-intersecting (bow-tie) zone passes the structural checks and fails PostGIS validity after
        // the city and service area were already planned: nothing is written.
        var bowTie = Document(organization, suffix);
        bowTie["operating_zones"]![1]!["polygon"] = Polygon((-107.49, 24.71), (-107.41, 24.79), (-107.41, 24.71), (-107.49, 24.79));
        var invalid = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(bowTie, organization));
        Assert.Equal(MasterDataLoader.DatabaseExitCode, invalid.ExitCode);
        Assert.Contains("MDM001_GEOMETRY_INVALID at operating_zones[2]", invalid.Message);

        var outside = Document(organization, suffix);
        outside["operating_zones"]![1]!["polygon"] = Polygon((-106.0, 24.0), (-105.9, 24.0), (-105.9, 24.1), (-106.0, 24.1));
        var escaped = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(outside, organization));
        Assert.Contains("MDM001_ZONE_OUTSIDE_SERVICE_AREA at operating_zones[2]", escaped.Message);

        var unknownZone = Document(organization, suffix);
        unknownZone["tariff_rules"]![1]!["operating_zone"] = "Missing zone";
        var missing = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(unknownZone, organization));
        Assert.Contains("MDM001_OPERATING_ZONE_NOT_FOUND at tariff_rules[2]", missing.Message);

        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM locations.cities WHERE name=@name", ("name", $"MDM City {suffix}")));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));

        // After a good load, a changed price for the same natural key is refused and nothing moves:
        // a new price is a new rule with a later active_from.
        var document = Document(organization, suffix);
        await LoadAsync(document, organization);
        var snapshot = await SnapshotAsync(organization);
        document["tariff_rules"]![0]!["amount_cents"] = 99999;
        document["operating_zones"]![0]!["status"] = "INACTIVE";
        var immutable = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(document, organization));
        Assert.Contains("MDM001_TARIFF_AMOUNT_IMMUTABLE at tariff_rules[1]", immutable.Message);
        Assert.Equal(snapshot, await SnapshotAsync(organization));

        // A global city is never rewritten by a tenant load.
        var cityConflict = Document(organization, suffix);
        cityConflict["cities"]![0]!["timezone"] = "America/Mexico_City";
        var conflict = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(cityConflict, organization));
        Assert.Contains("MDM001_CITY_CONFLICT at cities[1]", conflict.Message);
        Assert.Equal(snapshot, await SnapshotAsync(organization));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));
    }

    [PostgreSqlContractFact]
    public async Task Driver_profiles_need_a_driver_membership_and_the_GATE007_flag_outside_synthetic_environments()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await LoadAsync(Document(organization, suffix), organization);
        var driver = await NewUserAsync(organization, "DRIVER");
        var viewer = await NewUserAsync(organization, "VIEWER");

        var document = Document(organization, suffix);
        document["driver_profiles"] = new JsonArray(DriverProfile(driver, suffix));

        // A pilot classification refuses before touching the database unless GATE-007 is attested.
        var gated = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(document, organization, environment: Pilot));
        Assert.Equal(MasterDataLoader.GateExitCode, gated.ExitCode);
        Assert.Contains("MDM001_DRIVER_PROFILES_REQUIRE_GATE_007", gated.Message);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM drivers.driver_profiles WHERE user_id=@user", ("user", driver)));
        Assert.False(new MasterDataEnvironment("Development", "PILOT_REAL_PEOPLE").IsSyntheticOnly);
        Assert.False(new MasterDataEnvironment("DevSynthetic", null).IsSyntheticOnly);
        Assert.True(new MasterDataEnvironment("DevSynthetic", "DEV_SYNTHETIC").IsSyntheticOnly);

        var loaded = await LoadAsync(document, organization);
        Assert.Contains("driver_profiles: created=1 updated=0 unchanged=0", loaded.Output);
        Assert.Contains("driver_service_areas: created=1 updated=0 unchanged=0", loaded.Output);
        Assert.DoesNotContain(driver.ToString("D"), loaded.Output);
        Assert.Equal("OWN|MOTORCYCLE|PENDING", await ScalarAsync<string>(
            "SELECT concat_ws('|',driver_type,vehicle_type,status) FROM drivers.driver_profiles WHERE user_id=@user AND org_id=@org",
            ("user", driver), ("org", organization)));

        var reload = await LoadAsync(document, organization, environment: Pilot, allowRealDriverProfiles: true);
        Assert.Contains("driver_profiles: created=0 updated=0 unchanged=1", reload.Output);
        Assert.Contains("driver_service_areas: created=0 updated=0 unchanged=1", reload.Output);

        // A user without an ACTIVE DRIVER membership of this organization never gets a profile.
        document["driver_profiles"] = new JsonArray(DriverProfile(viewer, suffix));
        var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(document, organization));
        Assert.Contains("MDM001_DRIVER_MEMBERSHIP_REQUIRED at driver_profiles[1]", refused.Message);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM drivers.driver_profiles WHERE user_id=@user", ("user", viewer)));

        // Another organization cannot take over the driver's profile.
        var other = await NewOrganizationAsync();
        await LoadAsync(Document(other, suffix), other);
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organization_memberships(user_id,organization_id,role) VALUES (@user,@org,'DRIVER')",
            ("user", driver), ("org", other));
        var takeover = Document(other, suffix);
        takeover["driver_profiles"] = new JsonArray(DriverProfile(driver, suffix));
        var blocked = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(takeover, other));
        Assert.Contains("MDM001_DRIVER_MEMBERSHIP_REQUIRED", blocked.Message);
        Assert.Equal(organization, await ScalarAsync<Guid>(
            "SELECT org_id FROM drivers.driver_profiles WHERE user_id=@user", ("user", driver)));
    }

    [PostgreSqlContractFact]
    public async Task Loader_refuses_privileged_or_runtime_connections()
    {
        var organization = await NewOrganizationAsync();
        var file = await WriteTempAsync(Document(organization, Guid.NewGuid().ToString("N")[..8]));
        try
        {
            foreach (var connectionString in new[] { fixture.DeploymentConnectionString, fixture.AppConnectionString })
            {
                var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataLoader.RunAsync(
                    connectionString, new MasterDataLoadOptions(file, organization, false, false), Testing,
                    TextWriter.Null, CancellationToken.None));
                Assert.Contains("MDM001_OPERATOR_LOGIN_REQUIRED", refused.Message);
            }
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
    }

    [PostgreSqlContractFact]
    public async Task Pricing_lane_rolls_back_and_reapplies_on_real_postgresql()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("mdm001updown");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(1, await ScalarAsync<long>(connectionString,
                $"SELECT count(*) FROM pg_proc WHERE proowner='{Executor}'::regrole"));

            // Down (both Pricing migrations; the adoption rollback is a no-op): the function and the history
            // rows go; roles, grants and loaded data stay.
            await MigratePricingAsync(connectionString, Migration.InitialDatabase);
            Assert.False(await ScalarAsync<bool>(connectionString,
                $"SELECT to_regprocedure('{AddMasterDataLoader.FunctionSignature}') IS NOT NULL"));
            Assert.True(await ScalarAsync<bool>(connectionString,
                $"SELECT to_regrole('{Executor}') IS NOT NULL AND to_regrole('{Loader}') IS NOT NULL"));
            Assert.False(await ScalarAsync<bool>(connectionString,
                $"SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_pricing WHERE \"MigrationId\"='{AddMasterDataLoader.MigrationId}')"));
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
                await transaction.RollbackAsync();
                var semantic = await new E002SemanticAssertions().AssertAsync(
                    connection, await E002NotificationStateReader.ReadAsync(connection));
                Assert.DoesNotContain("_PLUS_MDM001", semantic.RoutineMap, StringComparison.Ordinal);
            }

            // Roles are cluster-wide and shared with the fixture database, so a populated installation that
            // predates MDM-001 is simulated by removing this database's grants only; the lane restores them.
            await ExecuteAsync(connectionString, $"""
                REVOKE ALL ON SCHEMA security FROM {Loader};
                DROP OWNED BY {Executor};
                """);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString,
                $"SELECT count(*) FROM information_schema.column_privileges WHERE grantee='{Executor}'"));

            await MigratePricingAsync(connectionString, null);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await DatabaseBaselineAssertions.AssertMasterDataLoaderInstalledAsync(connection, transaction);
                await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
                await transaction.RollbackAsync();
                var semantic = await new E002SemanticAssertions().AssertAsync(
                    connection, await E002NotificationStateReader.ReadAsync(connection));
                Assert.EndsWith("_PLUS_MDM001_V1", semantic.RoutineMap, StringComparison.Ordinal);
            }

            Assert.Equal("APPLIED", (await new ModuleMigrationCoordinator().AssertAsync(connectionString, CancellationToken.None))
                .Single(state => state.Module == "Pricing").Status);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static JsonObject Document(Guid organization, string suffix)
    {
        JsonObject City() => new()
        {
            ["country_code"] = "MX",
            ["state_code"] = "SIN",
            ["name"] = $"MDM City {suffix}",
        };
        JsonObject Area() => new() { ["city"] = City(), ["name"] = $"Area {suffix}" };
        return new JsonObject
        {
            ["format"] = MasterDataDocumentValidator.Format,
            ["classification"] = "SYNTHETIC",
            ["owner_org_id"] = organization.ToString("D"),
            ["cities"] = new JsonArray(new JsonObject
            {
                ["country_code"] = "MX",
                ["state_code"] = "SIN",
                ["name"] = $"MDM City {suffix}",
                ["timezone"] = "America/Mazatlan",
                ["status"] = "ACTIVE",
            }),
            ["service_areas"] = new JsonArray(new JsonObject
            {
                ["city"] = City(),
                ["name"] = $"Area {suffix}",
                ["status"] = "ACTIVE",
                ["polygon"] = Polygon((-107.5, 24.7), (-107.3, 24.7), (-107.3, 24.9), (-107.5, 24.9)),
            }),
            ["operating_zones"] = new JsonArray(
                new JsonObject
                {
                    ["service_area"] = Area(),
                    ["name"] = $"Core {suffix}",
                    ["zone_type"] = "CORE",
                    ["status"] = "ACTIVE",
                    ["polygon"] = Polygon((-107.45, 24.75), (-107.35, 24.75), (-107.35, 24.85), (-107.45, 24.85)),
                },
                new JsonObject
                {
                    ["service_area"] = Area(),
                    ["name"] = $"Excluded {suffix}",
                    ["zone_type"] = "EXCLUDED",
                    ["status"] = "ACTIVE",
                    ["polygon"] = Polygon((-107.34, 24.86), (-107.31, 24.86), (-107.31, 24.89), (-107.34, 24.89)),
                }),
            ["tariff_rules"] = new JsonArray(
                new JsonObject
                {
                    ["city"] = City(),
                    ["service_area"] = null,
                    ["operating_zone"] = null,
                    ["pricing_tier"] = "OCCASIONAL",
                    ["service_type"] = "SAME_DAY",
                    ["amount_cents"] = 11100,
                    ["tax_mode"] = "EXEMPT",
                    ["policy_version"] = "synthetic-v1",
                    ["active_from"] = "2026-01-01T00:00:00Z",
                    ["active_to"] = null,
                    ["status"] = "ACTIVE",
                },
                new JsonObject
                {
                    ["city"] = City(),
                    ["service_area"] = $"Area {suffix}",
                    ["operating_zone"] = $"Core {suffix}",
                    ["pricing_tier"] = "OCCASIONAL",
                    ["service_type"] = "URGENT",
                    ["amount_cents"] = 22200,
                    ["tax_mode"] = "EXEMPT",
                    ["policy_version"] = "synthetic-v1",
                    ["active_from"] = "2026-01-01T00:00:00Z",
                    ["active_to"] = "2026-12-31T23:59:59Z",
                    ["status"] = "ACTIVE",
                }),
            ["driver_profiles"] = new JsonArray(),
        };
    }

    private static JsonObject DriverProfile(Guid user, string suffix) => new()
    {
        ["user_id"] = user.ToString("D"),
        ["home_city"] = new JsonObject { ["country_code"] = "MX", ["state_code"] = "SIN", ["name"] = $"MDM City {suffix}" },
        ["driver_type"] = "OWN",
        ["vehicle_type"] = "MOTORCYCLE",
        ["status"] = "PENDING",
        ["service_areas"] = new JsonArray(new JsonObject
        {
            ["city"] = new JsonObject { ["country_code"] = "MX", ["state_code"] = "SIN", ["name"] = $"MDM City {suffix}" },
            ["name"] = $"Area {suffix}",
            ["status"] = "ACTIVE",
        }),
    };

    /// <summary>A closed single-ring MultiPolygon from four corners.</summary>
    private static JsonObject Polygon(params (double X, double Y)[] corners)
    {
        var ring = new JsonArray();
        foreach (var (x, y) in corners.Append(corners[0]))
        {
            ring.Add(new JsonArray(x, y));
        }

        return new JsonObject
        {
            ["type"] = "MultiPolygon",
            ["coordinates"] = new JsonArray(new JsonArray(ring)),
        };
    }

    private static void RenameCity(JsonObject document, string from, string to)
    {
        foreach (var node in Descendants(document))
        {
            if (node is JsonObject item && item["name"]?.GetValue<string>() == from && item.ContainsKey("state_code"))
            {
                item["name"] = to;
            }
        }
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject item => item.Select(pair => pair.Value),
            JsonArray array => array.AsEnumerable(),
            _ => [],
        };
        foreach (var child in children.OfType<JsonNode>().ToArray())
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record LoadResult(string Output, byte[] Sha256);

    private async Task<LoadResult> LoadAsync(
        JsonObject document,
        Guid organization,
        bool dryRun = false,
        MasterDataEnvironment? environment = null,
        bool allowRealDriverProfiles = false)
    {
        await EnsureOperatorLoginAsync();
        var file = await WriteTempAsync(document);
        var output = new StringWriter();
        try
        {
            using var result = await MasterDataLoader.RunAsync(
                OperatorConnectionString(),
                new MasterDataLoadOptions(file, organization, dryRun, allowRealDriverProfiles),
                environment ?? Testing,
                output,
                CancellationToken.None);
            return new LoadResult(output.ToString(), SHA256.HashData(await File.ReadAllBytesAsync(file)));
        }
        catch (MasterDataLoadException exception)
        {
            exception.Data["output"] = output.ToString();
            throw;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<string> WriteTempAsync(JsonObject document)
    {
        var file = Path.Combine(Path.GetTempPath(), $"mdm001-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    private string OperatorConnectionString() =>
        new NpgsqlConnectionStringBuilder(fixture.DeploymentConnectionString)
        {
            Username = OperatorLogin,
            Password = OperatorPassword,
            Pooling = false,
            ApplicationName = "Paqueteria.MDM001.ContractTests",
        }.ConnectionString;

    /// <summary>The operator login: NOINHERIT, member of the loader grantee only (it SETs ROLE per transaction).</summary>
    private async Task EnsureOperatorLoginAsync() => await ExecuteAdminAsync($$"""
        DO $login$
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{{OperatorLogin}}') THEN
            CREATE ROLE {{OperatorLogin}} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
              PASSWORD '{{OperatorPassword}}';
          ELSE
            ALTER ROLE {{OperatorLogin}} WITH PASSWORD '{{OperatorPassword}}';
          END IF;
        END
        $login$;
        GRANT {{Loader}} TO {{OperatorLogin}};
        """);

    private async Task<Guid> NewOrganizationAsync()
    {
        var organization = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'MDM Sintética','MDM Sintética','ALLY')",
            ("org", organization));
        return organization;
    }

    private async Task<Guid> NewUserAsync(Guid organization, string role)
    {
        var user = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject) VALUES (@user,@subject);
            INSERT INTO organizations.organization_memberships(user_id,organization_id,role) VALUES (@user,@org,@role);
            """,
            ("user", user), ("subject", $"mdm001-{user:N}"), ("org", organization), ("role", role));
        return user;
    }

    /// <summary>Service areas, zones and tariff rules the API role sees under the tenant context of <paramref name="organization"/>.</summary>
    private async Task<string> VisibleCountsAsync(Guid organization)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [organization]);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM locations.service_areas) || '|' ||
                   (SELECT count(*) FROM locations.operating_zones) || '|' ||
                   (SELECT count(*) FROM pricing.tariff_rules)
            """,
            tenant.Connection, tenant.Transaction);
        var counts = (string)(await command.ExecuteScalarAsync())!;
        await tenant.RollbackAsync();
        return counts;
    }

    private Task<string> SnapshotAsync(Guid organization) => ScalarAsync<string>(
        """
        SELECT concat_ws(' || ',
          (SELECT string_agg(concat_ws(',',id,city_id,name,status,public.ST_AsText(polygon)), ';' ORDER BY id)
             FROM locations.service_areas WHERE owner_org_id=@org),
          (SELECT string_agg(concat_ws(',',id,service_area_id,name,zone_type,status,public.ST_AsText(polygon)), ';' ORDER BY id)
             FROM locations.operating_zones WHERE owner_org_id=@org),
          (SELECT string_agg(concat_ws(',',id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,
               amount_cents,tax_mode,active_from,active_to,status), ';' ORDER BY id)
             FROM pricing.tariff_rules WHERE owner_org_id=@org),
          (SELECT string_agg(concat_ws(',',id,user_id,home_city_id,driver_type,vehicle_type,status), ';' ORDER BY id)
             FROM drivers.driver_profiles WHERE org_id=@org))
        """,
        ("org", organization));

    private Task<string> RoleFlagsAsync(string role) => ScalarAsync<string>(
        """
        SELECT concat_ws('|',rolcanlogin,rolbypassrls,rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolinherit)
        FROM pg_roles WHERE rolname=@role
        """,
        ("role", role));

    private static async Task MigratePricingAsync(string connectionString, string? target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
            })
            .Options;
        await using var context = new PricingDbContext(options, new TenantDatabaseExecutionState());
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAdminAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal static class MasterDataLoadExceptionExtensions
{
    /// <summary>The console output the loader wrote before refusing (validation errors), captured by the test.</summary>
    public static string CapturedOutput(this MasterDataLoadException exception) =>
        exception.Data["output"] as string ?? string.Empty;
}
