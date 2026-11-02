using System.Text.Json;
using Drivers.Application.Documents;
using Drivers.Application.Eligibility;
using Drivers.Infrastructure;
using Drivers.Infrastructure.Documents;
using Drivers.Infrastructure.Eligibility;
using Drivers.Infrastructure.Persistence;
using Drivers.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 on a real PostgreSQL 18 / PostGIS 3.6 as the NOBYPASSRLS runtime role: the Drivers
/// lane step <c>20261102000100_AddInPersonDriverDocuments</c> (exact catalog; Up, Down and Up again; adoption of the AI-06
/// DDL on a fresh installation and creation on an upgraded one; drift refused; rollback refused while in-person data
/// exists), FORCE RLS and cross-tenant isolation of <c>drivers.driver_documents</c>, and the in-person service: the
/// record with its audit row and idempotency answer in one transaction, the revoke, the lists, the uniform 404 and the
/// DSP-001 eligibility of what it records. No row, audit payload or answer carries a key, a hash or a number.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DriverDocumentsPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Drivers";

    private static readonly string[] FullHistory =
    [
        AdoptCanonicalDriversBaseline.MigrationId,
        AdoptCanonicalDriverPositions.MigrationId,
        AddDriverVoiceBridge.MigrationId,
        AddInPersonDriverDocuments.MigrationId,
    ];

    // ------------------------------------------------------------------ migration

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_the_in_person_columns_as_the_latest_drivers_migration()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(AddInPersonDriverDocuments.MigrationId, verified.MigrationId);
        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal("APPLIED", applied.Status);
        Assert.Equal(FullHistory, await HistoryAsync(fixture.DeploymentConnectionString));
        await AssertCanonicalShapeAsync(fixture.DeploymentConnectionString);

        // The table keeps its tenant policy and the runtime privileges of every Drivers table (AI-18 unchanged).
        var connectionString = fixture.DeploymentConnectionString;
        Assert.Equal(
            ["driver_documents_tenant|ALL|PERMISSIVE|true|true"],
            await ListAsync(connectionString,
                """
                SELECT policyname || '|' || cmd || '|' || permissive || '|' ||
                       (qual LIKE '%security.app_allowed_org(org_id)%')::text || '|' ||
                       (with_check LIKE '%security.app_allowed_org(org_id)%')::text
                FROM pg_policies WHERE schemaname='drivers' AND tablename='driver_documents'
                """));
        Assert.Equal(
            "true|true|true",
            await ScalarAsync<string>(connectionString,
                """
                SELECT has_table_privilege('paqueteria_app','drivers.driver_documents','SELECT')::text || '|' ||
                       has_table_privilege('paqueteria_app','drivers.driver_documents','INSERT')::text || '|' ||
                       has_table_privilege('paqueteria_app','drivers.driver_documents','UPDATE')::text
                """));
        Assert.Equal(
            "false|false",
            await ScalarAsync<string>(connectionString,
                "SELECT rolbypassrls::text || '|' || (SELECT rolbypassrls::text FROM pg_roles WHERE rolname='paqueteria_worker') FROM pg_roles WHERE rolname='paqueteria_app'"));
    }

    [PostgreSqlContractFact]
    public async Task Up_down_and_up_again_adopt_create_refuse_drift_and_roll_back_only_without_data()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("drvdoc001lane");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            // A fresh AI-06 baseline already carries the columns and checks (the deployer's assertions passed).
            await AssertCanonicalShapeAsync(connectionString);

            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            await AssertCanonicalShapeAsync(connectionString);
            await AssertBaselineAsync(connectionString);

            // Running the Up SQL again changes nothing.
            await ExecuteAsMigratorAsync(connectionString, AddInPersonDriverDocuments.RenderUpSql());
            await AssertCanonicalShapeAsync(connectionString);

            // Drift is refused: a missing or different check, a missing column, a retyped column, a missing reference.
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE drivers.driver_documents DROP CONSTRAINT driver_documents_in_person_check;",
                "drivers.driver_documents in-person checks differ from the DRV-DOC-001 contract",
                "ALTER TABLE drivers.driver_documents ADD CONSTRAINT driver_documents_in_person_check CHECK (" + InPersonCheckBody() + ");");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE drivers.driver_documents DROP CONSTRAINT driver_documents_review_check; ALTER TABLE drivers.driver_documents ADD CONSTRAINT driver_documents_review_check CHECK (true);",
                "drivers.driver_documents in-person checks differ from the DRV-DOC-001 contract",
                "ALTER TABLE drivers.driver_documents DROP CONSTRAINT driver_documents_review_check; ALTER TABLE drivers.driver_documents ADD CONSTRAINT driver_documents_review_check CHECK ((reviewed_by IS NULL) = (reviewed_at IS NULL) AND (revoked_by IS NULL) = (revoked_at IS NULL) AND (revoked_at IS NULL OR status='REVOKED'));");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE drivers.driver_documents DROP COLUMN revoked_at CASCADE;",
                "drivers.driver_documents in-person columns are partially present",
                "ALTER TABLE drivers.driver_documents ADD COLUMN revoked_at timestamptz; ALTER TABLE drivers.driver_documents DROP CONSTRAINT IF EXISTS driver_documents_review_check, DROP CONSTRAINT IF EXISTS driver_documents_in_person_check; ALTER TABLE drivers.driver_documents ADD CONSTRAINT driver_documents_review_check CHECK ((reviewed_by IS NULL) = (reviewed_at IS NULL) AND (revoked_by IS NULL) = (revoked_at IS NULL) AND (revoked_at IS NULL OR status='REVOKED')), ADD CONSTRAINT driver_documents_in_person_check CHECK (" + InPersonCheckBody() + ");");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE drivers.driver_documents ALTER COLUMN object_key SET NOT NULL;",
                "drivers.driver_documents in-person columns differ from the DRV-DOC-001 contract",
                "ALTER TABLE drivers.driver_documents ALTER COLUMN object_key DROP NOT NULL;");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE drivers.driver_documents DROP CONSTRAINT driver_documents_revoked_by_fkey;",
                "drivers.driver_documents reviewer references differ from the DRV-DOC-001 contract",
                "ALTER TABLE drivers.driver_documents ADD CONSTRAINT driver_documents_revoked_by_fkey FOREIGN KEY (revoked_by) REFERENCES identity.users(id);");
            await AssertCanonicalShapeAsync(connectionString);

            // A FILE row (the earlier shape) and an in-person row; the in-person data blocks the rollback.
            var seed = await SeedLaneRowsAsync(connectionString);
            await AssertRollbackBlockedAsync(connectionString);
            // A FILE row that was revoked by dispatch also blocks it (its revocation would be lost).
            await ExecuteAsync(connectionString,
                "DELETE FROM drivers.driver_documents WHERE evidence_kind='IN_PERSON'; UPDATE drivers.driver_documents SET status='REVOKED',revoked_by=@user,revoked_at=clock_timestamp() WHERE id=@file;",
                new("user", seed.UserId), new("file", seed.FileDocumentId));
            await AssertRollbackBlockedAsync(connectionString);
            await ExecuteAsync(connectionString,
                "UPDATE drivers.driver_documents SET status='VALID',revoked_by=NULL,revoked_at=NULL WHERE id=@file;",
                new NpgsqlParameter("file", seed.FileDocumentId));

            // Without in-person data it drops exactly the seven columns and the five checks; the FILE row stays.
            await RollBackToVoiceBridgeAsync(connectionString);
            Assert.Equal(FullHistory[..3], await HistoryAsync(connectionString));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
            Assert.Equal(
                DatabaseBaselineAssertions.PreInPersonDriverDocumentColumns,
                await ColumnsAsync(connectionString));
            Assert.Equal(0L, await CheckCountAsync(connectionString));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM drivers.driver_documents WHERE id='{seed.FileDocumentId}'"));
            // An installation whose Drivers lane has not recorded the step passes with the earlier shape.
            await AssertBaselineAsync(connectionString);

            // Up again on what is now an upgraded installation: the canonical DDL runs and the FILE row reads as FILE.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            await AssertCanonicalShapeAsync(connectionString);
            await AssertBaselineAsync(connectionString);
            Assert.Equal(
                "FILE",
                await ScalarAsync<string>(connectionString, $"SELECT evidence_kind FROM drivers.driver_documents WHERE id='{seed.FileDocumentId}'"));

            // The recorded step requires the exact shape: the baseline assertions refuse drift.
            await ExecuteAsync(connectionString, "ALTER TABLE drivers.driver_documents DROP CONSTRAINT driver_documents_expiry_basis_check;");
            var drift = await Assert.ThrowsAsync<DatabaseAssertionException>(() => AssertBaselineAsync(connectionString));
            Assert.Contains(drift.Violations, violation => violation.Contains("in-person", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task The_checks_keep_each_evidence_kind_in_its_shape()
    {
        var scenario = await SeedAsync();
        var refused = new[]
        {
            // IN_PERSON with a key or a hash, FILE without them, an unknown kind.
            "'IN_PERSON','IDENTITY','VALID','synthetic/key',NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','VALID',NULL,decode(repeat('aa',32),'hex'),@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'FILE','IDENTITY','VALID',NULL,NULL,@expires,NULL,NULL,NULL,NULL,NULL,NULL",
            "'PHOTO','IDENTITY','VALID',NULL,NULL,@expires,NULL,NULL,NULL,NULL,NULL,NULL",
            // IN_PERSON without a review, of a type that is not recordable, or PENDING.
            "'IN_PERSON','IDENTITY','VALID',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,NULL,NULL,NULL,NULL",
            "'IN_PERSON','BACKGROUND_CHECK','VALID',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','PENDING',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),NULL,NULL",
            // VALID without an expiry or a basis, REJECTED without a reason or with an expiry.
            "'IN_PERSON','IDENTITY','VALID',NULL,NULL,NULL,NULL,NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','REJECTED',NULL,NULL,NULL,NULL,NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','REJECTED',NULL,NULL,@expires,'PRINTED_EXPIRY','EXPIRED',@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','REJECTED',NULL,NULL,NULL,NULL,'BLURRY',@reviewer,clock_timestamp(),NULL,NULL",
            // A permanent-license review on another type; a reason on a VALID row; a revocation on a VALID row.
            "'IN_PERSON','IDENTITY','VALID',NULL,NULL,@expires,'PERMANENT_LICENSE_REVIEW',NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','VALID',NULL,NULL,@expires,'PRINTED_EXPIRY','ILLEGIBLE',@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','VALID',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),@reviewer,clock_timestamp()",
            "'IN_PERSON','IDENTITY','REVOKED',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),NULL,NULL",
            "'IN_PERSON','IDENTITY','REVOKED',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),NULL,clock_timestamp()",
        };
        foreach (var values in refused)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(scenario, values));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        foreach (var values in new[]
                 {
                     "'IN_PERSON','DRIVER_LICENSE','VALID',NULL,NULL,@expires,'PERMANENT_LICENSE_REVIEW',NULL,@reviewer,clock_timestamp(),NULL,NULL",
                     "'IN_PERSON','IDENTITY','REJECTED',NULL,NULL,NULL,NULL,'HOLDER_MISMATCH',@reviewer,clock_timestamp(),NULL,NULL",
                     "'IN_PERSON','IDENTITY','REVOKED',NULL,NULL,@expires,'PRINTED_EXPIRY',NULL,@reviewer,clock_timestamp(),@reviewer,clock_timestamp()",
                     "'FILE','BACKGROUND_CHECK','PENDING','synthetic/file',decode(repeat('aa',32),'hex'),NULL,NULL,NULL,NULL,NULL,NULL,NULL",
                 })
        {
            await InsertRawAsync(scenario, values);
        }
    }

    // ------------------------------------------------------------------ RLS and isolation

    [PostgreSqlContractFact]
    public async Task Documents_are_isolated_by_forced_RLS_across_organizations()
    {
        var scenario = await SeedAsync();
        var services = CreateServices();
        var recorded = await services.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, "IDENTITY"), default);

        Assert.Equal(
            "true|true",
            await ScalarAsync<string>(fixture.DeploymentConnectionString,
                "SELECT relrowsecurity::text || '|' || relforcerowsecurity::text FROM pg_class WHERE oid='drivers.driver_documents'::regclass"));

        // No tenant context reads nothing; another organization reads, updates and deletes nothing.
        await using (var empty = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", scenario.DispatcherId, []))
        {
            Assert.Equal(0L, await CountAsync(empty, "SELECT count(*) FROM drivers.driver_documents WHERE id=@id", recorded.Id));
            await empty.RollbackAsync();
        }

        await using (var foreign = await TenantTransaction.BeginAsync(
                         fixture.AppDataSource, "paqueteria_app", scenario.ForeignDispatcherId, [scenario.ForeignOrganizationId]))
        {
            Assert.Equal(0L, await CountAsync(foreign, "SELECT count(*) FROM drivers.driver_documents WHERE id=@id", recorded.Id));
            Assert.Equal(0L, await CountAsync(foreign, "WITH u AS (UPDATE drivers.driver_documents SET status='REVOKED' WHERE id=@id RETURNING 1) SELECT count(*) FROM u", recorded.Id));
            Assert.Equal(0L, await CountAsync(foreign, "WITH d AS (DELETE FROM drivers.driver_documents WHERE id=@id RETURNING 1) SELECT count(*) FROM d", recorded.Id));
            // Writing a row for another organization is refused by the policy's WITH CHECK.
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO drivers.driver_documents(id,driver_id,org_id,document_type,expires_at,status,created_at,
                  evidence_kind,expiry_basis,reviewed_by,reviewed_at)
                VALUES (gen_random_uuid(),@driver,@org,'IDENTITY',clock_timestamp()+interval '30 days','VALID',clock_timestamp(),
                  'IN_PERSON','PRINTED_EXPIRY',@reviewer,clock_timestamp())
                """,
                foreign.Connection,
                foreign.Transaction);
            insert.Parameters.AddWithValue("driver", scenario.MotorcycleDriverId);
            insert.Parameters.AddWithValue("org", scenario.OrganizationId);
            insert.Parameters.AddWithValue("reviewer", scenario.ForeignDispatcherId);
            var violation = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, violation.SqlState);
            await foreign.RollbackAsync();
        }

        // Through the service: another organization's driver or document is the uniform not found.
        var foreignDispatch = CreateServices();
        await Assert.ThrowsAsync<DriverDocumentNotFoundException>(() => foreignDispatch.Documents.RecordAsync(
            Record(scenario, scenario.MotorcycleDriverId, "IDENTITY") with
            {
                ActorId = scenario.ForeignDispatcherId,
                OrganizationId = scenario.ForeignOrganizationId,
                IdempotencyKey = Key(),
            },
            default));
        await Assert.ThrowsAsync<DriverDocumentNotFoundException>(() => foreignDispatch.Documents.RevokeAsync(
            new RevokeDriverDocumentCommand(
                scenario.ForeignDispatcherId, scenario.ForeignOrganizationId, scenario.MotorcycleDriverId, recorded.Id, "req"),
            default));
        var foreignPage = await foreignDispatch.Documents.ListAsync(
            new ListDriverDocumentsQuery(scenario.ForeignDispatcherId, scenario.ForeignOrganizationId, null), default);
        Assert.DoesNotContain(foreignPage.Items, item => item.DriverId == scenario.MotorcycleDriverId);
        Assert.Contains(foreignPage.Items, item => item.DriverId == scenario.ForeignDriverId);
    }

    // ------------------------------------------------------------------ record, list and revoke

    [PostgreSqlContractFact]
    public async Task A_record_writes_the_in_person_row_its_audit_and_its_answer_in_one_transaction()
    {
        var scenario = await SeedAsync();
        var services = CreateServices();
        var command = Record(scenario, scenario.MotorcycleDriverId, "DRIVER_LICENSE", printed: new DateOnly(2027, 3, 1));
        var recorded = await services.Documents.RecordAsync(command, default);

        Assert.Equal("DRIVER_LICENSE", recorded.DocumentType);
        Assert.Equal("VALID", recorded.Status);
        Assert.Equal(new DateOnly(2027, 3, 1), recorded.ValidThrough);
        // Y12: valid through the whole printed day in Culiacán (UTC-7), so it expires at 07:00 UTC of the next day.
        Assert.Equal(new DateTimeOffset(2027, 3, 2, 7, 0, 0, TimeSpan.Zero), recorded.ExpiresAt);
        Assert.Equal("PRINTED_EXPIRY", recorded.ExpiryBasis);
        Assert.Null(recorded.RejectionCode);
        Assert.Null(recorded.RevokedAt);

        var row = await RowAsync(recorded.Id);
        Assert.Equal("IN_PERSON|null|null|" + scenario.DispatcherId.ToString("D") + "|true", row);

        // The document, its audit row and the stored answer were written by the same transaction (same xmin).
        Assert.Equal(
            "1|1|true|true",
            await ScalarAsync<string>(fixture.DeploymentConnectionString,
                $"""
                SELECT (SELECT count(*) FROM platform.audit_logs WHERE entity_id='{recorded.Id}')::text || '|' ||
                       (SELECT count(*) FROM platform.idempotency_keys WHERE resource_id='{recorded.Id}')::text || '|' ||
                       (SELECT (d.xmin::text = a.xmin::text)::text FROM drivers.driver_documents d, platform.audit_logs a
                        WHERE d.id='{recorded.Id}' AND a.entity_id=d.id) || '|' ||
                       (SELECT (d.xmin::text = k.xmin::text)::text FROM drivers.driver_documents d, platform.idempotency_keys k
                        WHERE d.id='{recorded.Id}' AND k.resource_id=d.id)
                """));
        var audit = await ScalarAsync<string>(fixture.DeploymentConnectionString,
            $"SELECT action || '|' || entity_type || '|' || actor_id || '|' || request_id || '|' || payload_redacted::text FROM platform.audit_logs WHERE entity_id='{recorded.Id}'");
        var parts = audit.Split('|', 5);
        Assert.Equal(["DRIVER_DOCUMENT_RECORDED", "DriverDocument", scenario.DispatcherId.ToString("D"), "req-record"], parts[..4]);
        using (var payload = JsonDocument.Parse(parts[4]))
        {
            Assert.Equal(
                ["document_id", "document_type", "driver_id", "evidence_kind", "expiry_basis", "rejection_code", "status"],
                payload.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal("IN_PERSON", payload.RootElement.GetProperty("evidence_kind").GetString());
            Assert.Equal("PRINTED_EXPIRY", payload.RootElement.GetProperty("expiry_basis").GetString());
        }

        // A replay of the same key and body answers the stored document and writes nothing.
        var replay = await services.Documents.RecordAsync(command, default);
        Assert.Equal(recorded, replay);
        Assert.Equal(1L, await DocumentCountAsync(scenario.MotorcycleDriverId));

        // The key reused with another body, or by another person, is IDEMPOTENCY_CONFLICT.
        foreach (var other in new[]
                 {
                     command with { DocumentType = "IDENTITY" },
                     command with { ActorId = scenario.SecondDispatcherId },
                 })
        {
            var conflict = await Assert.ThrowsAsync<DriverDocumentConflictException>(
                () => services.Documents.RecordAsync(other, default));
            Assert.Equal(DriverDocumentConflictCodes.IdempotencyConflict, conflict.Code);
        }

        // A permanent Sinaloa license is reviewed twelve months after the review day (Y5).
        var permanent = await services.Documents.RecordAsync(
            Record(scenario, scenario.MotorcycleDriverId, "DRIVER_LICENSE") with
            {
                ExpiryBasis = DriverDocumentVocabulary.PermanentLicenseReview,
                PrintedDate = null,
                IdempotencyKey = Key(),
            },
            default);
        Assert.Equal(Today.AddMonths(12), permanent.ValidThrough);
        Assert.Equal("PERMANENT_LICENSE_REVIEW", permanent.ExpiryBasis);

        // A rejection carries its fixed reason and no expiry (Y7).
        var rejected = await services.Documents.RecordAsync(Rejected(scenario, scenario.MotorcycleDriverId, "VEHICLE_CARD"), default);
        Assert.Equal(("REJECTED", "WRONG_DOCUMENT", (DateTimeOffset?)null, (DateOnly?)null, (string?)null),
            (rejected.Status, rejected.RejectionCode, rejected.ExpiresAt, rejected.ValidThrough, rejected.ExpiryBasis));
    }

    [PostgreSqlContractFact]
    public async Task A_record_refuses_foreign_retired_and_external_drivers_types_the_vehicle_does_not_need_and_out_of_range_dates()
    {
        var scenario = await SeedAsync();
        var services = CreateServices();
        foreach (var driver in new[] { scenario.InactiveDriverId, scenario.ExternalDriverId, scenario.ForeignDriverId, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<DriverDocumentNotFoundException>(
                () => services.Documents.RecordAsync(Record(scenario, driver, "IDENTITY"), default));
        }

        // Y11: a BICYCLE needs only IDENTITY.
        var notRequired = await Assert.ThrowsAsync<DriverDocumentConflictException>(
            () => services.Documents.RecordAsync(Record(scenario, scenario.BicycleDriverId, "DRIVER_LICENSE"), default));
        Assert.Equal(DriverDocumentConflictCodes.DocumentTypeNotRequired, notRequired.Code);
        // A PENDING profile (onboarding) can receive its documents.
        Assert.Equal("VALID", (await services.Documents.RecordAsync(Record(scenario, scenario.BicycleDriverId, "IDENTITY"), default)).Status);

        foreach (var printed in new[] { Today.AddDays(-1), Today.AddYears(10).AddDays(1) })
        {
            var outOfRange = await Assert.ThrowsAsync<DriverDocumentConflictException>(() => services.Documents.RecordAsync(
                Record(scenario, scenario.MotorcycleDriverId, "INSURANCE", printed: printed), default));
            Assert.Equal(DriverDocumentConflictCodes.ExpiryDateOutOfRange, outOfRange.Code);
        }

        // Today and ten years ahead are accepted; nothing was written by the refusals.
        Assert.Equal(Today, (await services.Documents.RecordAsync(
            Record(scenario, scenario.MotorcycleDriverId, "INSURANCE", printed: Today), default)).ValidThrough);
        Assert.Equal(1L, await DocumentCountAsync(scenario.MotorcycleDriverId));

        var disabled = CreateServices(enabled: false);
        await Assert.ThrowsAsync<DriverDocumentUnavailableException>(
            () => disabled.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, "IDENTITY"), default));
        await Assert.ThrowsAsync<DriverDocumentUnavailableException>(() => disabled.Documents.RevokeAsync(
            new RevokeDriverDocumentCommand(scenario.DispatcherId, scenario.OrganizationId, scenario.MotorcycleDriverId, Guid.NewGuid(), "r"),
            default));
        var off = await disabled.Documents.ListAsync(new ListDriverDocumentsQuery(scenario.DispatcherId, scenario.OrganizationId, null), default);
        Assert.False(off.DocumentsEnabled);
        Assert.Empty(off.Items);
        Assert.Null(off.NextAfterDriverId);
        var mineOff = await disabled.Documents.ListMineAsync(scenario.MotorcycleUserId, scenario.OrganizationId, default);
        Assert.False(mineOff.DocumentsEnabled);
        Assert.Empty(mineOff.Documents);
    }

    [PostgreSqlContractFact]
    public async Task Only_the_latest_valid_document_is_revocable_and_a_repeated_revoke_changes_nothing()
    {
        var scenario = await SeedAsync();
        var services = CreateServices();
        var first = await services.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, "IDENTITY"), default);
        var second = await services.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, "IDENTITY"), default);
        var rejected = await services.Documents.RecordAsync(Rejected(scenario, scenario.MotorcycleDriverId, "VEHICLE_CARD"), default);

        foreach (var notRevocable in new[] { first.Id, rejected.Id })
        {
            var conflict = await Assert.ThrowsAsync<DriverDocumentConflictException>(
                () => services.Documents.RevokeAsync(Revoke(scenario, notRevocable), default));
            Assert.Equal(DriverDocumentConflictCodes.DocumentNotRevocable, conflict.Code);
        }

        var revoked = await services.Documents.RevokeAsync(Revoke(scenario, second.Id), default);
        Assert.Equal("REVOKED", revoked.Status);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal(second.ExpiresAt, revoked.ExpiresAt);
        Assert.Equal(
            "REVOKED|" + scenario.DispatcherId.ToString("D"),
            await ScalarAsync<string>(fixture.DeploymentConnectionString,
                $"SELECT status || '|' || revoked_by FROM drivers.driver_documents WHERE id='{second.Id}'"));

        var again = await services.Documents.RevokeAsync(Revoke(scenario, second.Id), default);
        Assert.Equal(revoked, again);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(fixture.DeploymentConnectionString,
                $"SELECT count(*) FROM platform.audit_logs WHERE entity_id='{second.Id}' AND action='DRIVER_DOCUMENT_REVOKED'"));
        Assert.Equal(
            "{\"status\": \"REVOKED\", \"driver_id\": \"" + scenario.MotorcycleDriverId.ToString("D") + "\", \"document_id\": \"" +
                second.Id.ToString("D") + "\", \"document_type\": \"IDENTITY\"}",
            await ScalarAsync<string>(fixture.DeploymentConnectionString,
                $"SELECT payload_redacted::text FROM platform.audit_logs WHERE entity_id='{second.Id}' AND action='DRIVER_DOCUMENT_REVOKED'"));

        // Unknown documents, documents of another driver and types outside the recordable four are the uniform 404.
        var otherDriversDocument = await services.Documents.RecordAsync(Record(scenario, scenario.BicycleDriverId, "IDENTITY"), default);
        var backgroundCheck = await InsertRawAsync(
            scenario,
            "'FILE','BACKGROUND_CHECK','VALID','synthetic/file',decode(repeat('aa',32),'hex'),@expires,NULL,NULL,NULL,NULL,NULL,NULL");
        foreach (var document in new[] { Guid.NewGuid(), otherDriversDocument.Id, backgroundCheck })
        {
            await Assert.ThrowsAsync<DriverDocumentNotFoundException>(
                () => services.Documents.RevokeAsync(Revoke(scenario, document), default));
        }
    }

    [PostgreSqlContractFact]
    public async Task The_lists_show_the_latest_document_of_each_required_type_and_page_like_the_roster()
    {
        var scenario = await SeedAsync();
        var services = CreateServices();
        var identity = await services.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, "IDENTITY"), default);
        var license = await services.Documents.RecordAsync(Rejected(scenario, scenario.MotorcycleDriverId, "DRIVER_LICENSE"), default);
        // A FILE row of a type the vehicle does not require, and an older FILE IDENTITY row, are not listed.
        await InsertRawAsync(scenario,
            "'FILE','BACKGROUND_CHECK','VALID','synthetic/file',decode(repeat('aa',32),'hex'),@expires,NULL,NULL,NULL,NULL,NULL,NULL",
            createdAt: "clock_timestamp() - interval '1 day'");

        var page = await services.Documents.ListAsync(
            new ListDriverDocumentsQuery(scenario.DispatcherId, scenario.OrganizationId, null), default);
        Assert.True(page.DocumentsEnabled);
        Assert.Null(page.NextAfterDriverId);
        Assert.Equal(
            new[] { scenario.MotorcycleDriverId, scenario.BicycleDriverId }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal),
            page.Items.Select(item => item.DriverId));
        var motorcycle = page.Items.Single(item => item.DriverId == scenario.MotorcycleDriverId);
        Assert.Equal(["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE"], motorcycle.Documents.Select(slot => slot.DocumentType));
        Assert.Equal(identity, motorcycle.Documents[0].Document);
        Assert.Equal(license, motorcycle.Documents[1].Document);
        Assert.Null(motorcycle.Documents[2].Document);
        var bicycle = page.Items.Single(item => item.DriverId == scenario.BicycleDriverId);
        Assert.Equal(["IDENTITY"], bicycle.Documents.Select(slot => slot.DocumentType));

        // The cursor continues after the last listed driver.
        var after = page.Items[0].DriverId;
        var next = await services.Documents.ListAsync(
            new ListDriverDocumentsQuery(scenario.DispatcherId, scenario.OrganizationId, after), default);
        Assert.Equal([page.Items[1].DriverId], next.Items.Select(item => item.DriverId));

        // The driver reads its own documents only, whatever its profile status; without a profile, 403.
        var mine = await services.Documents.ListMineAsync(scenario.MotorcycleUserId, scenario.OrganizationId, default);
        Assert.True(mine.DocumentsEnabled);
        Assert.Equal(motorcycle.Documents, mine.Documents);
        var pending = await services.Documents.ListMineAsync(scenario.BicycleUserId, scenario.OrganizationId, default);
        Assert.Equal(["IDENTITY"], pending.Documents.Select(slot => slot.DocumentType));
        Assert.Null(pending.Documents[0].Document);
        await Assert.ThrowsAsync<DriverDocumentForbiddenException>(
            () => services.Documents.ListMineAsync(scenario.DispatcherId, scenario.OrganizationId, default));
        await Assert.ThrowsAsync<DriverDocumentForbiddenException>(
            () => services.Documents.ListMineAsync(scenario.MotorcycleUserId, scenario.ForeignOrganizationId, default));
    }

    // ------------------------------------------------------------------ DSP-001 end to end

    [PostgreSqlContractFact]
    public async Task DSP001_admits_valid_unexpired_in_person_documents_and_keeps_every_rejection()
    {
        var scenario = await SeedAsync();
        var services = CreateServices();
        var command = new EvaluateOwnDriverEligibilityCommand(
            scenario.DispatcherId,
            scenario.OrganizationId,
            scenario.MotorcycleDriverId,
            scenario.CityId,
            null,
            new DriverCapacityRequirement(1, 500, 500, [new PackageSides(100, 100, 100)]),
            Now);

        Assert.Equal(["REQUIRED_DOCUMENT_MISSING"], await CodesAsync(services, command));
        foreach (var type in DriverDocumentVocabulary.RecordableTypes)
        {
            await services.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, type), default);
        }

        Assert.Empty(await CodesAsync(services, command));

        // A newer REJECTED record keeps the driver out (the latest row wins) until a new VALID record; it has no expiry,
        // so DSP-001 also reports the missing expiry, as it does for any document without one.
        await services.Documents.RecordAsync(Rejected(scenario, scenario.MotorcycleDriverId, "INSURANCE"), default);
        Assert.Equal(["DOCUMENT_STATUS_NOT_VALID", "DOCUMENT_EXPIRY_MISSING"], await CodesAsync(services, command));
        var insurance = await services.Documents.RecordAsync(Record(scenario, scenario.MotorcycleDriverId, "INSURANCE"), default);
        Assert.Empty(await CodesAsync(services, command));

        // Revoked (Y10): no new orders.
        await services.Documents.RevokeAsync(Revoke(scenario, insurance.Id), default);
        Assert.Equal(["DOCUMENT_STATUS_NOT_VALID"], await CodesAsync(services, command));
        var renewed = await services.Documents.RecordAsync(
            Record(scenario, scenario.MotorcycleDriverId, "INSURANCE", printed: Today), default);
        Assert.Empty(await CodesAsync(services, command));

        // Y12: valid through the whole printed day in Culiacán; at 00:00 of the next local day it is expired.
        var midnight = renewed.ExpiresAt!.Value;
        Assert.Empty(await CodesAsync(services, command with { EvaluatedAt = midnight.AddTicks(-10) }));
        Assert.Equal(["DOCUMENT_EXPIRED"], await CodesAsync(services, command with { EvaluatedAt = midnight }));

        // A PENDING document of another path keeps its rejection too.
        await InsertRawAsync(scenario,
            "'FILE','INSURANCE','PENDING','synthetic/file',decode(repeat('aa',32),'hex'),@expires,NULL,NULL,NULL,NULL,NULL,NULL",
            createdAt: "clock_timestamp() + interval '1 second'");
        Assert.Equal(["DOCUMENT_STATUS_NOT_VALID"], await CodesAsync(services, command));
    }

    // ------------------------------------------------------------------ helpers

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    /// <summary>Today in Culiacán at <see cref="Now"/> (11:00 local).</summary>
    private static readonly DateOnly Today = new(2026, 11, 2);

    private static async Task<string[]> CodesAsync(Services services, EvaluateOwnDriverEligibilityCommand command) =>
        (await services.Eligibility.EvaluateAsync(command, default)).Rejections.Select(rejection => rejection.Code).ToArray();

    private static string Key() => "drv-doc-001-" + Guid.NewGuid().ToString("N");

    private static RecordDriverDocumentCommand Record(Scenario scenario, Guid driverId, string type, DateOnly? printed = null) => new(
        scenario.DispatcherId,
        scenario.OrganizationId,
        driverId,
        type,
        DriverDocumentVocabulary.Valid,
        DriverDocumentVocabulary.PrintedExpiry,
        printed ?? Today.AddMonths(6),
        null,
        Key(),
        "req-record");

    private static RecordDriverDocumentCommand Rejected(Scenario scenario, Guid driverId, string type) => new(
        scenario.DispatcherId,
        scenario.OrganizationId,
        driverId,
        type,
        DriverDocumentVocabulary.Rejected,
        null,
        null,
        "WRONG_DOCUMENT",
        Key(),
        "req-reject");

    private static RevokeDriverDocumentCommand Revoke(Scenario scenario, Guid documentId) =>
        new(scenario.DispatcherId, scenario.OrganizationId, scenario.MotorcycleDriverId, documentId, "req-revoke");

    private Services CreateServices(bool enabled = true)
    {
        var dataSource = fixture.CreateAppDataSource(maxPoolSize: 2, applicationName: "Paqueteria.DRVDOC001.Contract");
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(dataSource, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.EnableRetryOnFailure();
            })
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DriversDbContext(dbOptions, state);
        var transactions = new TenantTransactionContext<DriversDbContext>(context, state);
        var options = Options.Create(new DriversOptions
        {
            Provider = DriversProviderKind.PostgreSql,
            Documents = new DriverDocumentsOptions { Enabled = enabled },
            Eligibility = new DriverEligibilityOptions
            {
                RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal)
                {
                    ["MOTORCYCLE"] = ["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE"],
                    ["BICYCLE"] = ["IDENTITY"],
                },
                VehicleCapacity = new(StringComparer.Ordinal)
                {
                    ["MOTORCYCLE"] = new VehicleCapacityOptions
                    {
                        MaximumPackageCount = 5,
                        MaximumTotalWeightGrams = 20_000,
                        MaximumSinglePackageWeightGrams = 10_000,
                        MaximumLengthMillimeters = 400,
                        MaximumWidthMillimeters = 400,
                        MaximumHeightMillimeters = 350,
                    },
                },
            },
        });
        var documents = new PostgreSqlDriverDocumentService(
            transactions,
            options,
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new FixedClock(Now),
            NullLogger<PostgreSqlDriverDocumentService>.Instance);
        var eligibilityState = new TenantDatabaseExecutionState();
        var eligibilityContext = new DriversDbContext(
            new DbContextOptionsBuilder<DriversDbContext>()
                .UseNpgsql(dataSource, postgres =>
                {
                    postgres.UseNetTopologySuite();
                    postgres.EnableRetryOnFailure();
                })
                .AddInterceptors(
                    new TenantTransactionGuardInterceptor(eligibilityState),
                    new TenantSaveChangesGuardInterceptor(eligibilityState))
                .Options,
            eligibilityState);
        var eligibility = new PostgreSqlDriverEligibilityService(
            new TenantTransactionContext<DriversDbContext>(eligibilityContext, eligibilityState),
            options);
        return new Services(documents, eligibility);
    }

    private sealed record Services(PostgreSqlDriverDocumentService Documents, PostgreSqlDriverEligibilityService Eligibility);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record Scenario(
        Guid OrganizationId,
        Guid ForeignOrganizationId,
        Guid CityId,
        Guid DispatcherId,
        Guid SecondDispatcherId,
        Guid ForeignDispatcherId,
        Guid MotorcycleUserId,
        Guid MotorcycleDriverId,
        Guid BicycleUserId,
        Guid BicycleDriverId,
        Guid InactiveDriverId,
        Guid ExternalDriverId,
        Guid ForeignDriverId);

    /// <summary>
    /// Two synthetic organizations (left in place, like the other synthetic scenarios: audit rows are append-only): a
    /// MOTORCYCLE OWN ACTIVE driver, a BICYCLE OWN PENDING driver, a retired and an EXTERNAL driver, two dispatchers,
    /// and in the other organization an OWN driver and a dispatcher.
    /// </summary>
    private async Task<Scenario> SeedAsync()
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var inactiveUser = Guid.NewGuid();
        var externalUser = Guid.NewGuid();
        var foreignUser = Guid.NewGuid();
        await ExecuteAsync(fixture.DeploymentConnectionString,
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@org,'DRV-DOC-001 synthetic','DRV-DOC-001 synthetic','BUSINESS'),
                   (@foreign_org,'DRV-DOC-001 foreign','DRV-DOC-001 foreign','BUSINESS');
            INSERT INTO locations.cities(id,state_code,name,timezone) VALUES (@city,'SI',@city_name,'America/Mazatlan');
            INSERT INTO identity.users(id,identity_subject,status)
            SELECT u, 'drvdoc001|' || replace(u::text,'-',''), 'ACTIVE'
            FROM unnest(ARRAY[@dispatcher,@second,@foreign_dispatcher,@moto_user,@bike_user,@inactive_user,@external_user,@foreign_user]::uuid[]) u;
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (gen_random_uuid(),@dispatcher,@org,'DISPATCHER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@second,@org,'DISPATCHER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@foreign_dispatcher,@foreign_org,'DISPATCHER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@moto_user,@org,'DRIVER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@bike_user,@org,'DRIVER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@inactive_user,@org,'DRIVER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@external_user,@org,'DRIVER','ACTIVE',true,clock_timestamp()),
                   (gen_random_uuid(),@foreign_user,@foreign_org,'DRIVER','ACTIVE',true,clock_timestamp());
            INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@moto,@moto_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE'),
                   (@bike,@bike_user,@org,@city,'OWN','BICYCLE','PENDING'),
                   (@inactive,@inactive_user,@org,@city,'OWN','MOTORCYCLE','INACTIVE'),
                   (@external,@external_user,@org,@city,'EXTERNAL','MOTORCYCLE','ACTIVE'),
                   (@foreign_driver,@foreign_user,@foreign_org,@city,'OWN','MOTORCYCLE','ACTIVE');
            """,
            new("org", scenario.OrganizationId), new("foreign_org", scenario.ForeignOrganizationId),
            new("city", scenario.CityId), new("city_name", $"DRV-DOC-001 {scenario.CityId:N}"),
            new("dispatcher", scenario.DispatcherId), new("second", scenario.SecondDispatcherId),
            new("foreign_dispatcher", scenario.ForeignDispatcherId), new("moto_user", scenario.MotorcycleUserId),
            new("bike_user", scenario.BicycleUserId), new("inactive_user", inactiveUser),
            new("external_user", externalUser), new("foreign_user", foreignUser),
            new("moto", scenario.MotorcycleDriverId), new("bike", scenario.BicycleDriverId),
            new("inactive", scenario.InactiveDriverId), new("external", scenario.ExternalDriverId),
            new("foreign_driver", scenario.ForeignDriverId));
        return scenario;
    }

    /// <summary>A row written directly (as the superuser) with the given evidence kind, type, status and columns.</summary>
    private async Task<Guid> InsertRawAsync(Scenario scenario, string values, string createdAt = "clock_timestamp()")
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(fixture.DeploymentConnectionString,
            $"""
            INSERT INTO drivers.driver_documents(
              id,driver_id,org_id,created_at,evidence_kind,document_type,status,object_key,sha256,expires_at,expiry_basis,
              rejection_code,reviewed_by,reviewed_at,revoked_by,revoked_at)
            VALUES (@id,@driver,@org,{createdAt},{values})
            """,
            new("id", id), new("driver", scenario.MotorcycleDriverId), new("org", scenario.OrganizationId),
            new("expires", Now.AddDays(30)), new("reviewer", scenario.DispatcherId));
        return id;
    }

    private Task<string> RowAsync(Guid id) => ScalarAsync<string>(fixture.DeploymentConnectionString,
        $"""
        SELECT evidence_kind || '|' || COALESCE(object_key,'null') || '|' || COALESCE(encode(sha256,'hex'),'null') || '|' ||
               reviewed_by || '|' || (reviewed_at = created_at)::text
        FROM drivers.driver_documents WHERE id='{id}'
        """);

    private Task<long> DocumentCountAsync(Guid driverId) => ScalarAsync<long>(fixture.DeploymentConnectionString,
        $"SELECT count(*) FROM drivers.driver_documents WHERE driver_id='{driverId}'");

    private static async Task<long> CountAsync(TenantTransaction transaction, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction.Transaction);
        command.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string InPersonCheckBody() =>
        "evidence_kind<>'IN_PERSON' OR (reviewed_at IS NOT NULL AND document_type IN ('IDENTITY','DRIVER_LICENSE','VEHICLE_CARD','INSURANCE') " +
        "AND status IN ('VALID','REJECTED','REVOKED') AND (status='REJECTED') = (rejection_code IS NOT NULL) " +
        "AND (status='REJECTED') = (expires_at IS NULL) AND (status='REJECTED') = (expiry_basis IS NULL) " +
        "AND (status='REVOKED') = (revoked_at IS NOT NULL))";

    private async Task<LaneSeed> SeedLaneRowsAsync(string connectionString)
    {
        var org = Guid.NewGuid();
        var user = Guid.NewGuid();
        var city = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var file = Guid.NewGuid();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@org,'DRV-DOC-001 lane','DRV-DOC-001 lane','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status) VALUES (@user,@subject,'ACTIVE');
            INSERT INTO locations.cities(id,state_code,name,timezone) VALUES (@city,'SI',@city_name,'America/Mazatlan');
            INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO drivers.driver_documents(id,driver_id,org_id,document_type,object_key,sha256,expires_at,status)
            VALUES (@file,@driver,@org,'IDENTITY','synthetic/lane',decode(repeat('ab',32),'hex'),clock_timestamp()+interval '30 days','VALID');
            INSERT INTO drivers.driver_documents(id,driver_id,org_id,document_type,expires_at,status,evidence_kind,expiry_basis,
              reviewed_by,reviewed_at)
            VALUES (gen_random_uuid(),@driver,@org,'DRIVER_LICENSE',clock_timestamp()+interval '30 days','VALID','IN_PERSON',
              'PRINTED_EXPIRY',@user,clock_timestamp());
            """,
            new("org", org), new("user", user), new("subject", $"drvdoc001-lane|{user:N}"), new("city", city),
            new("city_name", $"DRV-DOC-001 lane {city:N}"), new("driver", driver), new("file", file));
        return new LaneSeed(user, file);
    }

    private sealed record LaneSeed(Guid UserId, Guid FileDocumentId);

    private static async Task ExpectUpRefusalAsync(string connectionString, string drift, string message, string repair)
    {
        await ExecuteAsync(connectionString, drift);
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsMigratorAsync(connectionString, AddInPersonDriverDocuments.RenderUpSql()));
        Assert.Equal(message, refused.MessageText);
        await ExecuteAsync(connectionString, repair);
    }

    private static async Task AssertCanonicalShapeAsync(string connectionString)
    {
        Assert.Equal(DatabaseBaselineAssertions.InPersonDriverDocumentColumns, await ColumnsAsync(connectionString));
        Assert.Equal(
            DatabaseBaselineAssertions.InPersonDriverDocumentChecks,
            await ListAsync(connectionString,
                """
                SELECT conname || ':' || pg_get_constraintdef(oid) || ':' || convalidated::text
                FROM pg_constraint
                WHERE conrelid='drivers.driver_documents'::regclass AND contype='c'
                  AND conname IN ('driver_documents_evidence_check','driver_documents_expiry_basis_check',
                                  'driver_documents_in_person_check','driver_documents_rejection_code_check',
                                  'driver_documents_review_check')
                ORDER BY conname
                """));
        Assert.Equal(
            AddInPersonDriverDocuments.ContractForeignKeys,
            await ListAsync(connectionString,
                """
                SELECT pg_get_constraintdef(c.oid)
                FROM pg_constraint c
                JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=ANY(c.conkey)
                WHERE c.conrelid='drivers.driver_documents'::regclass AND c.contype='f'
                  AND a.attname IN ('reviewed_by','revoked_by')
                ORDER BY 1
                """));
        Assert.Equal(
            "true|true|paqueteria_migrator",
            await ScalarAsync<string>(connectionString,
                "SELECT relrowsecurity::text || '|' || relforcerowsecurity::text || '|' || pg_get_userbyid(relowner) FROM pg_class WHERE oid='drivers.driver_documents'::regclass"));
        // No new index: the eligibility index serves the reads.
        Assert.Equal(
            ["driver_documents_eligibility_idx", "driver_documents_pkey"],
            await ListAsync(connectionString,
                "SELECT indexname FROM pg_indexes WHERE schemaname='drivers' AND tablename='driver_documents' ORDER BY 1"));
    }

    private async Task AssertRollbackBlockedAsync(string connectionString)
    {
        var blocked = await Assert.ThrowsAnyAsync<Exception>(() => RollBackToVoiceBridgeAsync(connectionString));
        var postgres = blocked as PostgresException ?? blocked.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(AddInPersonDriverDocuments.DowngradeBlockedMessage, postgres.MessageText);
        Assert.Equal(FullHistory, await HistoryAsync(connectionString));
        await AssertCanonicalShapeAsync(connectionString);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conname='driver_documents_in_person_downgrade_guard'"));
    }

    private static async Task RollBackToVoiceBridgeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
            })
            .Options;
        await using var context = new DriversDbContext(options, new TenantDatabaseExecutionState());
        await context.GetService<IMigrator>().MigrateAsync(AddDriverVoiceBridge.MigrationId);
    }

    private static async Task AssertBaselineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    private static Task<string[]> ColumnsAsync(string connectionString) => ListAsync(connectionString,
        """
        SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
        FROM information_schema.columns
        WHERE table_schema='drivers' AND table_name='driver_documents'
          AND column_name IN ('evidence_kind','expiry_basis','object_key','rejection_code','reviewed_at','reviewed_by',
                              'revoked_at','revoked_by','sha256')
        ORDER BY column_name
        """);

    private static Task<long> CheckCountAsync(string connectionString) => ScalarAsync<long>(connectionString,
        """
        SELECT count(*) FROM pg_constraint
        WHERE conrelid='drivers.driver_documents'::regclass AND contype='c'
          AND conname IN ('driver_documents_evidence_check','driver_documents_expiry_basis_check',
                          'driver_documents_in_person_check','driver_documents_rejection_code_check',
                          'driver_documents_review_check')
        """);

    private static Task<string[]> HistoryAsync(string connectionString) =>
        ListAsync(connectionString, "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_drivers ORDER BY 1");

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module)
            .Status;

    private static async Task ExecuteAsMigratorAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }
}
