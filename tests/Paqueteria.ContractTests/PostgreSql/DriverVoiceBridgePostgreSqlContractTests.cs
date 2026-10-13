using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Drivers.Application.Voice;
using Drivers.Endpoints;
using Drivers.Infrastructure;
using Drivers.Infrastructure.Persistence;
using Drivers.Infrastructure.Persistence.Migrations;
using Drivers.Infrastructure.Voice;
using Locations.Application.Geocoding;
using Locations.Infrastructure.Geocoding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Voice;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.Infrastructure.Voice;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11 against a real PostgreSQL: the Drivers lane migration (catalog, idempotency,
/// drift refusal, guarded rollback), the driver phone stored with the ADP-001 envelope, and the recipient call
/// request: DRIVER only, own ACCEPTED or ACTIVE assignment, DELIVERING only, uniform not-found, idempotency (retry-safe,
/// with abandoned requests ended UNCONFIRMED), rate limits, the provider outcome, the signed status callback and the
/// key presses of VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11. The driver's own phone stays readable and erasable whatever
/// the profile's status (ARCO). No number is ever stored in clear, audited or returned to the driver.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DriverVoiceBridgePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Drivers";
    private const string DriverDigits = "5511112222";
    private const string RecipientDigits = "3312345678";
    // Without long digit runs: the audit redactor masks any value that could be a phone number.
    private const string CallSid = "CAfedcba98fedcba98fedcba98fedcba98";
    private const string OtherCallSid = "CAabcdef01abcdef01abcdef01abcdef01";
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 16, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ migration

    [PostgreSqlContractFact]
    public async Task The_lane_carries_the_voice_bridge_with_its_exact_catalog()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(AddDriverVoiceBridge.MigrationId, verified.MigrationId);
        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal("APPLIED", applied.Status);

        var connectionString = fixture.DeploymentConnectionString;
        Assert.Equal(
            AddDriverVoiceBridge.PhoneCheckDefinition,
            await ScalarAsync<string>(connectionString,
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='drivers.driver_profiles'::regclass AND conname='driver_profiles_phone_check'"));
        Assert.Equal(
            AddDriverVoiceBridge.CallRequestColumns,
            await ListAsync(connectionString,
                """
                SELECT a.attname || ':' || format_type(a.atttypid,a.atttypmod) || ':' ||
                       CASE WHEN a.attnotnull THEN 'not-null' ELSE 'nullable' END
                FROM pg_attribute a
                WHERE a.attrelid='drivers.recipient_call_requests'::regclass AND a.attnum>0 AND NOT a.attisdropped
                ORDER BY a.attnum
                """));
        Assert.Equal(
            "true|true|paqueteria_migrator",
            await ScalarAsync<string>(connectionString,
                "SELECT relrowsecurity::text || '|' || relforcerowsecurity::text || '|' || pg_get_userbyid(relowner) FROM pg_class WHERE oid='drivers.recipient_call_requests'::regclass"));
        Assert.Equal(
            ["recipient_call_requests_tenant|ALL|PERMISSIVE|true|true"],
            await ListAsync(connectionString,
                """
                SELECT policyname || '|' || cmd || '|' || permissive || '|' ||
                       (qual LIKE '%security.app_allowed_org(org_id)%')::text || '|' ||
                       (with_check LIKE '%security.app_allowed_org(org_id)%')::text
                FROM pg_policies WHERE schemaname='drivers' AND tablename='recipient_call_requests'
                """));
        // The API reads, inserts and updates; nobody deletes at runtime; the Worker has nothing.
        Assert.Equal(
            ["paqueteria_app:SELECT:True", "paqueteria_app:INSERT:True", "paqueteria_app:UPDATE:True",
                "paqueteria_app:DELETE:False", "paqueteria_app:TRUNCATE:False", "paqueteria_worker:SELECT:False",
                "paqueteria_worker:INSERT:False", "paqueteria_worker:UPDATE:False", "paqueteria_worker:DELETE:False"],
            await PrivilegesAsync(connectionString));
        // No foreign key into Orders or Dispatch, and no column that could hold a number.
        Assert.Equal(
            ["drivers.driver_profiles", "identity.users", "organizations.organizations"],
            await ListAsync(connectionString,
                "SELECT confrelid::regclass::text FROM pg_constraint WHERE conrelid='drivers.recipient_call_requests'::regclass AND contype='f' ORDER BY 1"));
        Assert.DoesNotContain(AddDriverVoiceBridge.CallRequestColumns, column => column.Contains("phone", StringComparison.Ordinal));
        Assert.Equal(
            ["recipient_call_requests_call_sid_key", "recipient_call_requests_driver_idx", "recipient_call_requests_order_idx",
                "recipient_call_requests_pkey"],
            await ListAsync(connectionString,
                "SELECT indexname FROM pg_indexes WHERE schemaname='drivers' AND tablename='recipient_call_requests' ORDER BY 1"));
    }

    [PostgreSqlContractFact]
    public async Task The_migration_is_idempotent_refuses_drift_and_rolls_back_only_without_data()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("voice001lane");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            Assert.Equal(
                [AdoptCanonicalDriversBaseline.MigrationId, AdoptCanonicalDriverPositions.MigrationId, AddDriverVoiceBridge.MigrationId],
                await ListAsync(connectionString, "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_drivers ORDER BY 1"));

            // Running the Up SQL again changes nothing.
            await ExecuteAsMigratorAsync(connectionString, AddDriverVoiceBridge.RenderUpSql());
            Assert.True(await VoiceObjectsPresentAsync(connectionString));

            // Drift is refused: a phone column added to the request table, or a phone column of the profile missing.
            await ExecuteAsync(connectionString, "ALTER TABLE drivers.recipient_call_requests ADD COLUMN recipient_phone text;");
            Assert.Equal(
                "drivers.recipient_call_requests differs from the VOICE-001 contract",
                (await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsMigratorAsync(connectionString, AddDriverVoiceBridge.RenderUpSql()))).MessageText);
            await ExecuteAsync(connectionString, "ALTER TABLE drivers.recipient_call_requests DROP COLUMN recipient_phone;");

            await ExecuteAsync(connectionString, "ALTER TABLE drivers.driver_profiles DROP COLUMN phone_consented_at;");
            Assert.Equal(
                "drivers.driver_profiles phone columns are partially present",
                (await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsMigratorAsync(connectionString, AddDriverVoiceBridge.RenderUpSql()))).MessageText);
            await ExecuteAsync(connectionString, "ALTER TABLE drivers.driver_profiles ADD COLUMN phone_consented_at timestamptz;");

            await ExecuteAsync(connectionString,
                "ALTER TABLE drivers.driver_profiles ADD CONSTRAINT driver_profiles_phone_check CHECK (true);");
            Assert.Equal(
                "drivers.driver_profiles phone check differs from the VOICE-001 contract",
                (await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsMigratorAsync(connectionString, AddDriverVoiceBridge.RenderUpSql()))).MessageText);
            await ExecuteAsync(connectionString, "ALTER TABLE drivers.driver_profiles DROP CONSTRAINT driver_profiles_phone_check;");
            await ExecuteAsMigratorAsync(connectionString, AddDriverVoiceBridge.RenderUpSql());
            Assert.True(await VoiceObjectsPresentAsync(connectionString));

            // The rollback refuses while a stored driver phone or a call request exists.
            var org = Guid.NewGuid();
            var user = Guid.NewGuid();
            var city = Guid.NewGuid();
            var driver = Guid.NewGuid();
            await ExecuteAsync(connectionString,
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@org,'VOICE-001 lane','VOICE-001 lane','BUSINESS');
                INSERT INTO identity.users(id,identity_subject,status) VALUES (@user,@subject,'ACTIVE');
                INSERT INTO locations.cities(id,state_code,name,timezone) VALUES (@city,'SI',@city_name,'America/Mazatlan');
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,
                  phone_ciphertext,phone_pii_key_version,phone_consent_version,phone_consented_at)
                VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',
                  decode(repeat('ab',32),'hex'),'akv:pii-envelope/lane','VOICE-001-CONSENT-V1',clock_timestamp());
                """,
                new("org", org), new("user", user), new("subject", $"voice001|{user:N}"), new("city", city),
                new("city_name", $"VOICE-001 {city:N}"), new("driver", driver));
            await AssertRollbackBlockedAsync(connectionString);

            await ExecuteAsync(connectionString,
                """
                UPDATE drivers.driver_profiles
                SET phone_ciphertext=NULL,phone_pii_key_version=NULL,phone_consent_version=NULL,phone_consented_at=NULL
                WHERE id=@driver;
                INSERT INTO drivers.recipient_call_requests(
                  id,org_id,driver_id,requested_by,order_id,assignment_id,provider,status,result_code,requested_at)
                VALUES (gen_random_uuid(),@org,@driver,@user,gen_random_uuid(),gen_random_uuid(),'TWILIO','REQUESTED',
                  'VOICE_CALL_REQUESTED',clock_timestamp());
                """,
                new("org", org), new("user", user), new("driver", driver));
            await AssertRollbackBlockedAsync(connectionString);

            // Without data it drops exactly what it added, and the canonical migrator brings it back.
            await ExecuteAsync(connectionString, "DELETE FROM drivers.recipient_call_requests WHERE org_id=@org;", new NpgsqlParameter("org", org));
            await RollBackToDriverPositionsAsync(connectionString);
            Assert.False(await VoiceObjectsPresentAsync(connectionString));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(connectionString,
                    "SELECT count(*) FROM information_schema.columns WHERE table_schema='drivers' AND table_name='driver_profiles' AND column_name LIKE 'phone%'"));
            Assert.Equal(
                [AdoptCanonicalDriversBaseline.MigrationId, AdoptCanonicalDriverPositions.MigrationId],
                await ListAsync(connectionString, "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_drivers ORDER BY 1"));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM drivers.driver_profiles"));

            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.True(await VoiceObjectsPresentAsync(connectionString));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    // ------------------------------------------------------------------ driver phone

    [PostgreSqlContractFact]
    public async Task The_driver_phone_is_stored_with_the_envelope_bound_to_the_profile_and_can_be_removed()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        await using var services = CreateServices(VoiceBridgeMode.Live, vault);

        Assert.Equal(
            new DriverPhoneStatusResult(true, false, null, null),
            await services.Phones.GetAsync(scenario.DriverUserId, scenario.OrganizationId, default));

        var registered = await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        Assert.Equal(new DriverPhoneStatusResult(true, true, DriverPhoneConsent.CurrentVersion, Now), registered);
        var stored = await ReadDriverPhoneAsync(scenario.DriverId);
        Assert.NotNull(stored.Ciphertext);
        Assert.Equal(vault.CurrentVersion, stored.KeyVersion);
        Assert.Equal(DriverPhoneConsent.CurrentVersion, stored.ConsentVersion);
        Assert.Equal(Now, stored.ConsentedAt);
        Assert.DoesNotContain(DriverDigits, Encoding.Latin1.GetString(stored.Ciphertext!), StringComparison.Ordinal);

        // The envelope binds the number to this tenant, this profile and this column.
        Assert.Equal(
            DriverDigits,
            await services.Envelope.UnprotectAsync(
                new PiiBinding(scenario.OrganizationId, scenario.DriverId),
                VoicePiiPurposes.DriverPhone,
                stored.Ciphertext!,
                stored.KeyVersion!,
                default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() => services.Envelope.UnprotectAsync(
            new PiiBinding(scenario.OrganizationId, scenario.OtherDriverId),
            VoicePiiPurposes.DriverPhone,
            stored.Ciphertext!,
            stored.KeyVersion!,
            default));
        await Assert.ThrowsAsync<PiiCiphertextRejectedException>(() => services.Envelope.UnprotectAsync(
            new PiiBinding(scenario.OrganizationId, scenario.DriverId),
            VoicePiiPurposes.LocationPhone,
            stored.Ciphertext!,
            stored.KeyVersion!,
            default));

        // Replacing and removing are audited without the number; removing twice writes nothing more.
        services.Clock.UtcNow = Now.AddMinutes(1);
        await services.Phones.RegisterAsync(Register(scenario, "55 3333 4444"), default);
        services.Clock.UtcNow = Now.AddMinutes(2);
        var removed = await services.Phones.RemoveAsync(scenario.DriverUserId, scenario.OrganizationId, "req-remove", default);
        Assert.Equal(new DriverPhoneStatusResult(true, false, null, null), removed);
        await AssertNoDriverPhoneAsync(scenario.DriverId);
        await services.Phones.RemoveAsync(scenario.DriverUserId, scenario.OrganizationId, "req-remove-again", default);

        var audits = await ReadAuditsAsync(scenario.OrganizationId, "DriverProfile");
        Assert.Equal(
            [
                ("DRIVER_PHONE_REGISTERED", scenario.DriverUserId, scenario.DriverId),
                ("DRIVER_PHONE_REGISTERED", scenario.DriverUserId, scenario.DriverId),
                ("DRIVER_PHONE_REMOVED", scenario.DriverUserId, scenario.DriverId),
            ],
            audits.Select(audit => (audit.Action, audit.ActorId!.Value, audit.EntityId)));
        Assert.Equal("false", JsonDocument.Parse(audits[0].Payload).RootElement.GetProperty("replaced").GetRawText());
        Assert.Equal("true", JsonDocument.Parse(audits[1].Payload).RootElement.GetProperty("replaced").GetRawText());
        Assert.Equal(
            DriverPhoneConsent.CurrentVersion,
            JsonDocument.Parse(audits[0].Payload).RootElement.GetProperty("consent_version").GetString());
        Assert.Equal(
            0L,
            await Adp001FakeKeyVault.CountPlaintextInAuditAndOutboxAsync(
                fixture.AdminDataSource, scenario.OrganizationId, [DriverDigits, "5533334444", "+52" + DriverDigits]));

        // Only an ACTIVE DRIVER of the selected organization has a phone here.
        await Assert.ThrowsAsync<DriverPhoneForbiddenException>(() =>
            services.Phones.GetAsync(scenario.DispatcherUserId, scenario.OrganizationId, default));
        await Assert.ThrowsAsync<DriverPhoneForbiddenException>(() =>
            services.Phones.RegisterAsync(Register(scenario, DriverDigits) with { ActorId = scenario.DispatcherUserId }, default));
        await Assert.ThrowsAsync<DriverPhoneForbiddenException>(() =>
            services.Phones.RemoveAsync(scenario.DispatcherUserId, scenario.OrganizationId, null, default));
    }

    [PostgreSqlContractFact]
    public async Task While_the_bridge_is_disabled_no_phone_is_collected_but_one_can_be_removed()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        await using (var live = CreateServices(VoiceBridgeMode.Live, vault))
        {
            await live.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        }

        await using var disabled = CreateServices(VoiceBridgeMode.Disabled, vault);
        Assert.Equal(
            new DriverPhoneStatusResult(false, true, DriverPhoneConsent.CurrentVersion, Now),
            await disabled.Phones.GetAsync(scenario.DriverUserId, scenario.OrganizationId, default));
        await Assert.ThrowsAsync<DriverPhoneUnavailableException>(() =>
            disabled.Phones.RegisterAsync(Register(scenario, "5533334444"), default));
        Assert.Equal(vault.CurrentVersion, (await ReadDriverPhoneAsync(scenario.DriverId)).KeyVersion);

        Assert.Equal(
            new DriverPhoneStatusResult(false, false, null, null),
            await disabled.Phones.RemoveAsync(scenario.DriverUserId, scenario.OrganizationId, null, default));
        await AssertNoDriverPhoneAsync(scenario.DriverId);
    }

    // ------------------------------------------------------------------ ARCO

    [PostgreSqlContractFact]
    public async Task A_driver_that_is_no_longer_active_still_reads_and_erases_its_phone_but_registers_and_calls_nothing()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(_ => throw new InvalidOperationException("The provider must not be called."));
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider);
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        await scenario.SetDriverProfileStatusAsync("SUSPENDED");

        // Registering and calling need an ACTIVE driver.
        await Assert.ThrowsAsync<DriverPhoneForbiddenException>(() =>
            services.Phones.RegisterAsync(Register(scenario, "5533334444"), default));
        await Assert.ThrowsAsync<RecipientCallForbiddenException>(() =>
            services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
        await Assert.ThrowsAsync<RecipientCallForbiddenException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-suspended"), default));
        Assert.Equal(vault.CurrentVersion, (await ReadDriverPhoneAsync(scenario.DriverId)).KeyVersion);

        // Seeing and erasing its own number keep working (ARCO), audited as the driver.
        Assert.Equal(
            new DriverPhoneStatusResult(true, true, DriverPhoneConsent.CurrentVersion, Now),
            await services.Phones.GetAsync(scenario.DriverUserId, scenario.OrganizationId, default));
        services.Clock.UtcNow = Now.AddMinutes(1);
        Assert.Equal(
            new DriverPhoneStatusResult(true, false, null, null),
            await services.Phones.RemoveAsync(scenario.DriverUserId, scenario.OrganizationId, "req-arco", default));
        await AssertNoDriverPhoneAsync(scenario.DriverId);
        Assert.Equal(
            ("DRIVER_PHONE_REMOVED", (Guid?)scenario.DriverUserId, scenario.DriverId),
            (await ReadAuditsAsync(scenario.OrganizationId, "DriverProfile"))
                .Select(audit => (audit.Action, audit.ActorId, audit.EntityId))
                .Last());

        // The same for an INACTIVE or a PENDING profile; a member without a driver profile still has nothing here.
        foreach (var status in new[] { "INACTIVE", "PENDING" })
        {
            await scenario.SetDriverProfileStatusAsync(status);
            Assert.Equal(
                new DriverPhoneStatusResult(true, false, null, null),
                await services.Phones.GetAsync(scenario.DriverUserId, scenario.OrganizationId, default));
            Assert.Equal(
                new DriverPhoneStatusResult(true, false, null, null),
                await services.Phones.RemoveAsync(scenario.DriverUserId, scenario.OrganizationId, null, default));
            await Assert.ThrowsAsync<DriverPhoneForbiddenException>(() =>
                services.Phones.RegisterAsync(Register(scenario, DriverDigits), default));
        }

        await Assert.ThrowsAsync<DriverPhoneForbiddenException>(() =>
            services.Phones.RemoveAsync(scenario.DispatcherUserId, scenario.OrganizationId, null, default));
        Assert.Single(
            await ReadAuditsAsync(scenario.OrganizationId, "DriverProfile"),
            audit => audit.Action == "DRIVER_PHONE_REMOVED");
        Assert.Empty(provider.Calls);
    }

    // ------------------------------------------------------------------ recipient call

    [PostgreSqlContractFact]
    public async Task The_driver_is_bridged_with_the_recipient_of_its_own_delivering_stop_once_per_key()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(_ => new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, CallSid));
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider);
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        Assert.Equal(
            RecipientCallAvailabilityResult.Yes,
            await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));

        var result = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);

        Assert.Equal(RecipientCallStatuses.Placed, result.Status);
        var placed = Assert.Single(provider.Calls);
        Assert.Equal((result.CallRequestId, scenario.OrganizationId, "+52" + DriverDigits, "+52" + RecipientDigits), placed);

        var row = await ReadCallRequestAsync(result.CallRequestId);
        Assert.Equal(
            new CallRequestRow(scenario.OrganizationId, scenario.DriverId, scenario.DriverUserId, scenario.OrderId,
                scenario.AssignmentId, "TWILIO", "PLACED", "VOICE_CALL_PLACED", CallSid, null, null, Now, Now, null),
            row);

        var idempotency = await ReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-1");
        Assert.Equal(202, idempotency.Status);
        Assert.Equal(result.CallRequestId, idempotency.ResourceId);
        var body = JsonDocument.Parse(idempotency.Body!).RootElement;
        Assert.Equal(
            ["call_request_id", "status"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(result.CallRequestId, body.GetProperty("call_request_id").GetGuid());
        Assert.Equal("PLACED", body.GetProperty("status").GetString());

        var audits = await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest");
        Assert.Equal(
            [("RECIPIENT_CALL_REQUESTED", scenario.DriverUserId, result.CallRequestId),
                ("RECIPIENT_CALL_PLACED", scenario.DriverUserId, result.CallRequestId)],
            audits.Select(audit => (audit.Action, audit.ActorId!.Value, audit.EntityId)));
        var outcome = JsonDocument.Parse(audits[1].Payload).RootElement;
        Assert.Equal(CallSid, outcome.GetProperty("call_sid").GetString());
        Assert.Equal(scenario.OrderId, outcome.GetProperty("order_id").GetGuid());
        Assert.Equal("VOICE_CALL_PLACED", outcome.GetProperty("result_code").GetString());

        // No number anywhere: not in the request row, the idempotency answer, the audit or the outbox.
        Assert.Equal(0L, await CountRowsContainingAsync(scenario.OrganizationId, DriverDigits, RecipientDigits));
        Assert.Equal(
            0L,
            await Adp001FakeKeyVault.CountPlaintextInAuditAndOutboxAsync(
                fixture.AdminDataSource, scenario.OrganizationId,
                [DriverDigits, RecipientDigits, "+52" + DriverDigits, "+52" + RecipientDigits]));

        // The same key replays the stored answer and calls nobody; reusing it for another order is a conflict.
        Assert.Equal(result, await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));
        Assert.Single(provider.Calls);
        Assert.Equal(
            RecipientCallConflictCodes.IdempotencyConflict,
            (await Assert.ThrowsAsync<RecipientCallConflictException>(() =>
                services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1") with { OrderId = Guid.NewGuid() }, default))).Code);
        Assert.Single(provider.Calls);
    }

    [PostgreSqlContractFact]
    public async Task Only_the_assigned_driver_of_a_delivering_stop_with_both_phones_reaches_the_provider()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        await using var foreign = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(_ => throw new InvalidOperationException("The provider must not be called."));
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider);

        // A member without a driver profile (the scenario's DISPATCHER) is refused before any stop is read.
        await Assert.ThrowsAsync<RecipientCallForbiddenException>(() =>
            services.Calls.GetAvailabilityAsync(scenario.DispatcherUserId, scenario.OrganizationId, scenario.OrderId, default));
        await Assert.ThrowsAsync<RecipientCallForbiddenException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-x") with { ActorId = scenario.DispatcherUserId }, default));

        // Unknown order, another driver's stop and another tenant's stop are the same not-found.
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        await services.Phones.RegisterAsync(Register(scenario, "5533334444") with { ActorId = scenario.OtherDriverUserId }, default);
        foreach (var (actor, order) in new[]
                 {
                     (scenario.DriverUserId, Guid.NewGuid()),
                     (scenario.OtherDriverUserId, scenario.OrderId),
                     (scenario.DriverUserId, foreign.OrderId),
                 })
        {
            await Assert.ThrowsAsync<RecipientCallNotFoundException>(() =>
                services.Calls.GetAvailabilityAsync(actor, scenario.OrganizationId, order, default));
            await Assert.ThrowsAsync<RecipientCallNotFoundException>(() =>
                services.Calls.RequestAsync(Call(scenario, $"voice-001-idem-key-{order:N}") with { ActorId = actor, OrderId = order }, default));
        }

        // An offered or finished assignment is not the driver's current stop.
        foreach (var status in new[] { "OFFERED", "COMPLETED", "CANCELLED" })
        {
            await scenario.SetAssignmentStatusAsync(status);
            await Assert.ThrowsAsync<RecipientCallNotFoundException>(() =>
                services.Calls.RequestAsync(Call(scenario, $"voice-001-idem-key-{status}"), default));
        }

        await scenario.SetAssignmentStatusAsync("ACTIVE");
        Assert.Equal(
            RecipientCallAvailabilityResult.Yes,
            await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));

        // Only while DELIVERING, and only with both numbers stored.
        foreach (var status in new[] { "ASSIGNED", "PICKED_UP", "IN_TRANSIT", "FAILED_ATTEMPT", "DELIVERED" })
        {
            await scenario.SetOrderStatusAsync(status);
            Assert.Equal(
                RecipientCallAvailabilityResult.No(RecipientCallReasons.OrderStateNotAllowed),
                await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
            Assert.Equal(
                RecipientCallConflictCodes.OrderStateNotAllowed,
                (await Assert.ThrowsAsync<RecipientCallConflictException>(() =>
                    services.Calls.RequestAsync(Call(scenario, $"voice-001-idem-key-{status}"), default))).Code);
        }

        await scenario.SetOrderStatusAsync("DELIVERING");
        await scenario.ClearRecipientPhoneAsync();
        Assert.Equal(
            RecipientCallAvailabilityResult.No(RecipientCallReasons.RecipientPhoneUnavailable),
            await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
        Assert.Equal(
            RecipientCallConflictCodes.RecipientPhoneUnavailable,
            (await Assert.ThrowsAsync<RecipientCallConflictException>(() =>
                services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-no-recipient"), default))).Code);

        await scenario.ProtectRecipientPhoneAsync(vault);
        await services.Phones.RemoveAsync(scenario.DriverUserId, scenario.OrganizationId, null, default);
        Assert.Equal(
            RecipientCallAvailabilityResult.No(RecipientCallReasons.DriverPhoneRequired),
            await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
        Assert.Equal(
            RecipientCallConflictCodes.DriverPhoneRequired,
            (await Assert.ThrowsAsync<RecipientCallConflictException>(() =>
                services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-no-driver"), default))).Code);

        // A disabled bridge says so and places nothing.
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        await using var disabled = CreateServices(VoiceBridgeMode.Disabled, vault, provider);
        Assert.Equal(
            RecipientCallAvailabilityResult.No(RecipientCallReasons.VoiceCallsDisabled),
            await disabled.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
        await Assert.ThrowsAsync<RecipientCallUnavailableException>(() =>
            disabled.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-disabled"), default));

        // Every refusal wrote nothing.
        Assert.Empty(provider.Calls);
        Assert.Equal(0L, await ScalarAdminAsync("SELECT count(*) FROM drivers.recipient_call_requests WHERE org_id=@org", scenario.OrganizationId));
        Assert.Equal(
            0L,
            await ScalarAdminAsync(
                "SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org AND scope='VOICE-001:REQUEST_RECIPIENT_CALL'",
                scenario.OrganizationId));
        Assert.Empty(await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest"));
    }

    [PostgreSqlContractFact]
    public async Task Rate_limits_count_calls_that_may_ring_and_a_failed_call_releases_its_key()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var outcome = new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected);
        var provider = new FakeVoiceProvider(request => outcome with
        {
            ProviderCallId = outcome.Outcome == VoiceBridgeOutcome.Placed ? "CA" + request.CallRequestId.ToString("N") : null,
        });
        await using var services = CreateServices(
            VoiceBridgeMode.Live,
            vault,
            provider,
            new RecipientCallOptions { MaximumPerOrder = 2, OrderWindowMinutes = 15, MaximumPerDriverPerHour = 20 });
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        // A provider refusal places nothing: 503, the row ends FAILED and the key is free again.
        await Assert.ThrowsAsync<RecipientCallUnavailableException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));
        var failed = Assert.Single(await ReadCallRequestsAsync(scenario.OrganizationId));
        Assert.Equal(("FAILED", "VOICE_PROVIDER_REJECTED", (string?)null), (failed.Status, failed.ResultCode, failed.ProviderCallSid));
        Assert.Null(await TryReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-1"));
        Assert.Equal(
            ["RECIPIENT_CALL_REQUESTED", "RECIPIENT_CALL_FAILED"],
            (await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest")).Select(audit => audit.Action));

        // The driver's own number refused by the provider is a 409 the driver can fix.
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DriverPhoneRejected);
        Assert.Equal(
            RecipientCallConflictCodes.DriverPhoneRejected,
            (await Assert.ThrowsAsync<RecipientCallConflictException>(() =>
                services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default))).Code);

        // Two calls that may ring fill the order window; failed ones never count. A 5xx after the request left may
        // have created the call: UNCONFIRMED, its key kept and counted like a placed call.
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed);
        Assert.Equal(RecipientCallStatuses.Placed, (await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default)).Status);
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Unavailable);
        Assert.Equal(RecipientCallStatuses.Unconfirmed, (await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default)).Status);
        var ambiguousKey = await ReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-2");
        Assert.Equal(202, ambiguousKey.Status);
        Assert.Equal("UNCONFIRMED", JsonDocument.Parse(ambiguousKey.Body!).RootElement.GetProperty("status").GetString());
        var ambiguousRow = await ReadCallRequestAsync(ambiguousKey.ResourceId!.Value);
        Assert.Equal(("UNCONFIRMED", "VOICE_PROVIDER_UNAVAILABLE"), (ambiguousRow.Status, ambiguousRow.ResultCode));

        var limited = await Assert.ThrowsAsync<RecipientCallRateLimitedException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-3"), default));
        Assert.Equal(TimeSpan.FromMinutes(15), limited.RetryAfter);
        // The availability says when to check again.
        Assert.Equal(
            new RecipientCallAvailabilityResult(false, RecipientCallReasons.RateLimited, 900),
            await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
        Assert.Null(await TryReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-3"));
        Assert.Equal(4, provider.Calls.Count);

        services.Clock.UtcNow = Now.AddMinutes(16);
        Assert.Equal(
            RecipientCallAvailabilityResult.Yes,
            await services.Calls.GetAvailabilityAsync(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, default));
    }

    [PostgreSqlContractFact]
    public async Task The_signed_status_callback_completes_an_unconfirmed_call_once()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout);
        var provider = new FakeVoiceProvider(_ => outcome);
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider);
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        var unconfirmed = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);
        Assert.Equal(RecipientCallStatuses.Unconfirmed, unconfirmed.Status);
        Assert.Equal("UNCONFIRMED", (await ReadCallRequestAsync(unconfirmed.CallRequestId)).Status);
        Assert.Equal(
            "UNCONFIRMED",
            JsonDocument.Parse((await ReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-1")).Body!).RootElement
                .GetProperty("status").GetString());

        services.Clock.UtcNow = Now.AddMinutes(2);
        Assert.True(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, unconfirmed.CallRequestId, CallSid, "completed", 42),
            default));
        var row = await ReadCallRequestAsync(unconfirmed.CallRequestId);
        Assert.Equal(
            ("PLACED", CallSid, "completed", (int?)42, (DateTimeOffset?)Now.AddMinutes(2)),
            (row.Status, row.ProviderCallSid, row.ProviderCallStatus, row.DurationSeconds, row.StatusReportedAt));
        Assert.Equal("VOICE_PROVIDER_TIMEOUT", row.ResultCode);
        var reported = (await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest")).Last();
        Assert.Equal(("RECIPIENT_CALL_STATUS_REPORTED", (Guid?)null), (reported.Action, reported.ActorId));
        Assert.Equal(
            ["call_request_id", "call_sid", "call_status", "duration_seconds", "order_id"],
            JsonDocument.Parse(reported.Payload).RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        // Once final, later reports change nothing; another tenant, an unknown request or another call id never match.
        Assert.False(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, unconfirmed.CallRequestId, CallSid, "failed", 0), default));
        Assert.False(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(Guid.NewGuid(), unconfirmed.CallRequestId, CallSid, "completed", 1), default));
        Assert.False(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, Guid.NewGuid(), OtherCallSid, "completed", 1), default));
        var unchanged = await ReadCallRequestAsync(unconfirmed.CallRequestId);
        Assert.Equal(("completed", (int?)42), (unchanged.ProviderCallStatus, unchanged.DurationSeconds));

        // A call id already attached to another request is refused, not moved.
        var second = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default);
        Assert.False(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, second.CallRequestId, CallSid, "completed", 5), default));
        Assert.True(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, second.CallRequestId, OtherCallSid, "no-answer", 0), default));
        var secondRow = await ReadCallRequestAsync(second.CallRequestId);
        Assert.Equal(("PLACED", OtherCallSid, "no-answer"), (secondRow.Status, secondRow.ProviderCallSid, secondRow.ProviderCallStatus));
        Assert.Equal(0L, await CountRowsContainingAsync(scenario.OrganizationId, DriverDigits, RecipientDigits));
    }

    [PostgreSqlContractFact]
    public async Task The_status_callback_never_promotes_a_failed_request_but_may_complete_one_in_flight()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        VoiceServices? current = null;
        var outcome = new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected);
        var provider = new FakeVoiceProvider(
            _ => outcome,
            async request =>
            {
                if (outcome.Outcome == VoiceBridgeOutcome.Placed)
                {
                    // The provider's final report arrives before the request recorded its own outcome.
                    Assert.True(await current!.Calls.RecordStatusAsync(
                        new VoiceCallStatusReport(request.OrganizationId, request.CallRequestId, CallSid, "no-answer", 0),
                        default));
                }
            });
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider);
        current = services;
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        // A request answered as definitively failed (nothing was created) stays FAILED whatever a callback says.
        await Assert.ThrowsAsync<RecipientCallUnavailableException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));
        var failedId = await SingleRequestIdAsync(scenario.OrganizationId, "FAILED");
        Assert.False(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, failedId, OtherCallSid, "completed", 30), default));
        var failed = await ReadCallRequestAsync(failedId);
        Assert.Equal(
            ("FAILED", "VOICE_PROVIDER_REJECTED", (string?)null, (string?)null),
            (failed.Status, failed.ResultCode, failed.ProviderCallSid, failed.ProviderCallStatus));

        // A callback that completes the request while it is still REQUESTED: the request keeps that answer, writes no
        // second outcome and settles its key with it.
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, CallSid);
        var placed = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default);
        Assert.Equal(RecipientCallStatuses.Placed, placed.Status);
        var row = await ReadCallRequestAsync(placed.CallRequestId);
        Assert.Equal(
            ("PLACED", "VOICE_CALL_REQUESTED", CallSid, "no-answer", (int?)0),
            (row.Status, row.ResultCode, row.ProviderCallSid, row.ProviderCallStatus, row.DurationSeconds));
        Assert.Equal(
            "PLACED",
            JsonDocument.Parse((await ReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-2")).Body!).RootElement
                .GetProperty("status").GetString());
        Assert.Equal(
            ["RECIPIENT_CALL_REQUESTED", "RECIPIENT_CALL_STATUS_REPORTED"],
            (await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest"))
                .Where(audit => audit.EntityId == placed.CallRequestId)
                .Select(audit => audit.Action));
    }

    // ------------------------------------------------------------------ retries and abandoned requests

    [PostgreSqlContractFact]
    public async Task A_retry_after_the_reservation_committed_goes_on_placing_its_own_call()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(_ => new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, CallSid));
        CrashOnceResolver? crashing = null;
        var callRequestId = Guid.NewGuid();
        await using var services = CreateServices(
            VoiceBridgeMode.Live,
            vault,
            provider,
            newCallRequestId: () => callRequestId,
            wrapResolver: inner => crashing = new CrashOnceResolver(inner));
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        // Transaction 1 commits (reservation, REQUESTED row, audit) and the attempt stops before the provider is called,
        // as when the commit's answer is lost and the execution strategy runs transaction 1 again.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));
        Assert.Equal("REQUESTED", (await ReadCallRequestAsync(callRequestId)).Status);
        Assert.Equal(((int?)null, (Guid?)callRequestId), await PendingReservationAsync(scenario.OrganizationId, "voice-001-idem-key-1"));
        Assert.Empty(provider.Calls);

        // The retry, with the same call request id, recognizes its own reservation: nothing is written twice and the
        // call it reserved is placed.
        crashing!.Crash = false;
        var result = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);

        Assert.Equal(new RecipientCallRequestResult(callRequestId, RecipientCallStatuses.Placed), result);
        Assert.Equal(callRequestId, Assert.Single(provider.Calls).CallRequestId);
        var row = Assert.Single(await ReadCallRequestsAsync(scenario.OrganizationId));
        Assert.Equal(("PLACED", "VOICE_CALL_PLACED", CallSid), (row.Status, row.ResultCode, row.ProviderCallSid));
        Assert.Equal(202, (await ReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-1")).Status);
        Assert.Equal(
            ["RECIPIENT_CALL_REQUESTED", "RECIPIENT_CALL_PLACED"],
            (await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest")).Select(audit => audit.Action));

        // If the stop stopped qualifying in between, the resumed reservation places nothing and releases its key.
        callRequestId = Guid.NewGuid();
        crashing!.Crash = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default));
        await scenario.SetOrderStatusAsync("DELIVERED");
        crashing!.Crash = false;
        Assert.Equal(
            RecipientCallConflictCodes.OrderStateNotAllowed,
            (await Assert.ThrowsAsync<RecipientCallConflictException>(() =>
                services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default))).Code);
        var refused = await ReadCallRequestAsync(callRequestId);
        Assert.Equal(("FAILED", "VOICE_REQUEST_REFUSED"), (refused.Status, refused.ResultCode));
        Assert.Null(await TryReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-2"));
        Assert.Single(provider.Calls);
    }

    [PostgreSqlContractFact]
    public async Task A_request_abandoned_between_its_transactions_ends_unconfirmed_on_a_late_replay()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(_ => throw new InvalidOperationException("The provider must not be called."));
        await using var services = CreateServices(
            VoiceBridgeMode.Live,
            vault,
            provider,
            wrapResolver: inner => new CrashOnceResolver(inner));
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);

        // The process stops after transaction 1: the row stays REQUESTED (the call may or may not ring).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));
        var abandonedId = await SingleRequestIdAsync(scenario.OrganizationId, "REQUESTED");

        // While it may still be in flight, a replay answers REQUESTED and calls nobody.
        services.Clock.UtcNow = Now.AddSeconds(299);
        Assert.Equal(
            new RecipientCallRequestResult(abandonedId, RecipientCallStatuses.Requested),
            await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));

        // Once stale, the replay ends it UNCONFIRMED (never retried: the call may have rung) and settles the key.
        services.Clock.UtcNow = Now.AddSeconds(301);
        var ended = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);
        Assert.Equal(new RecipientCallRequestResult(abandonedId, RecipientCallStatuses.Unconfirmed), ended);
        var row = await ReadCallRequestAsync(abandonedId);
        Assert.Equal(
            ("UNCONFIRMED", "VOICE_OUTCOME_UNKNOWN", (DateTimeOffset?)Now.AddSeconds(301)),
            (row.Status, row.ResultCode, row.CompletedAt));
        var key = await ReadIdempotencyAsync(scenario.OrganizationId, "voice-001-idem-key-1");
        Assert.Equal(202, key.Status);
        Assert.Equal("UNCONFIRMED", JsonDocument.Parse(key.Body!).RootElement.GetProperty("status").GetString());
        var audits = await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest");
        Assert.Equal(
            [("RECIPIENT_CALL_REQUESTED", (Guid?)scenario.DriverUserId), ("RECIPIENT_CALL_UNCONFIRMED", scenario.DriverUserId)],
            audits.Select(audit => (audit.Action, audit.ActorId)));
        Assert.Equal(
            "VOICE_OUTCOME_UNKNOWN",
            JsonDocument.Parse(audits[1].Payload).RootElement.GetProperty("result_code").GetString());

        // Later replays answer the stored UNCONFIRMED; a final callback can still complete it.
        Assert.Equal(ended, await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default));
        Assert.Equal(2, (await ReadAuditsAsync(scenario.OrganizationId, "RecipientCallRequest")).Count);
        Assert.True(await services.Calls.RecordStatusAsync(
            new VoiceCallStatusReport(scenario.OrganizationId, abandonedId, CallSid, "completed", 12), default));
        Assert.Equal("PLACED", (await ReadCallRequestAsync(abandonedId)).Status);
        Assert.Empty(provider.Calls);
    }

    // ------------------------------------------------------------------ key presses (VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11)

    [PostgreSqlContractFact]
    public async Task Only_the_drivers_timely_1_dials_the_recipient_and_only_the_recipients_1_connects_them()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(request =>
            new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, SidFor(request.CallRequestId)));
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider, ManyCalls());
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        var organizationId = scenario.OrganizationId;
        var call = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);
        var sid = SidFor(call.CallRequestId);
        Assert.Null(await ReadBridgeStageAsync(call.CallRequestId));

        // The driver pressed 1: the recipient's number comes back, only in memory, for the provider's TwiML.
        services.Clock.UtcNow = Now.AddSeconds(10);
        var decision = await services.Calls.ConfirmDriverAsync(new VoiceKeypress(organizationId, call.CallRequestId, sid, true), default);
        Assert.Equal("+52" + RecipientDigits, decision.Recipient?.E164);
        Assert.Equal("DRIVER_CONFIRMED", await ReadBridgeStageAsync(call.CallRequestId));

        // A replayed or repeated 1 never dials again.
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, call.CallRequestId, sid, true), default)).Recipient);

        // The recipient pressed 1 on the platform's call: both legs are connected, once.
        services.Clock.UtcNow = Now.AddSeconds(20);
        Assert.True(await services.Calls.ConfirmRecipientAsync(new VoiceKeypress(organizationId, call.CallRequestId, sid, true), default));
        Assert.Equal("RECIPIENT_CONFIRMED", await ReadBridgeStageAsync(call.CallRequestId));
        Assert.False(await services.Calls.ConfirmRecipientAsync(new VoiceKeypress(organizationId, call.CallRequestId, sid, true), default));
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, call.CallRequestId, sid, true), default)).Recipient);

        var audits = await ReadAuditsAsync(organizationId, "RecipientCallRequest");
        Assert.Equal(
            [
                ("RECIPIENT_CALL_REQUESTED", (Guid?)scenario.DriverUserId),
                ("RECIPIENT_CALL_PLACED", scenario.DriverUserId),
                ("RECIPIENT_CALL_DRIVER_CONFIRMED", null),
                ("RECIPIENT_CALL_BRIDGED", null),
            ],
            audits.Select(audit => (audit.Action, audit.ActorId)));
        var bridged = JsonDocument.Parse(audits[3].Payload).RootElement;
        Assert.Equal(
            ["bridge_stage", "call_request_id", "call_sid", "order_id"],
            bridged.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("RECIPIENT_CONFIRMED", sid), (bridged.GetProperty("bridge_stage").GetString(), bridged.GetProperty("call_sid").GetString()));

        // The number was only ever returned: never stored, audited or published.
        Assert.Single(provider.Calls);
        Assert.Equal(0L, await CountRowsContainingAsync(organizationId, DriverDigits, RecipientDigits));
        Assert.Equal(
            0L,
            await Adp001FakeKeyVault.CountPlaintextInAuditAndOutboxAsync(
                fixture.AdminDataSource, organizationId, [DriverDigits, RecipientDigits, "+52" + RecipientDigits]));
    }

    [PostgreSqlContractFact]
    public async Task Each_webhook_audit_row_carries_the_trace_id_of_its_request()
    {
        // OBS-003: the Twilio webhooks run inside an API request, so the audit row each one writes carries that request's
        // W3C trace id in request_id: the State.TraceId of the request's own log lines (its hosting "Request starting").
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(request =>
            new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, SidFor(request.CallRequestId)));
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider, ManyCalls());
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        var organizationId = scenario.OrganizationId;
        var call = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);
        var sid = SidFor(call.CallRequestId);
        var logs = new RequestStartCapture();
        await using var host = await VoiceWebhookHost.StartAsync(services.Calls, logs);

        services.Clock.UtcNow = Now.AddSeconds(10);
        Assert.Equal(
            VoiceWebhookHost.BridgeTwiml,
            await host.PostAsync(VoiceWebhookPaths.DriverConfirm, organizationId, call.CallRequestId, HttpStatusCode.OK,
                ("CallSid", sid), ("Digits", "1")));
        services.Clock.UtcNow = Now.AddSeconds(20);
        Assert.Equal(
            VoiceWebhookHost.ConnectTwiml,
            await host.PostAsync(VoiceWebhookPaths.RecipientConfirm, organizationId, call.CallRequestId, HttpStatusCode.OK,
                ("CallSid", OtherCallSid), ("ParentCallSid", sid), ("Digits", "1")));
        services.Clock.UtcNow = Now.AddMinutes(1);
        await host.PostAsync(VoiceWebhookPaths.CallStatus, organizationId, call.CallRequestId, HttpStatusCode.NoContent,
            ("CallSid", sid), ("CallStatus", "completed"), ("CallDuration", "42"));

        var requestIds = await ReadAuditRequestIdsAsync(organizationId, call.CallRequestId);
        var webhooks = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RECIPIENT_CALL_DRIVER_CONFIRMED"] = logs.TraceIdOf(VoiceWebhookPaths.DriverConfirm),
            ["RECIPIENT_CALL_BRIDGED"] = logs.TraceIdOf(VoiceWebhookPaths.RecipientConfirm),
            ["RECIPIENT_CALL_STATUS_REPORTED"] = logs.TraceIdOf(VoiceWebhookPaths.CallStatus),
        };
        Assert.All(webhooks, webhook => Assert.True(TraceCorrelation.IsTraceId(webhook.Value), webhook.Key));
        Assert.Equal(3, webhooks.Values.Distinct(StringComparer.Ordinal).Count());
        foreach (var (action, traceId) in webhooks)
        {
            Assert.Equal((action, traceId), (action, requestIds[action]));
        }

        // The driver's own call request keeps the request id its command carried.
        Assert.Equal("req-call", requestIds["RECIPIENT_CALL_REQUESTED"]);
    }

    [PostgreSqlContractFact]
    public async Task Without_a_1_nobody_is_bridged_the_outcome_is_recorded_and_nothing_is_retried()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var provider = new FakeVoiceProvider(request =>
            new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, SidFor(request.CallRequestId)));
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider, ManyCalls());
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        var organizationId = scenario.OrganizationId;

        // The driver leg reached a voicemail (or the driver pressed nothing): hang up, the recipient is never dialed.
        var first = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);
        var firstSid = SidFor(first.CallRequestId);
        services.Clock.UtcNow = Now.AddSeconds(10);
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, first.CallRequestId, firstSid, false), default)).Recipient);
        Assert.Equal("DRIVER_NOT_CONFIRMED", await ReadBridgeStageAsync(first.CallRequestId));
        // A later 1 and the recipient step change nothing.
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, first.CallRequestId, firstSid, true), default)).Recipient);
        Assert.False(await services.Calls.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, first.CallRequestId, firstSid, true), default));
        Assert.Equal("DRIVER_NOT_CONFIRMED", await ReadBridgeStageAsync(first.CallRequestId));

        // The driver pressed 1 but the recipient's voicemail (or the recipient) pressed nothing: never connected.
        services.Clock.UtcNow = Now.AddSeconds(20);
        var second = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default);
        var secondSid = SidFor(second.CallRequestId);
        services.Clock.UtcNow = Now.AddSeconds(30);
        Assert.NotNull((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, second.CallRequestId, secondSid, true), default)).Recipient);
        services.Clock.UtcNow = Now.AddSeconds(40);
        Assert.False(await services.Calls.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, second.CallRequestId, secondSid, false), default));
        Assert.Equal("RECIPIENT_NOT_CONFIRMED", await ReadBridgeStageAsync(second.CallRequestId));
        Assert.False(await services.Calls.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, second.CallRequestId, secondSid, true), default));

        // Each outcome is audited once, without a number; nothing was placed again.
        Assert.Equal(2, provider.Calls.Count);
        var audits = await ReadAuditsAsync(organizationId, "RecipientCallRequest");
        Assert.Equal(
            [
                ("RECIPIENT_CALL_REQUESTED", first.CallRequestId),
                ("RECIPIENT_CALL_PLACED", first.CallRequestId),
                ("RECIPIENT_CALL_NOT_CONFIRMED", first.CallRequestId),
                ("RECIPIENT_CALL_REQUESTED", second.CallRequestId),
                ("RECIPIENT_CALL_PLACED", second.CallRequestId),
                ("RECIPIENT_CALL_DRIVER_CONFIRMED", second.CallRequestId),
                ("RECIPIENT_CALL_NOT_CONFIRMED", second.CallRequestId),
            ],
            audits.Select(audit => (audit.Action, audit.EntityId)));
        Assert.Equal(
            ["DRIVER_NOT_CONFIRMED", "RECIPIENT_NOT_CONFIRMED"],
            audits.Where(audit => audit.Action == "RECIPIENT_CALL_NOT_CONFIRMED")
                .Select(audit => JsonDocument.Parse(audit.Payload).RootElement.GetProperty("bridge_stage").GetString()));
        Assert.Equal(0L, await CountRowsContainingAsync(organizationId, DriverDigits, RecipientDigits));
        Assert.Equal(
            0L,
            await Adp001FakeKeyVault.CountPlaintextInAuditAndOutboxAsync(
                fixture.AdminDataSource, organizationId, [DriverDigits, RecipientDigits, "+52" + RecipientDigits]));
    }

    [PostgreSqlContractFact]
    public async Task Key_presses_bind_to_the_platforms_own_call_and_fail_closed()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed);
        var provider = new FakeVoiceProvider(request => outcome.Outcome == VoiceBridgeOutcome.Placed
            ? outcome with { ProviderCallId = SidFor(request.CallRequestId) }
            : outcome);
        await using var services = CreateServices(VoiceBridgeMode.Live, vault, provider, ManyCalls());
        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        var organizationId = scenario.OrganizationId;

        // Another call, another tenant, an unknown request or a malformed call id: nobody is dialed, nothing recorded.
        var placed = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);
        var placedSid = SidFor(placed.CallRequestId);
        foreach (var keypress in new[]
                 {
                     new VoiceKeypress(organizationId, placed.CallRequestId, OtherCallSid, true),
                     new VoiceKeypress(Guid.NewGuid(), placed.CallRequestId, placedSid, true),
                     new VoiceKeypress(organizationId, Guid.NewGuid(), placedSid, true),
                     new VoiceKeypress(organizationId, placed.CallRequestId, "CA-1", true),
                     new VoiceKeypress(Guid.Empty, placed.CallRequestId, placedSid, true),
                 })
        {
            Assert.Null((await services.Calls.ConfirmDriverAsync(keypress, default)).Recipient);
            Assert.False(await services.Calls.ConfirmRecipientAsync(keypress, default));
        }

        Assert.Null(await ReadBridgeStageAsync(placed.CallRequestId));
        // The recipient step needs the driver's own 1 first.
        Assert.False(await services.Calls.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, placed.CallRequestId, placedSid, true), default));
        Assert.Null(await ReadBridgeStageAsync(placed.CallRequestId));

        // An ambiguous request has no call id yet: the first key press binds the platform's call to it.
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Unavailable);
        var ambiguous = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-2"), default);
        Assert.Equal(RecipientCallStatuses.Unconfirmed, ambiguous.Status);
        Assert.NotNull((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, ambiguous.CallRequestId, CallSid, true), default)).Recipient);
        var bound = await ReadCallRequestAsync(ambiguous.CallRequestId);
        Assert.Equal(("UNCONFIRMED", CallSid), (bound.Status, bound.ProviderCallSid));
        Assert.False(await services.Calls.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, ambiguous.CallRequestId, OtherCallSid, true), default));
        Assert.True(await services.Calls.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, ambiguous.CallRequestId, CallSid, true), default));

        // A failed request never rings: no key press counts for it.
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected);
        await Assert.ThrowsAsync<RecipientCallUnavailableException>(() =>
            services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-3"), default));
        var failedId = await SingleRequestIdAsync(organizationId, "FAILED");
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, failedId, OtherCallSid, true), default)).Recipient);
        Assert.Null(await ReadBridgeStageAsync(failedId));

        // A key press after the window never dials.
        outcome = new VoiceBridgeResult(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed);
        var late = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-4"), default);
        services.Clock.UtcNow = Now.AddMinutes(16);
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, late.CallRequestId, SidFor(late.CallRequestId), true), default)).Recipient);
        Assert.Null(await ReadBridgeStageAsync(late.CallRequestId));

        // The recipient's number became unusable after the request: the driver's 1 hangs up as BRIDGE_FAILED.
        var unusable = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-5"), default);
        await scenario.ClearRecipientPhoneAsync();
        services.Clock.UtcNow = Now.AddMinutes(17);
        Assert.Null((await services.Calls.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, unusable.CallRequestId, SidFor(unusable.CallRequestId), true), default)).Recipient);
        Assert.Equal("BRIDGE_FAILED", await ReadBridgeStageAsync(unusable.CallRequestId));
        Assert.Equal(
            "RECIPIENT_CALL_BRIDGE_FAILED",
            (await ReadAuditsAsync(organizationId, "RecipientCallRequest")).Last().Action);
        Assert.Equal(0L, await CountRowsContainingAsync(organizationId, DriverDigits, RecipientDigits));
    }

    [PostgreSqlContractFact]
    public async Task The_synthetic_mode_never_decrypts_and_places_through_the_deterministic_fake()
    {
        var vault = new Adp001FakeKeyVault();
        await using var scenario = await VoiceScenario.CreateAsync(fixture, vault);
        var synthetic = new SyntheticVoiceBridgeProvider(Options.Create(new VoiceBridgeOptions
        {
            Provider = VoiceBridgeProviderKind.Synthetic,
        }));
        var provider = new FakeVoiceProvider(request => synthetic.PlaceCallAsync(request, default).AsTask().GetAwaiter().GetResult());
        await using var services = CreateServices(VoiceBridgeMode.Synthetic, vault, provider);

        await services.Phones.RegisterAsync(Register(scenario, DriverDigits), default);
        var stored = await ReadDriverPhoneAsync(scenario.DriverId);
        Assert.Equal(SyntheticDriverPhoneProtector.KeyVersion, stored.KeyVersion);
        Assert.Equal(32, stored.Ciphertext!.Length);

        var result = await services.Calls.RequestAsync(Call(scenario, "voice-001-idem-key-1"), default);

        Assert.Equal(RecipientCallStatuses.Placed, result.Status);
        Assert.Equal(
            (result.CallRequestId, scenario.OrganizationId, SyntheticRecipientCallPhoneResolver.DriverPlaceholder,
                SyntheticRecipientCallPhoneResolver.RecipientPlaceholder),
            Assert.Single(provider.Calls));
        var row = await ReadCallRequestAsync(result.CallRequestId);
        Assert.Equal(("SYNTHETIC", "VOICE_SYNTHETIC_PLACED"), (row.Provider, row.ResultCode));
        Assert.Matches("^SY[0-9a-f]{32}$", row.ProviderCallSid);
    }

    // ------------------------------------------------------------------ helpers

    private static RegisterDriverPhoneCommand Register(VoiceScenario scenario, string phone)
    {
        Assert.True(DriverPhonePolicy.TryNormalize(phone, out var digits));
        return new RegisterDriverPhoneCommand(
            scenario.DriverUserId,
            scenario.OrganizationId,
            digits,
            DriverPhoneConsent.CurrentVersion,
            "req-register");
    }

    private static RequestRecipientCallCommand Call(VoiceScenario scenario, string key) =>
        new(scenario.DriverUserId, scenario.OrganizationId, scenario.OrderId, key, "req-call");

    /// <summary>Room for several calls of one stop inside the order window.</summary>
    private static RecipientCallOptions ManyCalls() =>
        new() { MaximumPerOrder = 10, OrderWindowMinutes = 15, MaximumPerDriverPerHour = 20 };

    /// <summary>A distinct provider call id per request, without digits (the audit redactor masks long digit runs).</summary>
    private static string SidFor(Guid callRequestId) =>
        "CA" + new string(callRequestId.ToString("N")
            .Select(character => char.IsAsciiDigit(character) ? (char)('g' + (character - '0')) : character)
            .ToArray());

    private VoiceServices CreateServices(
        VoiceBridgeMode mode,
        Adp001FakeKeyVault vault,
        FakeVoiceProvider? provider = null,
        RecipientCallOptions? limits = null,
        Func<Guid>? newCallRequestId = null,
        Func<IRecipientCallPhoneResolver, IRecipientCallPhoneResolver>? wrapResolver = null)
    {
        var dataSource = fixture.CreateAppDataSource(maxPoolSize: 2, applicationName: "Paqueteria.VOICE001.Contract");
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(dataSource, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.EnableRetryOnFailure();
            })
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DriversDbContext(options, state);
        var transactions = new TenantTransactionContext<DriversDbContext>(context, state);
        var status = new FakeVoiceStatus(mode);
        var envelope = new PiiEnvelopeProtector(vault);
        IDriverPhoneProtector protector = mode switch
        {
            VoiceBridgeMode.Live => new EnvelopeDriverPhoneProtector(envelope),
            VoiceBridgeMode.Synthetic => new SyntheticDriverPhoneProtector(),
            _ => new DisabledDriverPhoneProtector(),
        };
        IRecipientCallPhoneResolver resolver = mode switch
        {
            VoiceBridgeMode.Live => new EnvelopeRecipientCallPhoneResolver(envelope),
            VoiceBridgeMode.Synthetic => new SyntheticRecipientCallPhoneResolver(),
            _ => new DisabledRecipientCallPhoneResolver(),
        };
        resolver = wrapResolver?.Invoke(resolver) ?? resolver;
        var auditWriter = new PostgreSqlAppendOnlyAuditWriter(state);
        var redactor = new AuditPayloadRedactor();
        var clock = new MutableClock { UtcNow = Now };
        provider ??= new FakeVoiceProvider(_ => new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, CallSid));
        var phones = new PostgreSqlDriverPhoneService(
            transactions, status, protector, auditWriter, redactor, clock,
            NullLogger<PostgreSqlDriverPhoneService>.Instance);
        var calls = new PostgreSqlRecipientCallService(
            transactions, status, provider, resolver, auditWriter, redactor, clock,
            Options.Create(new DriversOptions
            {
                Provider = DriversProviderKind.PostgreSql,
                RecipientCalls = limits ?? new RecipientCallOptions(),
            }),
            NullLogger<PostgreSqlRecipientCallService>.Instance,
            newCallRequestId);
        return new VoiceServices(dataSource, context, phones, calls, clock, envelope);
    }

    private async Task AssertRollbackBlockedAsync(string connectionString)
    {
        var blocked = await Assert.ThrowsAnyAsync<Exception>(() => RollBackToDriverPositionsAsync(connectionString));
        var postgres = blocked as PostgresException ?? blocked.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(AddDriverVoiceBridge.DowngradeBlockedMessage, postgres.MessageText);
        Assert.True(await VoiceObjectsPresentAsync(connectionString));
        Assert.Contains(
            AddDriverVoiceBridge.MigrationId,
            await ListAsync(connectionString, "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_drivers ORDER BY 1"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conname IN ('recipient_call_requests_downgrade_guard','driver_profiles_phone_downgrade_guard')"));
    }

    private static async Task RollBackToDriverPositionsAsync(string connectionString)
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
        await context.GetService<IMigrator>().MigrateAsync(AdoptCanonicalDriverPositions.MigrationId);
    }

    private static async Task<bool> VoiceObjectsPresentAsync(string connectionString) =>
        await ScalarAsync<bool>(connectionString,
            """
            SELECT to_regclass('drivers.recipient_call_requests') IS NOT NULL
               AND EXISTS (SELECT 1 FROM pg_constraint
                           WHERE conrelid='drivers.driver_profiles'::regclass AND conname='driver_profiles_phone_check')
               AND (SELECT count(*) FROM information_schema.columns
                    WHERE table_schema='drivers' AND table_name='driver_profiles'
                      AND column_name IN ('phone_ciphertext','phone_pii_key_version','phone_consent_version','phone_consented_at'))=4
            """);

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module)
            .Status;

    private static async Task<string[]> PrivilegesAsync(string connectionString)
    {
        var result = new List<string>();
        foreach (var (role, privilege) in new[]
                 {
                     ("paqueteria_app", "SELECT"), ("paqueteria_app", "INSERT"), ("paqueteria_app", "UPDATE"),
                     ("paqueteria_app", "DELETE"), ("paqueteria_app", "TRUNCATE"), ("paqueteria_worker", "SELECT"),
                     ("paqueteria_worker", "INSERT"), ("paqueteria_worker", "UPDATE"), ("paqueteria_worker", "DELETE"),
                 })
        {
            var granted = await ScalarAsync<bool>(
                connectionString,
                $"SELECT has_table_privilege('{role}','drivers.recipient_call_requests','{privilege}')");
            result.Add($"{role}:{privilege}:{granted}");
        }

        return [.. result];
    }

    private async Task<(byte[]? Ciphertext, string? KeyVersion, string? ConsentVersion, DateTimeOffset? ConsentedAt)> ReadDriverPhoneAsync(Guid driverId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT phone_ciphertext,phone_pii_key_version,phone_consent_version,phone_consented_at FROM drivers.driver_profiles WHERE id=@driver");
        command.Parameters.AddWithValue("driver", driverId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task AssertNoDriverPhoneAsync(Guid driverId)
    {
        var stored = await ReadDriverPhoneAsync(driverId);
        Assert.Null(stored.Ciphertext);
        Assert.Null(stored.KeyVersion);
        Assert.Null(stored.ConsentVersion);
        Assert.Null(stored.ConsentedAt);
    }

    private async Task<CallRequestRow> ReadCallRequestAsync(Guid id) =>
        Assert.Single(await ReadCallRequestsAsync(null, id));

    private async Task<List<CallRequestRow>> ReadCallRequestsAsync(Guid? organizationId, Guid? id = null)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT org_id,driver_id,requested_by,order_id,assignment_id,provider,status,result_code,provider_call_sid,
                   provider_call_status,call_duration_seconds,requested_at,completed_at,status_reported_at
            FROM drivers.recipient_call_requests
            WHERE (@org::uuid IS NULL OR org_id=@org) AND (@id::uuid IS NULL OR id=@id)
            ORDER BY requested_at,id
            """);
        command.Parameters.Add(new NpgsqlParameter("org", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
        var rows = new List<CallRequestRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CallRequestRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
        }

        return rows;
    }

    private async Task<string?> ReadBridgeStageAsync(Guid id)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT bridge_stage FROM drivers.recipient_call_requests WHERE id=@id");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<Guid> SingleRequestIdAsync(Guid organizationId, string status)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT id FROM drivers.recipient_call_requests WHERE org_id=@org AND status=@status");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("status", status);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return Assert.Single(ids);
    }

    private async Task<(int? Status, Guid? ResourceId)> PendingReservationAsync(Guid organizationId, string key)
    {
        var reservation = await ReadIdempotencyAsync(organizationId, key);
        Assert.Null(reservation.Body);
        return (reservation.Status, reservation.ResourceId);
    }

    private async Task<(int? Status, string? Body, Guid? ResourceId)> ReadIdempotencyAsync(Guid organizationId, string key) =>
        await TryReadIdempotencyAsync(organizationId, key) ?? throw new Xunit.Sdk.XunitException("No idempotency row.");

    private async Task<(int? Status, string? Body, Guid? ResourceId)?> TryReadIdempotencyAsync(Guid organizationId, string key)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT response_status,response_body::text,resource_id FROM platform.idempotency_keys
            WHERE owner_org_id=@org AND scope='VOICE-001:REQUEST_RECIPIENT_CALL' AND idempotency_key=@key
            """);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2))
            : null;
    }

    private async Task<List<(string Action, Guid? ActorId, Guid EntityId, string Payload)>> ReadAuditsAsync(
        Guid organizationId,
        string entityType)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT action,actor_id,entity_id,payload_redacted::text FROM platform.audit_logs
            WHERE org_id=@org AND entity_type=@type
            ORDER BY occurred_at,
                     CASE WHEN action IN ('RECIPIENT_CALL_REQUESTED','DRIVER_PHONE_REGISTERED') THEN 0 ELSE 1 END
            """);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("type", entityType);
        var audits = new List<(string, Guid?, Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            audits.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3)));
        }

        return audits;
    }

    /// <summary>Rows of the tenant's call requests and idempotency answers whose text holds a number.</summary>
    private async Task<long> CountRowsContainingAsync(Guid organizationId, params string[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT (SELECT count(*) FROM drivers.recipient_call_requests r
                        WHERE r.org_id=@org AND row_to_json(r)::text LIKE '%' || @value || '%')
                     + (SELECT count(*) FROM platform.idempotency_keys k
                        WHERE k.owner_org_id=@org AND row_to_json(k)::text LIKE '%' || @value || '%')
                """);
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("value", value);
            total += (long)(await command.ExecuteScalarAsync())!;
        }

        return total;
    }

    private async Task<long> ScalarAdminAsync(string sql, Guid organizationId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("org", organizationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

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
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed record CallRequestRow(
        Guid OrganizationId,
        Guid DriverId,
        Guid RequestedBy,
        Guid OrderId,
        Guid AssignmentId,
        string Provider,
        string Status,
        string ResultCode,
        string? ProviderCallSid,
        string? ProviderCallStatus,
        int? DurationSeconds,
        DateTimeOffset RequestedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? StatusReportedAt);

    private sealed class VoiceServices(
        NpgsqlDataSource dataSource,
        DriversDbContext context,
        PostgreSqlDriverPhoneService phones,
        PostgreSqlRecipientCallService calls,
        MutableClock clock,
        IPiiEnvelopeProtector envelope) : IAsyncDisposable
    {
        public PostgreSqlDriverPhoneService Phones { get; } = phones;

        public PostgreSqlRecipientCallService Calls { get; } = calls;

        public MutableClock Clock { get; } = clock;

        public IPiiEnvelopeProtector Envelope { get; } = envelope;

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await dataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// An order in DELIVERING owned by a fresh organization (<see cref="SyntheticOrderScenario"/>, whose user is a
    /// DISPATCHER), two ACTIVE OWN drivers of that organization, the first one holding the order's ACCEPTED assignment,
    /// and the destination's phone protected with the real Locations envelope protector.
    /// </summary>
    private sealed class VoiceScenario : IAsyncDisposable
    {
        private readonly SyntheticOrderScenario _order;

        private VoiceScenario(PostgreSqlContractFixture fixture) => _order = new SyntheticOrderScenario(fixture);

        public Guid OrganizationId => _order.OrganizationId;

        public Guid OrderId => _order.OrderId;

        public Guid DispatcherUserId => _order.UserId;

        public Guid DriverUserId { get; } = Guid.NewGuid();

        public Guid DriverId { get; } = Guid.NewGuid();

        public Guid OtherDriverUserId { get; } = Guid.NewGuid();

        public Guid OtherDriverId { get; } = Guid.NewGuid();

        public Guid AssignmentId { get; } = Guid.NewGuid();

        public static async Task<VoiceScenario> CreateAsync(PostgreSqlContractFixture fixture, Adp001FakeKeyVault vault)
        {
            var scenario = new VoiceScenario(fixture);
            await scenario._order.InitializeAsync(orderStatus: "DELIVERING");
            await scenario._order.ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status) VALUES
                  (@driver_user,@driver_subject,'ACTIVE'),(@other_user,@other_subject,'ACTIVE');
                INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
                  (gen_random_uuid(),@driver_user,@org,'DRIVER','ACTIVE',true),
                  (gen_random_uuid(),@other_user,@org,'DRIVER','ACTIVE',true);
                INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status) VALUES
                  (@driver,@driver_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE'),
                  (@other,@other_user,@org,@city,'OWN','CAR','ACTIVE');
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents,accepted_at)
                VALUES (@assignment,@order,@org,@driver,'OWN','ACCEPTED',0,clock_timestamp());
                """,
                SyntheticOrderScenario.P("driver_user", scenario.DriverUserId),
                SyntheticOrderScenario.P("driver_subject", $"voice001|{scenario.DriverUserId:N}"),
                SyntheticOrderScenario.P("other_user", scenario.OtherDriverUserId),
                SyntheticOrderScenario.P("other_subject", $"voice001|{scenario.OtherDriverUserId:N}"),
                SyntheticOrderScenario.P("org", scenario.OrganizationId),
                SyntheticOrderScenario.P("city", scenario._order.CityId),
                SyntheticOrderScenario.P("driver", scenario.DriverId),
                SyntheticOrderScenario.P("other", scenario.OtherDriverId),
                SyntheticOrderScenario.P("assignment", scenario.AssignmentId),
                SyntheticOrderScenario.P("order", scenario.OrderId));
            await scenario.ProtectRecipientPhoneAsync(vault);
            return scenario;
        }

        /// <summary>The destination's address, contact and phone as the Locations module protects them.</summary>
        public async Task ProtectRecipientPhoneAsync(Adp001FakeKeyVault vault)
        {
            var protectedValues = await new AzureKeyVaultLocationPiiProtector(new PiiEnvelopeProtector(vault)).ProtectAsync(
                new LocationPiiValues(
                    OrganizationId,
                    _order.DestinationLocationId,
                    "Calle Sintética 1, Culiacán",
                    "Destinatario Sintético",
                    RecipientDigits),
                default);
            await _order.ExecuteAdminAsync(
                """
                UPDATE locations.locations
                SET address_ciphertext=@address,contact_name_ciphertext=@contact,phone_ciphertext=@phone,pii_key_version=@version
                WHERE id=@destination;
                """,
                SyntheticOrderScenario.P("address", protectedValues.AddressTextCiphertext),
                SyntheticOrderScenario.P("contact", protectedValues.ContactNameCiphertext!),
                SyntheticOrderScenario.P("phone", protectedValues.PhoneCiphertext!),
                SyntheticOrderScenario.P("version", protectedValues.KeyVersion),
                SyntheticOrderScenario.P("destination", _order.DestinationLocationId));
        }

        public Task ClearRecipientPhoneAsync() =>
            _order.ExecuteAdminAsync(
                "UPDATE locations.locations SET phone_ciphertext=NULL WHERE id=@destination;",
                SyntheticOrderScenario.P("destination", _order.DestinationLocationId));

        public Task SetAssignmentStatusAsync(string status) =>
            _order.ExecuteAdminAsync(
                "UPDATE dispatch.assignments SET status=@status WHERE id=@assignment;",
                SyntheticOrderScenario.P("status", status),
                SyntheticOrderScenario.P("assignment", AssignmentId));

        public Task SetOrderStatusAsync(string status) =>
            _order.ExecuteAdminAsync(
                "UPDATE orders.orders SET status=@status WHERE id=@order;",
                SyntheticOrderScenario.P("status", status),
                SyntheticOrderScenario.P("order", OrderId));

        public Task SetDriverProfileStatusAsync(string status) =>
            _order.ExecuteAdminAsync(
                "UPDATE drivers.driver_profiles SET status=@status WHERE id=@driver;",
                SyntheticOrderScenario.P("status", status),
                SyntheticOrderScenario.P("driver", DriverId));

        public async ValueTask DisposeAsync()
        {
            await _order.ExecuteAdminAsync(
                "DELETE FROM drivers.recipient_call_requests WHERE org_id=@org;",
                SyntheticOrderScenario.P("org", OrganizationId));
            await _order.DisposeAsync();
            await _order.ExecuteAdminAsync(
                "DELETE FROM identity.users WHERE id IN (@driver_user,@other_user);",
                SyntheticOrderScenario.P("driver_user", DriverUserId),
                SyntheticOrderScenario.P("other_user", OtherDriverUserId));
        }
    }

    private sealed class FakeVoiceStatus(VoiceBridgeMode mode) : IVoiceBridgeStatus
    {
        public VoiceBridgeMode Mode { get; } = mode;

        public string ProviderName => Mode switch
        {
            VoiceBridgeMode.Live => "TWILIO",
            VoiceBridgeMode.Synthetic => "SYNTHETIC",
            _ => "DISABLED",
        };
    }

    /// <param name="beforeAnswer">Runs while the provider "holds" the request, before it answers (for example a status
    /// callback that arrives first).</param>
    private sealed class FakeVoiceProvider(
        Func<VoiceBridgeRequest, VoiceBridgeResult> respond,
        Func<VoiceBridgeRequest, Task>? beforeAnswer = null) : IVoiceBridgeProvider
    {
        public List<(Guid CallRequestId, Guid OrganizationId, string Driver, string Recipient)> Calls { get; } = [];

        public async ValueTask<VoiceBridgeResult> PlaceCallAsync(VoiceBridgeRequest request, CancellationToken cancellationToken)
        {
            Calls.Add((request.CallRequestId, request.OrganizationId, request.Driver.E164, request.Recipient.E164));
            if (beforeAnswer is not null)
            {
                await beforeAnswer(request);
            }

            return respond(request);
        }
    }

    private async Task<Dictionary<string, string?>> ReadAuditRequestIdsAsync(Guid organizationId, Guid callRequestId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT action,request_id FROM platform.audit_logs
            WHERE org_id=@org AND entity_type='RecipientCallRequest' AND entity_id=@id
            """);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("id", callRequestId);
        var requestIds = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            requestIds.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        return requestIds;
    }

    /// <summary>
    /// The production webhook routes (<see cref="VoiceWebhookEndpoints"/>) on a loopback Kestrel, behind the API's
    /// <c>UseTraceCorrelation</c> and with its log correlation (<c>AddTraceCorrelation</c>), over the real call service.
    /// The verifier accepts every request: the signature is not under test here.
    /// </summary>
    private sealed class VoiceWebhookHost : IAsyncDisposable
    {
        internal const string BridgeTwiml = "<Response><Dial>synthetic</Dial></Response>";
        internal const string ConnectTwiml = "<Response></Response>";
        private const string HangupTwiml = "<Response><Hangup/></Response>";

        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private VoiceWebhookHost(WebApplication app, HttpClient client)
        {
            _app = app;
            _client = client;
        }

        internal static async Task<VoiceWebhookHost> StartAsync(IRecipientCallService calls, ILoggerProvider logs)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddTraceCorrelation();
            builder.Services.AddSingleton<IVoiceBridgeStatus>(new FakeVoiceStatus(VoiceBridgeMode.Live));
            builder.Services.AddSingleton<IVoiceWebhookVerifier, AcceptingVerifier>();
            builder.Services.AddSingleton(calls);
            var app = builder.Build();
            app.UseTraceCorrelation();
            app.MapVoiceWebhookEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new VoiceWebhookHost(app, new HttpClient { BaseAddress = new Uri(address, UriKind.Absolute) });
        }

        internal async Task<string> PostAsync(
            string path,
            Guid organizationId,
            Guid callRequestId,
            HttpStatusCode expected,
            params (string Name, string Value)[] fields)
        {
            using var content = new FormUrlEncodedContent(
                fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
            using var response = await _client.PostAsync(
                VoiceWebhookPaths.PathAndQuery(path, organizationId, callRequestId), content);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(expected == response.StatusCode, $"{path}: {(int)response.StatusCode} {body}");
            return body;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed class AcceptingVerifier : IVoiceWebhookVerifier
        {
            public VoiceWebhookVerdict Verify(VoiceWebhookRequest request) => VoiceWebhookVerdict.Verified;

            public string BuildInboundCallResponse() => HangupTwiml;

            public string BuildBridgeResponse(VoicePhoneNumber recipient, Guid organizationId, Guid callRequestId) =>
                BridgeTwiml;

            public string BuildRecipientScreenResponse(Guid organizationId, Guid callRequestId) => HangupTwiml;

            public string BuildConnectResponse() => ConnectTwiml;

            public string BuildHangupResponse() => HangupTwiml;
        }
    }

    /// <summary>The <c>TraceId</c> of each hosting "Request starting" line, by request path.</summary>
    private sealed class RequestStartCapture : ILoggerProvider
    {
        private const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
        private const int RequestStartingEventId = 1;
        private readonly ConcurrentQueue<(string Path, string? TraceId)> _starts = new();

        internal string? TraceIdOf(string path) => Assert.Single(_starts, start => start.Path == path).TraceId;

        public ILogger CreateLogger(string categoryName) =>
            categoryName == HostingCategory ? new Logger(_starts) : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(string Path, string? TraceId)> starts) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != RequestStartingEventId || state is not IEnumerable<KeyValuePair<string, object?>> properties)
                {
                    return;
                }

                string? path = null;
                string? traceId = null;
                foreach (var property in properties)
                {
                    if (property.Key == "Path")
                    {
                        path = property.Value?.ToString();
                    }
                    else if (property.Key == TraceCorrelation.LogProperty)
                    {
                        traceId = property.Value as string;
                    }
                }

                if (path is not null)
                {
                    starts.Enqueue((path, traceId));
                }
            }
        }
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    /// <summary>
    /// Stops the attempt right after transaction 1 committed, before the provider is called (as a lost commit answer or a
    /// process stop would), until <see cref="Crash"/> is cleared.
    /// </summary>
    private sealed class CrashOnceResolver(IRecipientCallPhoneResolver inner) : IRecipientCallPhoneResolver
    {
        public bool Crash { get; set; } = true;

        public Task<(VoicePhoneNumber Driver, VoicePhoneNumber Recipient)> ResolveAsync(
            RecipientCallProtectedPhones phones,
            CancellationToken cancellationToken) =>
            Crash
                ? throw new InvalidOperationException("Synthetic stop after transaction 1.")
                : inner.ResolveAsync(phones, cancellationToken);

        public Task<VoicePhoneNumber> ResolveRecipientAsync(ProtectedRecipientPhone phone, CancellationToken cancellationToken) =>
            inner.ResolveRecipientAsync(phone, cancellationToken);
    }
}
