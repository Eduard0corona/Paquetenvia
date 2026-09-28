using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Persistence.Migrations;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// MDM-001-OPERATOR-LOADER against real PostgreSQL/PostGIS: the executor and operator-grantee boundary read
/// from catalog ACLs, the GATE-007 deployment marker enforced by the database, PLATFORM-only city creation,
/// non-overlapping tariff windows, idempotent reload, cross-tenant isolation, one audit row per load naming
/// the operator, dry-run, strict rejection with no partial write, and the Pricing lane down and up on an
/// isolated database. Every test uses its own organizations and city names; the deployment marker is set by
/// each load (the collection runs sequentially) and left SYNTHETIC.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class MasterDataLoaderPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Executor = AddMasterDataLoader.ExecutorRole;
    private const string Loader = AddMasterDataLoader.LoaderRole;
    private const string OperatorLogin = "paqueteria_mdm_operator_login_test";
    private const string Synthetic = "SYNTHETIC";
    private const string Real = "REAL";
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
        Assert.Equal("t|t", await ScalarAsync<string>(
            """
            SELECT concat_ws('|',p.prosecdef,@search_path = ANY(p.proconfig))
            FROM pg_proc p WHERE p.oid=@function::regprocedure
            """,
            ("function", AddMasterDataLoader.FunctionSignature), ("search_path", AddMasterDataLoader.SearchPath)));

        // m3: the exact function ACL from the catalog: the owner and the operator grantee, nothing else.
        Assert.Equal(
            "paqueteria_master_data_executor:EXECUTE:false,paqueteria_master_data_loader:EXECUTE:false",
            await ScalarAsync<string>(
                """
                SELECT string_agg(entry, ',' ORDER BY entry) FROM (
                  SELECT CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END
                         || ':' || a.privilege_type || ':' || a.is_grantable AS entry
                  FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a WHERE p.oid=@function::regprocedure) acl
                """,
                ("function", AddMasterDataLoader.FunctionSignature)));

        // Exactly the AI-18 column grants from pg_attribute.attacl; no table-level ACL entry for either role.
        Assert.Equal(
            string.Join(',', AddMasterDataLoader.ExecutorColumnGrants.Order(StringComparer.Ordinal)),
            await ScalarAsync<string>(
                """
                SELECT string_agg(entry, ',' ORDER BY entry COLLATE "C") FROM (
                  SELECT n.nspname || '.' || c.relname || '.' || att.attname || ':' || a.privilege_type AS entry
                  FROM pg_attribute att JOIN pg_class c ON c.oid=att.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                  CROSS JOIN LATERAL aclexplode(att.attacl) a
                  WHERE a.grantee=@executor::regrole) grants
                """,
                ("executor", Executor)));
        Assert.Equal(
            string.Join(',', AddMasterDataLoader.ExecutorColumnGrants.Order(StringComparer.Ordinal)),
            string.Join(',', DatabaseBaselineAssertions.MasterDataExecutorGrants.Order(StringComparer.Ordinal)));
        Assert.Equal(116, AddMasterDataLoader.ExecutorColumnGrants.Count);
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT (SELECT count(*) FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
                    WHERE a.grantee IN (@executor::regrole,@loader::regrole))
                 + (SELECT count(*) FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
                    WHERE a.grantee=@loader::regrole)
            """,
            ("executor", Executor), ("loader", Loader)));
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
    public async Task Boundary_assertions_detect_a_widened_acl_or_an_unexpected_executor_member()
    {
        foreach (var (widen, narrow, expected) in new[]
                 {
                     ($"GRANT EXECUTE ON FUNCTION {AddMasterDataLoader.FunctionSignature} TO paqueteria_bootstrap",
                      $"REVOKE EXECUTE ON FUNCTION {AddMasterDataLoader.FunctionSignature} FROM paqueteria_bootstrap",
                      "master data loader function ACL differs"),
                     ($"GRANT SELECT (id,status) ON identity.users TO {Executor} WITH GRANT OPTION",
                      $"REVOKE GRANT OPTION FOR SELECT (id,status) ON identity.users FROM {Executor}",
                      "master data executor column grant is grantable"),
                     ($"GRANT SELECT ON platform.master_data_deployment_gate TO {Loader}",
                      $"REVOKE SELECT ON platform.master_data_deployment_gate FROM {Loader}",
                      "platform.master_data_deployment_gate grants SELECT"),
                     ($"GRANT {Executor} TO {OperatorLogin}",
                      $"REVOKE {Executor} FROM {OperatorLogin}",
                      "master data executor has a member that is not a deployment principal"),
                     ($"GRANT {Loader} TO paqueteria_migrator",
                      $"REVOKE {Loader} FROM paqueteria_migrator",
                      "master data loader has a member that is also a deployment principal"),
                     (LegacyFunctionSql,
                      $"DROP FUNCTION {AddMasterDataLoader.LegacyFunctionSignature}",
                      "master data executor owns another function"),
                 })
        {
            await EnsureOperatorLoginAsync();
            await ExecuteAdminAsync(widen);
            try
            {
                await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                var violations = await Assert.ThrowsAsync<DatabaseAssertionException>(
                    () => DatabaseBaselineAssertions.AssertMasterDataLoaderInstalledAsync(connection, transaction));
                Assert.Contains(violations.Violations, violation => violation.StartsWith(expected, StringComparison.Ordinal));
                await transaction.RollbackAsync();
            }
            finally
            {
                await ExecuteAdminAsync(narrow);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task Runtime_roles_gain_no_master_data_write_capability()
    {
        foreach (var runtime in new[] { "paqueteria_app", "paqueteria_worker" })
        {
            Assert.False(await ScalarAsync<bool>(
                "SELECT has_function_privilege(@runtime,@function,'EXECUTE')",
                ("runtime", runtime), ("function", AddMasterDataLoader.FunctionSignature)));
            // Global cities stay read-only at runtime (AI-18); the deployment marker is out of reach.
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@runtime,'locations.cities','INSERT')
                    OR has_table_privilege(@runtime,'locations.cities','UPDATE')
                    OR has_table_privilege(@runtime,'locations.cities','DELETE')
                    OR has_table_privilege(@runtime,'platform.master_data_deployment_gate','SELECT,INSERT,UPDATE,DELETE')
                """,
                ("runtime", runtime)));
        }

        // The API login calling the function directly is refused by PostgreSQL itself.
        var organization = await NewOrganizationAsync();
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [organization]);
        await using var call = new NpgsqlCommand(
            "SELECT security.load_master_data(@org, @load, '{}'::json, decode(repeat('00',32),'hex'), true)",
            tenant.Connection, tenant.Transaction);
        call.Parameters.AddWithValue("org", organization);
        call.Parameters.AddWithValue("load", Guid.NewGuid());
        var denied = await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteScalarAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task The_deployment_marker_is_written_only_by_the_migrator_path()
    {
        // The operator login can neither read nor write it.
        await EnsureOperatorLoginAsync();
        await using (var connection = new NpgsqlConnection(OperatorConnectionString()))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
                     {
                         $"SET ROLE {Loader}; UPDATE platform.master_data_deployment_gate SET gate_007_closed=true",
                         "UPDATE platform.master_data_deployment_gate SET gate_007_closed=true",
                         "SELECT deployment_class FROM platform.master_data_deployment_gate",
                     })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal("42501", denied.SqlState);
                await using var reset = new NpgsqlCommand("RESET ROLE", connection);
                await reset.ExecuteNonQueryAsync();
            }
        }

        // The marker forces RLS; its only policy admits the migrator.
        Assert.Equal("t|t|master_data_deployment_gate_migrator", await ScalarAsync<string>(
            """
            SELECT concat_ws('|',c.relrowsecurity,c.relforcerowsecurity,
              (SELECT string_agg(polname,',') FROM pg_policy WHERE polrelid=c.oid))
            FROM pg_class c WHERE c.oid='platform.master_data_deployment_gate'::regclass
            """));

        // The migrator command writes it, audited against the PLATFORM organization; GATE-007 is recorded
        // only for a REAL database, and a REAL database is never marked SYNTHETIC again.
        var platform = await NewOrganizationAsync("PLATFORM");
        var tenant = await NewOrganizationAsync();
        var refusedTenant = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataGate.SetAsync(
            fixture.DeploymentConnectionString, new MasterDataGateOptions(Real, false, tenant), null, TextWriter.Null, CancellationToken.None));
        Assert.Contains("MDM001_GATE_PLATFORM_ORGANIZATION_REQUIRED", refusedTenant.Message);
        await SetGateAsync(Synthetic, false);
        var output = new StringWriter();
        await MasterDataGate.SetAsync(fixture.DeploymentConnectionString, new MasterDataGateOptions(Real, true, platform), null, output, CancellationToken.None);
        Assert.Contains("MDM001_GATE_SET deployment_class=REAL gate_007_closed=true (audited)", output.ToString());
        Assert.Equal("REAL|t", await ScalarAsync<string>(
            "SELECT concat_ws('|',deployment_class,gate_007_closed) FROM platform.master_data_deployment_gate"));
        var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataGate.SetAsync(
            fixture.DeploymentConnectionString, new MasterDataGateOptions(Synthetic, true, platform), null, TextWriter.Null, CancellationToken.None));
        Assert.Contains("MDM001_GATE_007_ONLY_FOR_REAL", refused.Message);
        var pilot = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataGate.SetAsync(
            fixture.DeploymentConnectionString, new MasterDataGateOptions(Synthetic, false, platform), "PILOT_REAL_PEOPLE", TextWriter.Null, CancellationToken.None));
        Assert.Contains("MDM001_GATE_SYNTHETIC_REFUSED", pilot.Message);
        var backwards = await Assert.ThrowsAsync<MasterDataLoadException>(() => MasterDataGate.SetAsync(
            fixture.DeploymentConnectionString, new MasterDataGateOptions(Synthetic, false, platform), null, TextWriter.Null, CancellationToken.None));
        Assert.Contains("MDM001_GATE_REAL_TO_SYNTHETIC_REFUSED", backwards.Message);
        await MasterDataGate.SetAsync(fixture.DeploymentConnectionString, new MasterDataGateOptions(Real, false, platform), null, TextWriter.Null, CancellationToken.None);
        Assert.Equal("REAL|f", await ScalarAsync<string>(
            "SELECT concat_ws('|',deployment_class,gate_007_closed) FROM platform.master_data_deployment_gate"));

        // Two audited changes, append-only, in the PLATFORM organization, with a pseudonym and no login name.
        var audit = await ScalarAsync<string>(
            """
            SELECT string_agg(payload_redacted::text, '|' ORDER BY occurred_at)
            FROM platform.audit_logs
            WHERE org_id=@org AND action='MASTER_DATA_GATE_CHANGED' AND entity_type='MASTER_DATA_DEPLOYMENT_GATE'
            """,
            ("org", platform));
        Assert.Equal(2, audit.Split('|').Length);
        Assert.Contains("\"from\": {\"gate_007_closed\": false, \"deployment_class\": \"SYNTHETIC\"}", audit);
        Assert.Contains("\"to\": {\"gate_007_closed\": true, \"deployment_class\": \"REAL\"}", audit);
        var deploymentLogin = new NpgsqlConnectionStringBuilder(fixture.DeploymentConnectionString).Username!;
        Assert.Contains($"\"operator_ref\": \"{OperatorReference(deploymentLogin)}\"", audit);
        Assert.DoesNotContain(deploymentLogin, audit);
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_GATE_CHANGED'", ("org", tenant)));
        await SetGateAsync(Synthetic, false);
    }

    [PostgreSqlContractFact]
    public async Task The_database_enforces_GATE007_and_the_file_classification_on_a_direct_call()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);
        await LoadAsync(Document(organization, suffix), organization);
        var driver = await NewUserAsync(organization, "DRIVER");
        var withProfile = Document(organization, suffix);
        withProfile["driver_profiles"] = new JsonArray(DriverProfile(driver, suffix));

        // A REAL database with GATE-007 open: the function itself refuses driver profiles, whatever the
        // operator's environment variables say (the job's own check is bypassed by calling the function).
        await SetGateAsync(Real, false);
        withProfile["classification"] = "REVIEWED";
        var gated = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, withProfile.ToJsonString()));
        Assert.Equal("MDM001_DRIVER_PROFILES_REQUIRE_GATE_007", gated.MessageText);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM drivers.driver_profiles WHERE user_id=@user", ("user", driver)));

        // A REAL database never takes a SYNTHETIC file, and a SYNTHETIC database never takes a REVIEWED one.
        var synthetic = Document(organization, suffix);
        var classification = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, synthetic.ToJsonString()));
        Assert.Equal("MDM001_CLASSIFICATION_NOT_ALLOWED", classification.MessageText);
        await SetGateAsync(Synthetic, false);
        var reviewed = Document(organization, suffix);
        reviewed["classification"] = "REVIEWED";
        classification = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, reviewed.ToJsonString()));
        Assert.Equal("MDM001_CLASSIFICATION_NOT_ALLOWED", classification.MessageText);

        // Once the migrator records GATE-007 closed, a REVIEWED file with profiles loads in a REAL database.
        await SetGateAsync(Real, true);
        var loaded = await CallFunctionAsync(organization, withProfile.ToJsonString());
        Assert.Equal(1, loaded.RootElement.GetProperty("counts").GetProperty("driver_profiles").GetProperty("created").GetInt64());
        Assert.Equal("REAL", loaded.RootElement.GetProperty("deployment_class").GetString());
        await SetGateAsync(Synthetic, false);
    }

    [PostgreSqlContractFact]
    public async Task Shipped_synthetic_examples_load_and_a_reload_changes_nothing_but_the_audit()
    {
        var platform = await NewOrganizationAsync("PLATFORM");
        var organization = await NewOrganizationAsync();
        var directory = Path.Combine(RepositoryRootLocator.Find(), "tests", "fixtures", "mdm-001");
        var cities = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "synthetic-platform-cities.json")))!.AsObject();
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "synthetic-master-data.json")))!.AsObject();
        Assert.Equal(Synthetic, cities["classification"]!.GetValue<string>());
        Assert.Equal(Synthetic, document["classification"]!.GetValue<string>());
        Assert.Empty(document["cities"]!.AsArray());
        cities["owner_org_id"] = platform.ToString("D");
        document["owner_org_id"] = organization.ToString("D");
        // Cities are global: a unique city name keeps this test independent of every other load.
        var suffix = Suffix();
        RenameCity(cities, "Synthetic MDM City", $"Synthetic MDM City {suffix}");
        RenameCity(document, "Synthetic MDM City", $"Synthetic MDM City {suffix}");

        var cityLoad = await LoadAsync(cities, platform);
        Assert.Contains("cities: created=1 updated=0 unchanged=0", cityLoad.Output);
        Assert.Equal("ACTIVE", await ScalarAsync<string>(
            "SELECT status FROM locations.cities WHERE name=@name", ("name", $"Synthetic MDM City {suffix}")));

        var first = await LoadAsync(document, organization);
        Assert.Contains("service_areas: created=1 updated=0 unchanged=0", first.Output);
        Assert.Contains("operating_zones: created=2 updated=0 unchanged=0", first.Output);
        Assert.Contains("tariff_rules: created=2 updated=0 unchanged=0", first.Output);
        Assert.Contains("MDM001_NOTE policy_version was validated but is NOT stored yet", first.Output);
        Assert.Contains($"file_sha256={Convert.ToHexStringLower(first.Sha256)}", first.Output);
        var snapshot = await SnapshotAsync(organization);

        var second = await LoadAsync(document, organization);
        Assert.Contains("service_areas: created=0 updated=0 unchanged=1", second.Output);
        Assert.Contains("operating_zones: created=0 updated=0 unchanged=2", second.Output);
        Assert.Contains("tariff_rules: created=0 updated=0 unchanged=2", second.Output);
        Assert.Equal(snapshot, await SnapshotAsync(organization));

        // One append-only audit row per load, in the loaded organization, naming the operator login, with the
        // file hash, PostgreSQL's own hash of the document and the counts only.
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_LOADED' AND entity_type='MASTER_DATA_LOAD'",
            ("org", organization)));
        var payloads = await ScalarAsync<string>(
            "SELECT string_agg(payload_redacted::text, '|') FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_LOADED'",
            ("org", organization));
        Assert.Contains(Convert.ToHexStringLower(first.Sha256), payloads);
        Assert.Contains("document_sha256_computed", payloads);
        Assert.Contains($"\"operator_ref\": \"{OperatorReference(OperatorLogin)}\"", payloads);
        Assert.DoesNotContain(OperatorLogin, payloads);
        Assert.DoesNotContain("Synthetic", payloads);
        Assert.DoesNotContain("-107.", payloads);
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='MASTER_DATA_LOADED'", ("org", platform)));
    }

    [PostgreSqlContractFact]
    public async Task A_load_for_one_tenant_is_invisible_to_another_and_each_tenant_owns_its_copy()
    {
        var tenantA = await NewOrganizationAsync();
        var tenantB = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);
        await LoadAsync(Document(tenantA, suffix), tenantA);

        Assert.Equal("1|2|2", await VisibleCountsAsync(tenantA));
        Assert.Equal("0|0|0", await VisibleCountsAsync(tenantB));

        // The same natural keys for tenant B create B's own rows on the shared global city.
        var loadB = await LoadAsync(Document(tenantB, suffix), tenantB);
        Assert.Contains("service_areas: created=1 updated=0 unchanged=0", loadB.Output);
        Assert.Equal("1|2|2", await VisibleCountsAsync(tenantB));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM locations.service_areas a JOIN locations.service_areas b
              ON a.city_id=b.city_id AND a.name=b.name AND a.id=b.id
            WHERE a.owner_org_id=@a AND b.owner_org_id=@b
            """,
            ("a", tenantA), ("b", tenantB)));

        // A caller whose tenant context names another organization is refused before any read or write.
        await EnsureOperatorLoginAsync();
        await using var connection = new NpgsqlConnection(OperatorConnectionString());
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
            "SELECT security.load_master_data(@org, @load, @doc::json, decode(repeat('00',32),'hex'), false)",
            connection, transaction);
        call.Parameters.AddWithValue("org", tenantA);
        call.Parameters.AddWithValue("load", Guid.NewGuid());
        call.Parameters.AddWithValue("doc", Document(tenantA, suffix).ToJsonString());
        var refused = await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteScalarAsync());
        Assert.Equal("MDM001_TENANT_CONTEXT_MISMATCH", refused.MessageText);
    }

    [PostgreSqlContractFact]
    public async Task Only_a_platform_organization_creates_cities_and_tenants_reference_active_cities_only()
    {
        var tenant = await NewOrganizationAsync();
        var platform = await NewOrganizationAsync("PLATFORM");
        var suffix = Suffix();

        // A tenant load may not create a city, even a valid one.
        var withCity = Document(tenant, suffix);
        withCity["cities"] = new JsonArray(CityEntry(suffix));
        var refused = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(withCity, tenant));
        Assert.Contains("MDM001_CITIES_REQUIRE_PLATFORM_ORGANIZATION at cities", refused.Message);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM locations.cities WHERE name=@name", ("name", CityName(suffix))));

        // Without the city, the tenant's references do not resolve.
        var missing = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(Document(tenant, suffix), tenant));
        Assert.Contains("MDM001_CITY_NOT_FOUND at service_areas[1]", missing.Message);

        // The job refuses a non-Mexican country or a zone outside the Mexican allowlist before the database.
        foreach (var (mutate, code) in new (Action<JsonObject>, string)[]
                 {
                     (city => city["timezone"] = "Europe/Madrid", "MDM001_CITY_TIMEZONE_NOT_ALLOWED"),
                     (city => city["timezone"] = "America/Ensenada", "MDM001_CITY_TIMEZONE_NOT_ALLOWED"),
                     (city => city["country_code"] = "US", "MDM001_CITY_COUNTRY_NOT_SUPPORTED"),
                     (city => city["status"] = "INACTIVE", "MDM001_ENTRY_SHAPE"),
                 })
        {
            var entry = CityEntry(suffix);
            mutate(entry);
            var bad = CitiesDocument(platform, entry);
            var rejected = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(bad, platform));
            Assert.Contains(code, rejected.CapturedOutput(), StringComparison.Ordinal);
        }

        // The function repeats the allowlist on a direct call.
        var wrongZone = CityEntry(suffix);
        wrongZone["timezone"] = "Europe/Madrid";
        var direct = await Assert.ThrowsAsync<PostgresException>(
            () => CallFunctionAsync(platform, CitiesDocument(platform, wrongZone).ToJsonString()));
        Assert.Equal("MDM001_CITY_TIMEZONE_NOT_ALLOWED", direct.MessageText);

        // The platform creates it ACTIVE; a second platform load with another zone is a conflict.
        await LoadAsync(CitiesDocument(platform, CityEntry(suffix)), platform);
        Assert.Equal("America/Mazatlan|ACTIVE", await ScalarAsync<string>(
            "SELECT concat_ws('|',timezone,status) FROM locations.cities WHERE name=@name", ("name", CityName(suffix))));
        var other = CityEntry(suffix);
        other["timezone"] = "America/Mexico_City";
        var conflict = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(CitiesDocument(platform, other), platform));
        Assert.Contains("MDM001_CITY_CONFLICT at cities[1]", conflict.Message);
        await LoadAsync(Document(tenant, suffix), tenant);

        // An INACTIVE city can no longer be referenced by any load.
        await ExecuteAdminAsync("UPDATE locations.cities SET status='INACTIVE' WHERE name=@name", ("name", CityName(suffix)));
        var another = await NewOrganizationAsync();
        var inactive = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(Document(another, suffix), another));
        Assert.Contains("MDM001_CITY_NOT_FOUND at service_areas[1]", inactive.Message);
        var reactivate = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(CitiesDocument(platform, CityEntry(suffix)), platform));
        Assert.Contains("MDM001_CITY_CONFLICT", reactivate.Message);
    }

    [PostgreSqlContractFact]
    public async Task Overlapping_active_tariff_windows_are_rejected_and_a_new_price_must_close_the_old_one()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);

        // Within one file: the job refuses before the database, and a direct call is refused by PostgreSQL.
        var overlapping = Document(organization, suffix);
        overlapping["tariff_rules"]!.AsArray().Add(Tariff(suffix, "SAME_DAY", 12300, "2026-06-01T00:00:00Z", null));
        var inFile = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(overlapping, organization));
        Assert.Contains("tariff_rules[3]: MDM001_TARIFF_OVERLAP", inFile.CapturedOutput());
        await SetGateAsync(Synthetic, false);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, overlapping.ToJsonString()));
        Assert.Equal("MDM001_TARIFF_OVERLAP", direct.MessageText);

        // Against stored rules: the stored open-ended SAME_DAY rule overlaps a new price from June.
        await LoadAsync(Document(organization, suffix), organization);
        var newPrice = Document(organization, suffix);
        newPrice["tariff_rules"] = new JsonArray(Tariff(suffix, "SAME_DAY", 12300, "2026-06-01T00:00:00Z", null));
        var stored = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(newPrice, organization));
        Assert.Contains("MDM001_TARIFF_OVERLAP at tariff_rules[1]", stored.Message);
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM pricing.tariff_rules WHERE owner_org_id=@org", ("org", organization)));

        // Closing the old rule in the same file makes the new price valid; an INACTIVE rule never overlaps.
        var closed = Document(organization, suffix);
        closed["tariff_rules"]![0]!["active_to"] = "2026-06-01T00:00:00Z";
        closed["tariff_rules"]!.AsArray().Add(Tariff(suffix, "SAME_DAY", 12300, "2026-06-01T00:00:00Z", null));
        var applied = await LoadAsync(closed, organization);
        Assert.Contains("tariff_rules: created=1 updated=1 unchanged=1", applied.Output);
        var inactive = Document(organization, suffix);
        inactive["tariff_rules"] = new JsonArray(Tariff(suffix, "SAME_DAY", 99900, "2026-03-01T00:00:00Z", null, "INACTIVE"));
        await LoadAsync(inactive, organization);
        Assert.Equal(4, await ScalarAsync<long>("SELECT count(*) FROM pricing.tariff_rules WHERE owner_org_id=@org", ("org", organization)));
    }

    [PostgreSqlContractFact]
    public async Task Dry_run_prints_counts_and_reference_diffs_and_writes_nothing()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);
        var document = Document(organization, suffix);

        var dryRun = await LoadAsync(document, organization, dryRun: true);
        Assert.Contains("MDM001_DRY_RUN", dryRun.Output);
        Assert.Contains("service_areas: created=1 updated=0 unchanged=0", dryRun.Output);
        Assert.Contains("CREATE operating_zones[2]", dryRun.Output);
        Assert.Contains("MDM001_NOTE policy_version was validated but is NOT stored yet", dryRun.Output);
        Assert.DoesNotContain(suffix, dryRun.Output);
        Assert.DoesNotContain(organization.ToString("D"), dryRun.Output);
        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
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
        var suffix = Suffix();
        foreach (var (mutate, expected) in new (Action<JsonObject>, string)[]
                 {
                     (d => d["tariff_rules"]![0]!["amount_cents"] = JsonValue.Create(120.5), "MDM001_AMOUNT_NOT_INTEGER_CENTS"),
                     (d => d["tariff_rules"]![0]!["amount_cents"] = "12000", "MDM001_AMOUNT_NOT_INTEGER_CENTS"),
                     (d => d["tariff_rules"]![0]!["amount_cents"] = -1, "MDM001_AMOUNT_NOT_INTEGER_CENTS"),
                     (d => d["service_areas"]![0]!.AsObject()["extra"] = 1, "MDM001_ENTRY_SHAPE"),
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

        // Exponent, fractional and negative-zero spellings of an integer are refused from the raw token.
        foreach (var spelling in new[] { "12000.0", "1.2e4", "1.2E4", "-0" })
        {
            var raw = AmountSpelling(Document(organization, suffix), spelling);
            var validation = MasterDataDocumentValidator.Validate(Encoding.UTF8.GetBytes(raw), organization);
            Assert.Contains(validation.Errors, error => error.EndsWith("MDM001_AMOUNT_NOT_INTEGER_CENTS", StringComparison.Ordinal));
        }

        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));
    }

    [PostgreSqlContractFact]
    public async Task PostgreSQL_accepts_exactly_the_same_cents_spellings_and_refuses_duplicate_keys()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);
        await SetGateAsync(Synthetic, false);

        // MDM-001 m5: the function sees the json text, so 1.2e4 and -0 fail there too (jsonb would read 12000
        // and 0), while the plain token is accepted in a dry run.
        foreach (var spelling in new[] { "12000.0", "1.2e4", "-0" })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(
                () => CallFunctionAsync(organization, AmountSpelling(Document(organization, suffix), spelling)));
            Assert.Equal("MDM001_AMOUNT_NOT_INTEGER_CENTS", refused.MessageText);
            Assert.Equal("tariff_rules[1]", refused.Hint);
        }

        var accepted = await CallFunctionAsync(organization, AmountSpelling(Document(organization, suffix), "11100"), dryRun: true);
        Assert.Equal(2, accepted.RootElement.GetProperty("counts").GetProperty("tariff_rules").GetProperty("created").GetInt64());

        // A duplicated key (jsonb would silently keep the last one) is refused, at any depth.
        var text = Document(organization, suffix).ToJsonString();
        foreach (var duplicated in new[]
                 {
                     text.Replace("\"format\":", "\"format\":\"x\",\"format\":", StringComparison.Ordinal),
                     text.Replace("\"zone_type\":\"CORE\"", "\"zone_type\":\"EXCLUDED\",\"zone_type\":\"CORE\"", StringComparison.Ordinal),
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, duplicated));
            Assert.Equal("MDM001_DUPLICATE_PROPERTY", refused.MessageText);
            Assert.Contains(MasterDataDocumentValidator.Validate(Encoding.UTF8.GetBytes(duplicated), organization).Errors,
                error => error.EndsWith("MDM001_DUPLICATE_PROPERTY", StringComparison.Ordinal));
        }
    }

    [PostgreSqlContractFact]
    public async Task Database_limits_repeat_the_job_limits_on_a_direct_call()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);
        await SetGateAsync(Synthetic, false);

        var outOfRange = Document(organization, suffix);
        outOfRange["service_areas"]![0]!["polygon"] = Polygon((179.5, 24.7), (181.0, 24.7), (181.0, 24.9), (179.5, 24.9));
        var range = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, outOfRange.ToJsonString()));
        Assert.Equal("MDM001_GEOMETRY_OUT_OF_RANGE", range.MessageText);

        var tooMany = Document(organization, suffix);
        var rules = new JsonArray();
        for (var index = 0; index < 5000; index++)
        {
            rules.Add(Tariff(suffix, "SAME_DAY", 100 + index, $"2026-01-01T00:00:00Z", null, "INACTIVE"));
        }

        tooMany["tariff_rules"] = rules;
        tooMany["operating_zones"] = new JsonArray(Enumerable.Range(0, 5000).Select(_ => (JsonNode?)JsonNode.Parse("{}")).ToArray());
        tooMany["service_areas"] = new JsonArray(Enumerable.Range(0, 5000).Select(_ => (JsonNode?)JsonNode.Parse("{}")).ToArray());
        tooMany["driver_profiles"] = new JsonArray(Enumerable.Range(0, 5000).Select(_ => (JsonNode?)JsonNode.Parse("{\"service_areas\":[{}]}")).ToArray());
        var entries = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, tooMany.ToJsonString()));
        Assert.Equal("MDM001_DOCUMENT_TOO_LARGE", entries.MessageText);

        var vertices = Document(organization, suffix);
        var ring = new JsonArray();
        for (var index = 0; index < 100_001; index++)
        {
            ring.Add(new JsonArray(-107.5 + index * 1e-6, 24.7));
        }

        ring.Add(new JsonArray(-107.3, 24.9));
        ring.Add(new JsonArray(-107.5, 24.9));
        ring.Add(new JsonArray(-107.5, 24.7));
        var polygon = new JsonObject { ["type"] = "MultiPolygon", ["coordinates"] = new JsonArray(new JsonArray(ring)) };
        vertices["service_areas"]![0]!["polygon"] = polygon;
        var areas = vertices["service_areas"]!.AsArray();
        var twin = areas[0]!.DeepClone();
        twin["name"] = $"Area twin {suffix}";
        areas.Add(twin);
        var budget = await Assert.ThrowsAsync<PostgresException>(() => CallFunctionAsync(organization, vertices.ToJsonString()));
        Assert.Equal("MDM001_GEOMETRY_TOO_LARGE", budget.MessageText);
        Assert.Equal("service_areas[2]", budget.Hint);

        Assert.Equal("0|0|0", await VisibleCountsAsync(organization));
    }

    [PostgreSqlContractFact]
    public async Task Database_rejections_roll_back_the_whole_load_with_no_partial_write()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);

        // A self-intersecting (bow-tie) zone passes the structural checks and fails PostGIS validity after
        // the service area was already planned: nothing is written.
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
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org", ("org", organization)));
    }

    [PostgreSqlContractFact]
    public async Task Driver_profiles_need_a_driver_membership_and_the_GATE007_flag_outside_synthetic_environments()
    {
        var organization = await NewOrganizationAsync();
        var suffix = Suffix();
        await EnsureCityAsync(suffix);
        await LoadAsync(Document(organization, suffix), organization);
        var driver = await NewUserAsync(organization, "DRIVER");
        var viewer = await NewUserAsync(organization, "VIEWER");

        var document = Document(organization, suffix);
        document["driver_profiles"] = new JsonArray(DriverProfile(driver, suffix));

        // The job's own check (defence in depth) refuses before touching the database.
        var gated = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(document, organization, environment: Pilot));
        Assert.Equal(MasterDataLoader.GateExitCode, gated.ExitCode);
        Assert.Contains("MDM001_DRIVER_PROFILES_REQUIRE_GATE_007", gated.Message);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM drivers.driver_profiles WHERE user_id=@user", ("user", driver)));
        Assert.False(new MasterDataEnvironment("Development", "PILOT_REAL_PEOPLE").IsSyntheticOnly);
        Assert.False(new MasterDataEnvironment("DevSynthetic", null).IsSyntheticOnly);
        Assert.True(new MasterDataEnvironment("DevSynthetic", "DEV_SYNTHETIC").IsSyntheticOnly);

        // Even with the operator's flag, a REAL database with GATE-007 open refuses in PostgreSQL.
        var reviewed = JsonNode.Parse(document.ToJsonString())!.AsObject();
        reviewed["classification"] = "REVIEWED";
        var closedInDatabase = await Assert.ThrowsAsync<MasterDataLoadException>(() => LoadAsync(
            reviewed, organization, environment: Pilot, allowRealDriverProfiles: true, gate: Real));
        Assert.Contains("MDM001_DRIVER_PROFILES_REQUIRE_GATE_007 at driver_profiles", closedInDatabase.Message);

        var loaded = await LoadAsync(document, organization);
        Assert.Contains("driver_profiles: created=1 updated=0 unchanged=0", loaded.Output);
        Assert.Contains("driver_service_areas: created=1 updated=0 unchanged=0", loaded.Output);
        Assert.DoesNotContain(driver.ToString("D"), loaded.Output);
        Assert.Equal("OWN|MOTORCYCLE|PENDING", await ScalarAsync<string>(
            "SELECT concat_ws('|',driver_type,vehicle_type,status) FROM drivers.driver_profiles WHERE user_id=@user AND org_id=@org",
            ("user", driver), ("org", organization)));

        var reload = await LoadAsync(reviewed, organization, environment: Pilot, allowRealDriverProfiles: true, gate: Real, gate007Closed: true);
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
        var file = await WriteTempAsync(Document(organization, Suffix()));
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
            // rows go; roles, grants, the deployment marker and loaded data stay (MDM-001 m7).
            await MigratePricingAsync(connectionString, Migration.InitialDatabase);
            Assert.False(await ScalarAsync<bool>(connectionString,
                $"SELECT to_regprocedure('{AddMasterDataLoader.FunctionSignature}') IS NOT NULL"));
            Assert.True(await ScalarAsync<bool>(connectionString,
                $"SELECT to_regrole('{Executor}') IS NOT NULL AND to_regrole('{Loader}') IS NOT NULL AND to_regclass('{AddMasterDataLoader.GateTable}') IS NOT NULL"));
            Assert.Equal(116L, await ScalarAsync<long>(connectionString,
                $"SELECT count(*) FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a WHERE a.grantee='{Executor}'::regrole"));
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
            // predates MDM-001 is simulated by removing this database's grants and its deployment marker; the
            // lane restores both.
            await ExecuteAsync(connectionString, $"""
                REVOKE ALL ON SCHEMA security FROM {Loader};
                DROP OWNED BY {Executor};
                DROP TABLE {AddMasterDataLoader.GateTable};
                """);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString,
                $"SELECT count(*) FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a WHERE a.grantee='{Executor}'::regrole"));

            // MDM-001 N1: the first published version installed an ungated jsonb overload, owned by the
            // executor and executable by the loader. Up removes it.
            await ExecuteAsync(connectionString, LegacyFunctionSql);
            await MigratePricingAsync(connectionString, null);
            Assert.False(await ScalarAsync<bool>(connectionString,
                $"SELECT to_regprocedure('{AddMasterDataLoader.LegacyFunctionSignature}') IS NOT NULL"));
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

            Assert.Equal("paqueteria_migrator", await ScalarAsync<string>(connectionString,
                $"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='{AddMasterDataLoader.GateTable}'::regclass"));
            Assert.True(await ScalarAsync<bool>(connectionString,
                $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='{AddMasterDataLoader.GateTable}'::regclass"));

            // Down removes the jsonb overload as well; Up then leaves only the json function.
            await ExecuteAsync(connectionString, LegacyFunctionSql);
            await MigratePricingAsync(connectionString, Migration.InitialDatabase);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_proc WHERE proname='load_master_data'"));
            await MigratePricingAsync(connectionString, null);
            Assert.Equal(AddMasterDataLoader.FunctionSignature, await ScalarAsync<string>(connectionString,
                "SELECT string_agg(oid::regprocedure::text, ',') FROM pg_proc WHERE proname='load_master_data'"));
            Assert.Equal("APPLIED", (await new ModuleMigrationCoordinator().AssertAsync(connectionString, CancellationToken.None))
                .Single(state => state.Module == "Pricing").Status);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>The first published signature, installed as that version did (ungated, loader-executable).</summary>
    private static readonly string LegacyFunctionSql = $$"""
        CREATE FUNCTION security.load_master_data(p_organization_id uuid, p_load_id uuid, p_document jsonb,
          p_document_sha256 bytea, p_dry_run boolean)
        RETURNS jsonb LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS 'SELECT NULL::jsonb';
        REVOKE ALL ON FUNCTION {{AddMasterDataLoader.LegacyFunctionSignature}} FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION {{AddMasterDataLoader.LegacyFunctionSignature}} TO {{Loader}};
        ALTER FUNCTION {{AddMasterDataLoader.LegacyFunctionSignature}} OWNER TO {{Executor}};
        """;

    /// <summary>MDM-001 N3: the audit pseudonym of a login, as platform staff recompute it.</summary>
    private static string OperatorReference(string login) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(AddMasterDataLoader.OperatorReferencePrefix + login)));

    private static string CityName(string suffix) => $"MDM City {suffix}";

    private static JsonObject CityReference(string suffix) => new()
    {
        ["country_code"] = "MX",
        ["state_code"] = "SIN",
        ["name"] = CityName(suffix),
    };

    private static JsonObject CityEntry(string suffix) => new()
    {
        ["country_code"] = "MX",
        ["state_code"] = "SIN",
        ["name"] = CityName(suffix),
        ["timezone"] = "America/Mazatlan",
    };

    private static JsonObject CitiesDocument(Guid platform, JsonObject city) => new()
    {
        ["format"] = MasterDataDocumentValidator.Format,
        ["classification"] = Synthetic,
        ["owner_org_id"] = platform.ToString("D"),
        ["cities"] = new JsonArray(city),
        ["service_areas"] = new JsonArray(),
        ["operating_zones"] = new JsonArray(),
        ["tariff_rules"] = new JsonArray(),
        ["driver_profiles"] = new JsonArray(),
    };

    /// <summary>The city is global: a PLATFORM organization's load creates it (MDM-001 M2).</summary>
    private async Task EnsureCityAsync(string suffix)
    {
        var platform = await NewOrganizationAsync("PLATFORM");
        await LoadAsync(CitiesDocument(platform, CityEntry(suffix)), platform);
    }

    private static JsonObject Tariff(string suffix, string serviceType, long amount, string from, string? to, string status = "ACTIVE") => new()
    {
        ["city"] = CityReference(suffix),
        ["service_area"] = null,
        ["operating_zone"] = null,
        ["pricing_tier"] = "OCCASIONAL",
        ["service_type"] = serviceType,
        ["amount_cents"] = amount,
        ["tax_mode"] = "EXEMPT",
        ["policy_version"] = "synthetic-v1",
        ["active_from"] = from,
        ["active_to"] = to,
        ["status"] = status,
    };

    private static JsonObject Document(Guid organization, string suffix)
    {
        JsonObject Area() => new() { ["city"] = CityReference(suffix), ["name"] = $"Area {suffix}" };
        var zoned = Tariff(suffix, "URGENT", 22200, "2026-01-01T00:00:00Z", "2026-12-31T23:59:59Z");
        zoned["service_area"] = $"Area {suffix}";
        zoned["operating_zone"] = $"Core {suffix}";
        return new JsonObject
        {
            ["format"] = MasterDataDocumentValidator.Format,
            ["classification"] = Synthetic,
            ["owner_org_id"] = organization.ToString("D"),
            ["cities"] = new JsonArray(),
            ["service_areas"] = new JsonArray(new JsonObject
            {
                ["city"] = CityReference(suffix),
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
                Tariff(suffix, "SAME_DAY", 11100, "2026-01-01T00:00:00Z", null),
                zoned),
            ["driver_profiles"] = new JsonArray(),
        };
    }

    /// <summary>The document text with the first tariff amount spelled exactly as given (not re-serialized).</summary>
    private static string AmountSpelling(JsonObject document, string spelling)
    {
        var text = document.ToJsonString();
        const string Original = "\"amount_cents\":11100";
        Assert.Contains(Original, text, StringComparison.Ordinal);
        var index = text.IndexOf(Original, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), $"\"amount_cents\":{spelling}", text.AsSpan(index + Original.Length));
    }

    private static JsonObject DriverProfile(Guid user, string suffix) => new()
    {
        ["user_id"] = user.ToString("D"),
        ["home_city"] = CityReference(suffix),
        ["driver_type"] = "OWN",
        ["vehicle_type"] = "MOTORCYCLE",
        ["status"] = "PENDING",
        ["service_areas"] = new JsonArray(new JsonObject
        {
            ["city"] = CityReference(suffix),
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
        bool allowRealDriverProfiles = false,
        string gate = Synthetic,
        bool gate007Closed = false)
    {
        await EnsureOperatorLoginAsync();
        await SetGateAsync(gate, gate007Closed);
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
            await SetGateAsync(Synthetic, false);
        }
    }

    /// <summary>
    /// Calls the function directly as the operator grantee, bypassing every check of the job, with the
    /// document text passed as is.
    /// </summary>
    private async Task<JsonDocument> CallFunctionAsync(Guid organization, string documentText, bool dryRun = false)
    {
        await EnsureOperatorLoginAsync();
        await using var connection = new NpgsqlConnection(OperatorConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            $"SET LOCAL ROLE {Loader}; SELECT set_config('app.current_org_ids', @orgs::uuid[]::text, true);",
            connection, transaction))
        {
            context.Parameters.AddWithValue("orgs", new[] { organization });
            await context.ExecuteNonQueryAsync();
        }

        await using var call = new NpgsqlCommand(
            "SELECT security.load_master_data(@org, @load, @doc, @sha, @dry_run)::text", connection, transaction);
        call.Parameters.AddWithValue("org", organization);
        call.Parameters.AddWithValue("load", Guid.NewGuid());
        call.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Json) { Value = documentText });
        call.Parameters.AddWithValue("sha", SHA256.HashData(Encoding.UTF8.GetBytes(documentText)));
        call.Parameters.AddWithValue("dry_run", dryRun);
        var result = JsonDocument.Parse((string)(await call.ExecuteScalarAsync())!);
        await transaction.CommitAsync();
        return result;
    }

    private Task SetGateAsync(string deploymentClass, bool gate007Closed) => ExecuteAdminAsync(
        """
        INSERT INTO platform.master_data_deployment_gate(singleton,deployment_class,gate_007_closed)
        VALUES (true,@class,@closed)
        ON CONFLICT (singleton) DO UPDATE SET deployment_class=EXCLUDED.deployment_class, gate_007_closed=EXCLUDED.gate_007_closed
        """,
        ("class", deploymentClass), ("closed", gate007Closed));

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

    private async Task<Guid> NewOrganizationAsync(string type = "ALLY")
    {
        var organization = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'MDM Sintética','MDM Sintética',@type)",
            ("org", organization), ("type", type));
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
