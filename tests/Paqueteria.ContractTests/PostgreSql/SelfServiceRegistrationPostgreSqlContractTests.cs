using Identity.Application.Bootstrap;
using Identity.Infrastructure.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Organizations.Application;
using Organizations.Application.Registration;
using Organizations.Infrastructure.Persistence;
using Organizations.Infrastructure.Persistence.Migrations;
using Organizations.Infrastructure.Registration;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// REG-001 (AUTH-OPEN-REGISTRATION and the REG-* owner decisions) against real PostgreSQL: the
/// registration executor boundary, first sign-in exactly once, the one-organization limit under
/// concurrency, idempotent replay, the PLATFORM_ADMIN ALLY decision with its audit rows, and the
/// Organizations lane up and down on an isolated database. Every test uses its own users and
/// organizations, so the shared database needs no cleanup.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class SelfServiceRegistrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Executor = AddSelfServiceRegistration.ExecutorRole;

    [PostgreSqlContractFact]
    public async Task Executor_role_functions_and_grants_match_the_REG001_contract_exactly()
    {
        Assert.Equal(
            "f|t|f|f|f|f|t",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',rolcanlogin,rolbypassrls,rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolinherit)
                FROM pg_roles WHERE rolname=@executor
                """,
                ("executor", Executor)));
        foreach (var runtime in new[] { "paqueteria_app", "paqueteria_worker", PostgreSqlContractFixture.AppLogin })
        {
            Assert.False(await ScalarAsync<bool>(
                "SELECT pg_has_role(@runtime,@executor,'MEMBER') OR pg_has_role(@runtime,@executor,'SET')",
                ("runtime", runtime), ("executor", Executor)));
        }

        Assert.Equal(
            string.Join(',', AddSelfServiceRegistration.OwnedFunctions.Order(StringComparer.Ordinal)),
            await ScalarAsync<string>(
                "SELECT string_agg(oid::regprocedure::text, ',' ORDER BY oid::regprocedure::text) FROM pg_proc WHERE proowner=@executor::regrole",
                ("executor", Executor)));
        foreach (var signature in AddSelfServiceRegistration.OwnedFunctions)
        {
            Assert.Equal(
                "t|f|f|t",
                await ScalarAsync<string>(
                    """
                    SELECT concat_ws('|',p.prosecdef,
                      has_function_privilege('public',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_app',p.oid,'EXECUTE'))
                    FROM pg_proc p WHERE p.oid=@function::regprocedure
                    """,
                    ("function", signature)));
        }

        // No table-wide grant, no DELETE anywhere and no business table outside the three schemas.
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.table_privileges WHERE grantee=@executor",
            ("executor", Executor)));
        foreach (var table in new[] { "orders.orders", "platform.outbox_events", "platform.idempotency_keys", "allies.ally_relationships" })
        {
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@executor,@table,'SELECT') OR has_table_privilege(@executor,@table,'INSERT')
                    OR has_table_privilege(@executor,@table,'UPDATE') OR has_table_privilege(@executor,@table,'DELETE')
                """,
                ("executor", Executor), ("table", table)));
        }

        Assert.Equal("identity,organizations,platform", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'USAGE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));

        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertRegistrationExecutorInstalledAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_first_sign_ins_create_exactly_one_user_and_never_touch_an_existing_one()
    {
        var subject = $"reg001-subject-{Guid.NewGuid():N}";
        var registration = IdentityRegistration();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => registration.RegisterAsync(subject, CancellationToken.None).AsTask()));

        Assert.Equal("1|ACTIVE", await ScalarAsync<string>(
            "SELECT count(*) || '|' || max(status) FROM identity.users WHERE identity_subject=@subject",
            ("subject", subject)));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM organizations.organization_memberships m
            JOIN identity.users u ON u.id=m.user_id WHERE u.identity_subject=@subject
            """,
            ("subject", subject)));

        // A later sign-in of a SUSPENDED subject neither reactivates nor relinks it.
        await ExecuteAdminAsync("UPDATE identity.users SET status='SUSPENDED' WHERE identity_subject=@subject", ("subject", subject));
        await registration.RegisterAsync(subject, CancellationToken.None);
        Assert.Equal("1|SUSPENDED", await ScalarAsync<string>(
            "SELECT count(*) || '|' || max(status) FROM identity.users WHERE identity_subject=@subject",
            ("subject", subject)));
    }

    [PostgreSqlContractFact]
    public async Task Business_is_created_active_with_its_admin_membership_and_audit_row_and_replays_by_key()
    {
        var userId = await NewUserAsync();
        var service = RegistrationService();
        var command = Command(userId, "reg001-key-business-000001", "BUSINESS", "Negocio Sintético SA", "Negocio Sintético");

        var created = await service.CreateOrganizationAsync(command, CancellationToken.None);
        var replayed = await service.CreateOrganizationAsync(command, CancellationToken.None);
        var conflicting = await service.CreateOrganizationAsync(
            command with { DisplayName = "Otro nombre" }, CancellationToken.None);

        Assert.Equal(SelfServiceOrganizationOutcome.Created, created.Outcome);
        Assert.Equal("ACTIVE", created.Organization!.Status);
        Assert.Equal("BUSINESS_ADMIN", created.Organization.Role);
        Assert.Equal(SelfServiceOrganizationOutcome.Replayed, replayed.Outcome);
        Assert.Equal(created.Organization, replayed.Organization);
        Assert.Equal(SelfServiceOrganizationOutcome.IdempotencyConflict, conflicting.Outcome);
        Assert.Null(conflicting.Organization);

        var organizationId = created.Organization.OrganizationId;
        Assert.Equal(
            PostgreSqlSelfServiceRegistrationService.DeriveOrganizationId(userId, command.IdempotencyKey),
            organizationId);
        Assert.Equal("BUSINESS_ADMIN|ACTIVE|true", await ScalarAsync<string>(
            "SELECT role || '|' || status || '|' || is_default FROM organizations.organization_memberships WHERE organization_id=@id",
            ("id", organizationId)));
        Assert.Equal("ORGANIZATION_SELF_REGISTERED|{\"organization_type\": \"BUSINESS\"}", await ScalarAsync<string>(
            """
            SELECT string_agg(action || '|' || payload_redacted::text, ',')
            FROM platform.audit_logs WHERE org_id=@id AND actor_id=@user
            """,
            ("id", organizationId), ("user", userId)));
        Assert.Equal(userId, await ScalarAsync<Guid>(
            "SELECT self_service_creator_user_id FROM organizations.organizations WHERE id=@id", ("id", organizationId)));
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_creations_with_different_keys_leave_exactly_one_open_organization_per_person()
    {
        var userId = await NewUserAsync();
        var service = RegistrationService();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => service.CreateOrganizationAsync(
            Command(userId, $"reg001-key-race-{index:D10}", index % 2 == 0 ? "ALLY" : "BUSINESS", $"Carrera {index}", $"Carrera {index}"),
            CancellationToken.None)));

        Assert.Single(results, result => result.Outcome == SelfServiceOrganizationOutcome.Created);
        Assert.All(
            results.Where(result => result.Outcome != SelfServiceOrganizationOutcome.Created),
            result => Assert.Equal(SelfServiceOrganizationOutcome.LimitReached, result.Outcome));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM organizations.organizations WHERE self_service_creator_user_id=@user", ("user", userId)));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM organizations.organization_memberships WHERE user_id=@user", ("user", userId)));
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_requests_with_the_same_key_create_once_and_replay_the_rest()
    {
        var userId = await NewUserAsync();
        var service = RegistrationService();
        var command = Command(userId, "reg001-key-same-000000001", "ALLY", "Aliado Concurrente", "Aliado Concurrente");

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => service.CreateOrganizationAsync(command, CancellationToken.None)));

        Assert.Single(results, result => result.Outcome == SelfServiceOrganizationOutcome.Created);
        Assert.All(results, result => Assert.Equal(results[0].Organization!.OrganizationId, result.Organization!.OrganizationId));
        Assert.All(
            results.Where(result => result.Outcome != SelfServiceOrganizationOutcome.Created),
            result => Assert.Equal(SelfServiceOrganizationOutcome.Replayed, result.Outcome));
    }

    [PostgreSqlContractFact]
    public async Task Ally_is_pending_until_a_platform_admin_decides_and_a_rejection_frees_the_slot()
    {
        var (adminId, platformId) = await NewPlatformAdminAsync();
        var creatorId = await NewUserAsync();
        var service = RegistrationService();

        var ally = await service.CreateOrganizationAsync(
            Command(creatorId, "reg001-key-ally-00000001", "ALLY", "Aliado Uno SA", "Aliado Uno"), CancellationToken.None);
        Assert.Equal("PENDING_APPROVAL", ally.Organization!.Status);
        Assert.Equal("ALLY_ADMIN", ally.Organization.Role);
        var allyId = ally.Organization.OrganizationId;

        // Pending: the creator's membership exists but resolves to no context (IDENTITY-ORG-ACTIVE-REQUIRED).
        Assert.DoesNotContain(allyId.ToString("D"), await ResolveContextAsync(creatorId));
        Assert.Contains(
            await service.ListOwnApplicationsAsync(creatorId, CancellationToken.None),
            application => application.OrganizationId == allyId && application.Status == "PENDING_APPROVAL");
        Assert.Contains(
            (await service.ListPendingAlliesAsync(adminId, platformId, 200, CancellationToken.None))!,
            pending => pending.OrganizationId == allyId);

        // A second application while the first is pending is refused.
        Assert.Equal(SelfServiceOrganizationOutcome.LimitReached, (await service.CreateOrganizationAsync(
            Command(creatorId, "reg001-key-ally-00000002", "BUSINESS", "Negocio Dos", "Negocio Dos"),
            CancellationToken.None)).Outcome);

        Assert.Equal(AllyDecisionOutcome.Rejected,
            await service.DecideAllyAsync(adminId, platformId, allyId, false, "reg001-reject", CancellationToken.None));
        Assert.Equal("CLOSED", await ScalarAsync<string>(
            "SELECT status FROM organizations.organizations WHERE id=@id", ("id", allyId)));
        Assert.DoesNotContain(allyId.ToString("D"), await ResolveContextAsync(creatorId));
        // Rejected, then approved: the decision already taken cannot be reversed.
        Assert.Equal(AllyDecisionOutcome.Conflict,
            await service.DecideAllyAsync(adminId, platformId, allyId, true, null, CancellationToken.None));
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE entity_id=@id AND action='ALLY_ORGANIZATION_REJECTED'",
            ("id", allyId)));
        Assert.Equal($"{platformId:D},{allyId:D}", await ScalarAsync<string>(
            """
            SELECT string_agg(org_id::text, ',' ORDER BY (org_id=@ally), org_id)
            FROM platform.audit_logs WHERE entity_id=@ally AND action='ALLY_ORGANIZATION_REJECTED' AND actor_id=@admin
            """,
            ("ally", allyId), ("admin", adminId)));

        // "Queda cerrada; puede volver a solicitar": the CLOSED ALLY frees the slot for a new application.
        var second = await service.CreateOrganizationAsync(
            Command(creatorId, "reg001-key-ally-00000003", "ALLY", "Aliado Dos SA", "Aliado Dos"), CancellationToken.None);
        Assert.Equal(SelfServiceOrganizationOutcome.Created, second.Outcome);
        var secondId = second.Organization!.OrganizationId;

        Assert.Equal(AllyDecisionOutcome.Approved,
            await service.DecideAllyAsync(adminId, platformId, secondId, true, "reg001-approve", CancellationToken.None));
        Assert.Equal(AllyDecisionOutcome.Approved,
            await service.DecideAllyAsync(adminId, platformId, secondId, true, "reg001-approve-again", CancellationToken.None));
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE entity_id=@id AND action='ALLY_ORGANIZATION_APPROVED'",
            ("id", secondId)));
        Assert.Contains(secondId.ToString("D"), await ResolveContextAsync(creatorId));
        // Approval only activates the ALLY: no ally relationship is created (REG-ALLY-APPROVAL-ACTIVATE-ONLY).
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM allies.ally_relationships WHERE ally_org_id=@id", ("id", secondId)));
        Assert.Equal(AllyDecisionOutcome.NotFound,
            await service.DecideAllyAsync(adminId, platformId, Guid.NewGuid(), true, null, CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task Only_an_active_platform_admin_of_an_active_platform_organization_may_list_or_decide()
    {
        var (adminId, platformId) = await NewPlatformAdminAsync();
        var creatorId = await NewUserAsync();
        var service = RegistrationService();
        var allyId = (await service.CreateOrganizationAsync(
            Command(creatorId, "reg001-key-guard-0000001", "ALLY", "Aliado Guardado", "Aliado Guardado"),
            CancellationToken.None)).Organization!.OrganizationId;

        // The ALLY creator is ALLY_ADMIN of its own pending organization, never a platform administrator.
        Assert.Null(await service.ListPendingAlliesAsync(creatorId, allyId, 10, CancellationToken.None));
        Assert.Equal(AllyDecisionOutcome.Forbidden,
            await service.DecideAllyAsync(creatorId, allyId, allyId, true, null, CancellationToken.None));

        // A PLATFORM_ADMIN member of a BUSINESS organization is not a platform administrator either.
        var businessAdminId = await NewUserAsync();
        var businessId = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'B','B','BUSINESS');
            INSERT INTO organizations.organization_memberships(user_id,organization_id,role) VALUES (@user,@org,'PLATFORM_ADMIN');
            """,
            ("org", businessId), ("user", businessAdminId));
        Assert.Null(await service.ListPendingAlliesAsync(businessAdminId, businessId, 10, CancellationToken.None));
        Assert.Equal(AllyDecisionOutcome.Forbidden,
            await service.DecideAllyAsync(businessAdminId, businessId, allyId, true, null, CancellationToken.None));

        // A suspended platform admin loses the capability immediately.
        await ExecuteAdminAsync("UPDATE identity.users SET status='SUSPENDED' WHERE id=@user", ("user", adminId));
        Assert.Equal(AllyDecisionOutcome.Forbidden,
            await service.DecideAllyAsync(adminId, platformId, allyId, true, null, CancellationToken.None));
        Assert.Equal("PENDING_APPROVAL", await ScalarAsync<string>(
            "SELECT status FROM organizations.organizations WHERE id=@id", ("id", allyId)));
        await ExecuteAdminAsync("UPDATE organizations.organizations SET status='CLOSED' WHERE id=@id", ("id", allyId));
    }

    [PostgreSqlContractFact]
    public async Task Inactive_users_cannot_create_organizations()
    {
        var userId = await NewUserAsync();
        await ExecuteAdminAsync("UPDATE identity.users SET status='SUSPENDED' WHERE id=@user", ("user", userId));

        var result = await RegistrationService().CreateOrganizationAsync(
            Command(userId, "reg001-key-inactive-00001", "BUSINESS", "Suspendido", "Suspendido"), CancellationToken.None);

        Assert.Equal(SelfServiceOrganizationOutcome.Forbidden, result.Outcome);
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM organizations.organizations WHERE self_service_creator_user_id=@user", ("user", userId)));
    }

    [PostgreSqlContractFact]
    public async Task Organizations_lane_rolls_back_and_reapplies_on_real_postgresql_and_refuses_pending_rows()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("reg001updown");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(5, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole"));

            // Down: functions gone, three-value status check back, history row removed.
            await MigrateOrganizationsAsync(connectionString, Migration.InitialDatabase);
            Assert.Equal(0, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole"));
            Assert.Equal(AddSelfServiceRegistration.PreviousStatusCheck, await ScalarAsync<string>(connectionString,
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='organizations_status_check'"));
            Assert.False(await ScalarAsync<bool>(connectionString,
                $"SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_organizations WHERE \"MigrationId\"='{AddSelfServiceRegistration.MigrationId}')"));

            // Simulate an installation that predates REG-001 entirely, then apply the lane on it.
            await ExecuteAsync(connectionString, """
                DROP INDEX organizations.organizations_one_open_self_service_uq;
                ALTER TABLE organizations.organizations DROP COLUMN self_service_creator_user_id;
                """);
            await MigrateOrganizationsAsync(connectionString, null);
            Assert.Equal(AddSelfServiceRegistration.StatusCheck, await ScalarAsync<string>(connectionString,
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='organizations_status_check'"));
            Assert.True(await ScalarAsync<bool>(connectionString,
                "SELECT to_regclass('organizations.organizations_one_open_self_service_uq') IS NOT NULL"));
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await DatabaseBaselineAssertions.AssertRegistrationExecutorInstalledAsync(connection, transaction);
                await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
                await transaction.RollbackAsync();
            }

            // A PENDING_APPROVAL organization blocks the rollback, and nothing changes.
            await ExecuteAsync(connectionString, """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type,status)
                VALUES ('0b0b0b0b-0000-0000-0000-000000000001','P','P','ALLY','PENDING_APPROVAL');
                """);
            var blocked = await Assert.ThrowsAsync<PostgresException>(
                () => MigrateOrganizationsAsync(connectionString, Migration.InitialDatabase));
            Assert.Equal("REG001_DOWNGRADE_BLOCKED_PENDING_ORGANIZATIONS", blocked.MessageText);
            Assert.Equal(5, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole"));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task A_business_created_after_a_rejected_ally_becomes_the_usable_default()
    {
        var (adminId, platformId) = await NewPlatformAdminAsync();
        var creatorId = await NewUserAsync();
        var service = RegistrationService();
        var allyId = (await service.CreateOrganizationAsync(
            Command(creatorId, "reg001-key-default-ally-1", "ALLY", "Aliado Rechazado", "Aliado Rechazado"),
            CancellationToken.None)).Organization!.OrganizationId;
        Assert.Equal("true", await MembershipDefaultAsync(creatorId, allyId));
        Assert.Equal(AllyDecisionOutcome.Rejected,
            await service.DecideAllyAsync(adminId, platformId, allyId, false, null, CancellationToken.None));

        var business = await service.CreateOrganizationAsync(
            Command(creatorId, "reg001-key-default-biz-01", "BUSINESS", "Negocio Nuevo", "Negocio Nuevo"),
            CancellationToken.None);

        // REG-DEFAULT-MEMBERSHIP-RELEASE: the dead ALLY membership gave up the slot and the business took it.
        Assert.Equal(SelfServiceOrganizationOutcome.Created, business.Outcome);
        var businessId = business.Organization!.OrganizationId;
        Assert.Equal("false", await MembershipDefaultAsync(creatorId, allyId));
        Assert.Equal("true", await MembershipDefaultAsync(creatorId, businessId));
        var context = await ResolveContextAsync(creatorId);
        Assert.Contains($"\"organization_id\": \"{businessId:D}\"", context);
        Assert.Contains("\"is_default\": true", context);
        Assert.DoesNotContain(allyId.ToString("D"), context);
    }

    [PostgreSqlContractFact]
    public async Task A_default_membership_in_an_active_organization_is_left_untouched()
    {
        var userId = await NewUserAsync();
        var existingId = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'Existente','Existente','BUSINESS');
            INSERT INTO organizations.organization_memberships(user_id,organization_id,role,is_default) VALUES (@user,@org,'VIEWER',true);
            """,
            ("org", existingId), ("user", userId));

        var created = await RegistrationService().CreateOrganizationAsync(
            Command(userId, "reg001-key-default-keep-1", "BUSINESS", "Negocio Propio", "Negocio Propio"),
            CancellationToken.None);

        Assert.Equal(SelfServiceOrganizationOutcome.Created, created.Outcome);
        Assert.Equal("true", await MembershipDefaultAsync(userId, existingId));
        Assert.Equal("false", await MembershipDefaultAsync(userId, created.Organization!.OrganizationId));
    }

    private Task<string> MembershipDefaultAsync(Guid userId, Guid organizationId) => ScalarAsync<string>(
        "SELECT is_default::text FROM organizations.organization_memberships WHERE user_id=@user AND organization_id=@org",
        ("user", userId), ("org", organizationId));

    private static async Task MigrateOrganizationsAsync(string connectionString, string? target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
            })
            .Options;
        await using var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState());
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private PostgreSqlSelfServiceRegistrationService RegistrationService() => new(
        fixture.AppDataSource,
        Options.Create(new TenancyOptions { Provider = TenancyProviderKind.PostgreSql, CommandTimeoutSeconds = 30 }),
        new VerifiedEmailSelfServiceOrganizationAuthorizer(),
        NullLogger<PostgreSqlSelfServiceRegistrationService>.Instance);

    private PostgreSqlIdentityRegistration IdentityRegistration() => new(
        fixture.AppDataSource,
        Options.Create(new IdentityBootstrapOptions
        {
            Provider = IdentityBootstrapProviderKind.PostgreSql,
            CommandTimeoutSeconds = 30,
        }),
        NullLogger<PostgreSqlIdentityRegistration>.Instance);

    private static CreateSelfServiceOrganizationCommand Command(
        Guid userId, string key, string type, string legalName, string displayName) =>
        new(userId, true, key, type, legalName, displayName, "reg001-contract");

    private async Task<Guid> NewUserAsync()
    {
        var userId = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO identity.users(id,identity_subject) VALUES (@id,@subject)",
            ("id", userId), ("subject", $"reg001-{userId:N}"));
        return userId;
    }

    private async Task<(Guid AdminId, Guid PlatformId)> NewPlatformAdminAsync()
    {
        var adminId = await NewUserAsync();
        var platformId = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'Plataforma','Plataforma','PLATFORM');
            INSERT INTO organizations.organization_memberships(user_id,organization_id,role) VALUES (@user,@org,'PLATFORM_ADMIN');
            """,
            ("org", platformId), ("user", adminId));
        return (adminId, platformId);
    }

    private async Task<string> ResolveContextAsync(Guid userId)
    {
        var subject = await ScalarAsync<string>("SELECT identity_subject FROM identity.users WHERE id=@id", ("id", userId));
        return await ScalarAsync<string>(
            "SELECT COALESCE(security.resolve_identity_context(@subject)::text, '')", ("subject", subject));
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
