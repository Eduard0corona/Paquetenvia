using System.Text;
using Dispatch.Application.Stops;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Stops;
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
using Paqueteria.Application.Security;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Security;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// REG-002 (REG-JOIN-EXISTING-BY-EMAIL and its owner decisions) against real PostgreSQL: the table and
/// executor boundary, the role ceiling, idempotent add and re-arm, seven-day expiry, renew and revoke,
/// acceptance in several organizations exactly once under concurrency, cross-tenant invisibility, the
/// DRIVER without a profile, and the Organizations lane up and down. Every test uses its own users,
/// organizations and emails, so the shared database needs no cleanup.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class PendingMembershipPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Table_runtime_privileges_and_functions_match_the_REG002_contract()
    {
        Assert.Equal("t|t", await ScalarAsync<string>(
            """
            SELECT concat_ws('|',relrowsecurity,relforcerowsecurity) FROM pg_class
            WHERE oid='organizations.pending_memberships'::regclass
            """));
        Assert.Equal("paqueteria_app:SELECT,paqueteria_migrator:DELETE,paqueteria_migrator:INSERT," +
            "paqueteria_migrator:REFERENCES,paqueteria_migrator:SELECT,paqueteria_migrator:TRIGGER," +
            "paqueteria_migrator:TRUNCATE,paqueteria_migrator:UPDATE", await ScalarAsync<string>(
            """
            SELECT string_agg(grantee || ':' || privilege_type, ',' ORDER BY grantee, privilege_type)
            FROM information_schema.table_privileges
            WHERE table_schema='organizations' AND table_name='pending_memberships'
            """));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) acl
            WHERE a.attrelid='organizations.pending_memberships'::regclass
              AND acl.grantee IN ('paqueteria_app'::regrole,'paqueteria_worker'::regrole)
            """));
        foreach (var signature in AddPendingMemberships.OwnedFunctions)
        {
            Assert.Equal(
                "paqueteria_registration_executor|t|f|f|t",
                await ScalarAsync<string>(
                    """
                    SELECT concat_ws('|',pg_get_userbyid(p.proowner),p.prosecdef,
                      has_function_privilege('public',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_app',p.oid,'EXECUTE'))
                    FROM pg_proc p WHERE p.oid=@function::regprocedure
                    """,
                    ("function", signature)));
        }

        // paqueteria_app can read under RLS only; every direct write is refused.
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        foreach (var statement in new[]
        {
            $"INSERT INTO organizations.pending_memberships(id,organization_id,email_hmac,email_hmac_key_version,role,status,invited_by,created_at,expires_at) VALUES (gen_random_uuid(),'{organization:D}',decode(repeat('00',32),'hex'),1,'VIEWER','PENDING','{admin:D}',now(),now()+interval '1 day')",
            "UPDATE organizations.pending_memberships SET status='REVOKED'",
            "DELETE FROM organizations.pending_memberships",
        })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => AsAppAsync(admin, organization, statement));
            Assert.Equal("42501", denied.SqlState);
        }

        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertRegistrationExecutorInstalledAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Add_answers_the_same_for_any_email_replays_by_key_and_re_arms_a_pending_entry()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        var service = PendingService();

        // An email that already belongs to a signed-up person and one nobody has used: same outcome, same shape.
        var existing = await NewUserAsync();
        var known = await service.AddAsync(Add(admin, organization, "reg002-known-000001", Email("known"), "VIEWER"), CancellationToken.None);
        var unknown = await service.AddAsync(Add(admin, organization, "reg002-unknown-00001", Email("unknown"), "VIEWER"), CancellationToken.None);
        Assert.Equal(PendingMembershipOutcome.Succeeded, known.Outcome);
        Assert.Equal(PendingMembershipOutcome.Succeeded, unknown.Outcome);
        Assert.Equal(("VIEWER", "PENDING"), (known.Entry!.Role, known.Entry.Status));
        Assert.Equal(("VIEWER", "PENDING"), (unknown.Entry!.Role, unknown.Entry.Status));
        Assert.Equal(TimeSpan.FromDays(7), known.Entry.ExpiresAt - known.Entry.CreatedAt);
        Assert.NotEqual(Guid.Empty, existing);

        // Nothing identifying is stored: no column holds the address, and the HMAC is 32 bytes.
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM organizations.pending_memberships
            WHERE organization_id=@org AND (position(convert_to('reg002','UTF8') in email_hmac)>0 OR octet_length(email_hmac)<>32)
            """,
            ("org", organization)));

        // The same key and request replays; the same key with another request conflicts.
        var command = Add(admin, organization, "reg002-replay-000001", Email("replay"), "BUSINESS_OPERATOR");
        var first = await service.AddAsync(command, CancellationToken.None);
        var replay = await service.AddAsync(command, CancellationToken.None);
        var conflict = await service.AddAsync(command with { Role = "VIEWER" }, CancellationToken.None);
        Assert.Equal(first.Entry, replay.Entry);
        Assert.Equal(PendingMembershipOutcome.IdempotencyConflict, conflict.Outcome);
        Assert.Equal(
            PostgreSqlPendingMembershipService.DerivePendingMembershipId(admin, organization, command.IdempotencyKey),
            first.Entry!.Id);

        // Re-adding the same email and role while pending re-arms that entry: one row, a later expiry.
        await AgeAsync(first.Entry.Id, TimeSpan.FromDays(3));
        var rearmCommand = Add(admin, organization, $"reg002-rearm-{Guid.NewGuid():N}", command.Email, command.Role);
        var rearmed = await service.AddAsync(rearmCommand, CancellationToken.None);
        Assert.Equal(PendingMembershipOutcome.Succeeded, rearmed.Outcome);
        Assert.Equal(first.Entry.Id, rearmed.Entry!.Id);
        Assert.True(rearmed.Entry.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6.9));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM organizations.pending_memberships WHERE organization_id=@org AND role='BUSINESS_OPERATOR'",
            ("org", organization)));
        Assert.Equal("PENDING_MEMBERSHIP_ADDED,PENDING_MEMBERSHIP_RENEWED", await ScalarAsync<string>(
            "SELECT string_agg(action, ',' ORDER BY occurred_at) FROM platform.audit_logs WHERE entity_id=@id",
            ("id", first.Entry.Id)));
        // A repeated re-arm key re-arms nothing more and writes no second audit row.
        await service.AddAsync(rearmCommand, CancellationToken.None);
        Assert.Equal(2, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE entity_id=@id", ("id", first.Entry.Id)));
        Assert.Equal("{\"role\": \"BUSINESS_OPERATOR\"}", await ScalarAsync<string>(
            "SELECT max(payload_redacted::text) FROM platform.audit_logs WHERE entity_id=@id", ("id", first.Entry.Id)));
    }

    /// <summary>
    /// REG-ROLE-CEILING and REG-PLATFORM-ADMIN-ADDS, every role against every kind of actor: the functions
    /// refuse outside the ceiling even when the caller skips the endpoint.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task The_role_ceiling_is_enforced_by_the_database()
    {
        var service = PendingService();
        foreach (var role in PendingMembershipLimits.Roles.Order(StringComparer.Ordinal))
        {
            foreach (var (organizationType, actorRole, allowed) in new[]
            {
                ("ALLY", "ALLY_ADMIN", role is "ALLY_ADMIN" or "ALLY_OPERATOR" or "DRIVER" or "VIEWER"),
                ("BUSINESS", "BUSINESS_ADMIN", role is "BUSINESS_ADMIN" or "BUSINESS_OPERATOR" or "VIEWER"),
                ("PLATFORM", "PLATFORM_ADMIN", true),
                ("BUSINESS", "PLATFORM_ADMIN", role != "PLATFORM_ADMIN"),
                ("BUSINESS", "DISPATCHER", false),
                ("ALLY", "VIEWER", false),
            })
            {
                var actor = await NewUserAsync();
                var organization = await NewOrganizationAsync(organizationType, (actor, actorRole));
                var result = await service.AddAsync(
                    Add(actor, organization, $"reg002-ceiling-{Guid.NewGuid():N}", Email("ceiling"), role),
                    CancellationToken.None);

                var label = $"{actorRole} in {organizationType} adding {role}";
                Assert.True(
                    (allowed ? PendingMembershipOutcome.Succeeded : PendingMembershipOutcome.Forbidden) == result.Outcome,
                    $"{label}: {result.Outcome}");
                Assert.True(
                    (allowed ? 1 : 0) == await ScalarAsync<long>(
                        "SELECT count(*) FROM organizations.pending_memberships WHERE organization_id=@org",
                        ("org", organization)),
                    label);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task Inactive_admins_and_inactive_organizations_cannot_add()
    {
        var service = PendingService();
        var suspended = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (suspended, "BUSINESS_ADMIN"));
        await ExecuteAdminAsync("UPDATE identity.users SET status='SUSPENDED' WHERE id=@id", ("id", suspended));
        Assert.Equal(PendingMembershipOutcome.Forbidden, (await service.AddAsync(
            Add(suspended, organization, "reg002-suspended-0001", Email("s"), "VIEWER"), CancellationToken.None)).Outcome);

        var admin = await NewUserAsync();
        var closed = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        await ExecuteAdminAsync("UPDATE organizations.organizations SET status='SUSPENDED' WHERE id=@id", ("id", closed));
        Assert.Equal(PendingMembershipOutcome.Forbidden, (await service.AddAsync(
            Add(admin, closed, "reg002-closed-org-001", Email("c"), "VIEWER"), CancellationToken.None)).Outcome);

        // An administrator of another organization is not an administrator here.
        var outsider = await NewUserAsync();
        await NewOrganizationAsync("BUSINESS", (outsider, "BUSINESS_ADMIN"));
        Assert.Equal(PendingMembershipOutcome.Forbidden, (await service.AddAsync(
            Add(outsider, organization, "reg002-outsider-0001", Email("o"), "VIEWER"), CancellationToken.None)).Outcome);
    }

    [PostgreSqlContractFact]
    public async Task Entries_expire_after_seven_days_stay_inert_and_renew_re_arms_them()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        var service = PendingService();
        var email = Email("expiry");
        var entry = (await service.AddAsync(Add(admin, organization, "reg002-expiry-000001", email, "VIEWER"),
            CancellationToken.None)).Entry!;

        await AgeAsync(entry.Id, TimeSpan.FromDays(8));
        var (subject, user) = await NewSubjectAsync();
        Assert.Equal(0, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None));
        Assert.Equal(0, await MembershipCountAsync(user, organization));
        Assert.Equal("PENDING", await EntryStatusAsync(entry.Id));

        var renewed = await service.RenewAsync(Action(admin, organization, entry.Id, "reg002-renew-0000001"), CancellationToken.None);
        Assert.Equal(PendingMembershipOutcome.Succeeded, renewed.Outcome);
        Assert.True(renewed.Entry!.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6.9));
        var replayed = await service.RenewAsync(Action(admin, organization, entry.Id, "reg002-renew-0000001"), CancellationToken.None);
        Assert.Equal(renewed.Entry.ExpiresAt, replayed.Entry!.ExpiresAt);

        Assert.Equal(1, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None));
        Assert.Equal(1, await MembershipCountAsync(user, organization));
        Assert.Equal(PendingMembershipOutcome.NotPending, (await service.RenewAsync(
            Action(admin, organization, entry.Id, "reg002-renew-0000002"), CancellationToken.None)).Outcome);
    }

    [PostgreSqlContractFact]
    public async Task Revoked_entries_are_never_applied_and_revoke_is_idempotent()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("ALLY", (admin, "ALLY_ADMIN"));
        var service = PendingService();
        var email = Email("revoke");
        var entry = (await service.AddAsync(Add(admin, organization, "reg002-revoke-000001", email, "ALLY_OPERATOR"),
            CancellationToken.None)).Entry!;

        var revoked = await service.RevokeAsync(Action(admin, organization, entry.Id, "reg002-revoke-key-01"), CancellationToken.None);
        var again = await service.RevokeAsync(Action(admin, organization, entry.Id, "reg002-revoke-key-02"), CancellationToken.None);
        Assert.Equal("REVOKED", revoked.Entry!.Status);
        Assert.Equal("REVOKED", again.Entry!.Status);
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE entity_id=@id AND action='PENDING_MEMBERSHIP_REVOKED'",
            ("id", entry.Id)));
        Assert.Equal(PendingMembershipOutcome.NotPending, (await service.RenewAsync(
            Action(admin, organization, entry.Id, "reg002-revoke-key-03"), CancellationToken.None)).Outcome);

        var (subject, user) = await NewSubjectAsync();
        Assert.Equal(0, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None));
        Assert.Equal(0, await MembershipCountAsync(user, organization));

        // An accepted entry already became a membership: revoking it is a conflict and changes nothing.
        var accepted = (await service.AddAsync(Add(admin, organization, "reg002-revoke-000002", email, "VIEWER"),
            CancellationToken.None)).Entry!;
        Assert.Equal(1, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None));
        Assert.Equal(PendingMembershipOutcome.NotPending, (await service.RevokeAsync(
            Action(admin, organization, accepted.Id, "reg002-revoke-key-04"), CancellationToken.None)).Outcome);
        Assert.Equal("ACCEPTED", await EntryStatusAsync(accepted.Id));
    }

    [PostgreSqlContractFact]
    public async Task Sign_in_accepts_every_matching_organization_and_ignores_other_emails()
    {
        var service = PendingService();
        var email = Email("multi");
        var businessAdmin = await NewUserAsync();
        var business = await NewOrganizationAsync("BUSINESS", (businessAdmin, "BUSINESS_ADMIN"));
        var allyAdmin = await NewUserAsync();
        var ally = await NewOrganizationAsync("ALLY", (allyAdmin, "ALLY_ADMIN"));
        var closedAdmin = await NewUserAsync();
        var closed = await NewOrganizationAsync("BUSINESS", (closedAdmin, "BUSINESS_ADMIN"));
        await service.AddAsync(Add(businessAdmin, business, "reg002-multi-0000001", email, "BUSINESS_OPERATOR"), CancellationToken.None);
        await service.AddAsync(Add(allyAdmin, ally, "reg002-multi-0000002", email, "DRIVER"), CancellationToken.None);
        await service.AddAsync(Add(closedAdmin, closed, "reg002-multi-0000003", email, "VIEWER"), CancellationToken.None);
        await ExecuteAdminAsync("UPDATE organizations.organizations SET status='CLOSED' WHERE id=@id", ("id", closed));

        // The normalization is shared: case, surrounding spaces and NFC decomposition are the same address.
        var decorated = "  " + email.ToUpperInvariant() + " ";
        var (subject, user) = await NewSubjectAsync();
        Assert.Equal(0, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, Email("other"), CancellationToken.None));
        Assert.Equal(2, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, decorated, CancellationToken.None));

        Assert.Equal("BUSINESS_OPERATOR|ACTIVE", await MembershipAsync(user, business));
        Assert.Equal("DRIVER|ACTIVE", await MembershipAsync(user, ally));
        Assert.Equal(0, await MembershipCountAsync(user, closed));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM organizations.organization_memberships WHERE user_id=@user AND is_default",
            ("user", user)));
        Assert.Equal(2, await ScalarAsync<long>(
            """
            SELECT count(*) FROM platform.audit_logs
            WHERE actor_id=@user AND action='PENDING_MEMBERSHIP_ACCEPTED' AND org_id IN (@business,@ally)
            """,
            ("user", user), ("business", business), ("ally", ally)));
        var context = await ScalarAsync<string>(
            "SELECT security.resolve_identity_context(@subject)::text", ("subject", subject));
        Assert.Contains(business.ToString("D"), context);
        Assert.Contains(ally.ToString("D"), context);
    }

    [PostgreSqlContractFact]
    public async Task Normalization_matches_case_spacing_and_unicode_composition()
    {
        Assert.True(EmailLookupNormalizer.TryNormalize("  José@Example.COM ", out var composed));
        Assert.True(EmailLookupNormalizer.TryNormalize("José@example.com", out var decomposed));
        Assert.Equal("josé@example.com", composed);
        Assert.Equal(composed, decomposed);
        Assert.False(EmailLookupNormalizer.TryNormalize("no-at-sign", out _));
        Assert.False(EmailLookupNormalizer.TryNormalize("a@b@c", out _));
        Assert.False(EmailLookupNormalizer.TryNormalize("a b@c", out _));

        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        var local = $"reg002-{Guid.NewGuid():N}";
        await PendingService().AddAsync(
            Add(admin, organization, "reg002-unicode-00001", $"{local}é@Example.com", "VIEWER"), CancellationToken.None);
        var (subject, user) = await NewSubjectAsync();
        Assert.Equal(1, await IdentityRegistration().ApplyPendingMembershipsAsync(
            subject, $" {local.ToUpperInvariant()}É@EXAMPLE.COM", CancellationToken.None));
        Assert.Equal(1, await MembershipCountAsync(user, organization));
    }

    [PostgreSqlContractFact]
    public async Task An_existing_user_gets_the_membership_at_the_next_sign_in_and_a_new_user_at_the_first()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        var service = PendingService();
        var registration = IdentityRegistration();

        // Existing account: it signed up before the entry existed.
        var (existingSubject, existingUser) = await NewSubjectAsync();
        var existingEmail = Email("existing");
        await service.AddAsync(Add(admin, organization, "reg002-existing-0001", existingEmail, "VIEWER"), CancellationToken.None);
        await registration.RegisterAsync(existingSubject, CancellationToken.None);
        Assert.Equal(1, await registration.ApplyPendingMembershipsAsync(existingSubject, existingEmail, CancellationToken.None));
        Assert.Equal("VIEWER|ACTIVE", await MembershipAsync(existingUser, organization));

        // New account: the first sign-in creates the user and applies the entry.
        var newSubject = $"reg002-new-{Guid.NewGuid():N}";
        var newEmail = Email("new");
        await service.AddAsync(Add(admin, organization, "reg002-new-user-0001", newEmail, "BUSINESS_ADMIN"), CancellationToken.None);
        await registration.RegisterAsync(newSubject, CancellationToken.None);
        Assert.Equal(1, await registration.ApplyPendingMembershipsAsync(newSubject, newEmail, CancellationToken.None));
        var newUser = await ScalarAsync<Guid>("SELECT id FROM identity.users WHERE identity_subject=@s", ("s", newSubject));
        Assert.Equal("BUSINESS_ADMIN|ACTIVE", await MembershipAsync(newUser, organization));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM organizations.organization_memberships WHERE user_id=@u AND is_default", ("u", newUser)));

        // A SUSPENDED user never receives a membership.
        var (suspendedSubject, suspendedUser) = await NewSubjectAsync();
        var suspendedEmail = Email("suspended");
        await service.AddAsync(Add(admin, organization, "reg002-suspended-002", suspendedEmail, "VIEWER"), CancellationToken.None);
        await ExecuteAdminAsync("UPDATE identity.users SET status='SUSPENDED' WHERE id=@id", ("id", suspendedUser));
        Assert.Equal(0, await registration.ApplyPendingMembershipsAsync(suspendedSubject, suspendedEmail, CancellationToken.None));
        Assert.Equal(0, await MembershipCountAsync(suspendedUser, organization));
    }

    [PostgreSqlContractFact]
    public async Task An_identical_membership_consumes_the_entry_without_a_duplicate_or_a_reactivation()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        var service = PendingService();
        var (subject, user) = await NewSubjectAsync();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organization_memberships(user_id,organization_id,role,status) VALUES (@u,@o,'VIEWER','SUSPENDED')",
            ("u", user), ("o", organization));
        var email = Email("identical");
        var entry = (await service.AddAsync(Add(admin, organization, "reg002-identical-001", email, "VIEWER"),
            CancellationToken.None)).Entry!;

        Assert.Equal(1, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None));
        Assert.Equal("VIEWER|SUSPENDED", await MembershipAsync(user, organization));
        Assert.Equal(1, await MembershipCountAsync(user, organization));
        Assert.Equal("ACCEPTED", await EntryStatusAsync(entry.Id));
        Assert.Equal("{\"role\": \"VIEWER\", \"membership_existed\": true}", await ScalarAsync<string>(
            "SELECT payload_redacted::text FROM platform.audit_logs WHERE entity_id=@id AND action='PENDING_MEMBERSHIP_ACCEPTED'",
            ("id", entry.Id)));
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_sign_ins_apply_each_entry_exactly_once()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("BUSINESS", (admin, "BUSINESS_ADMIN"));
        var email = Email("race");
        var entry = (await PendingService().AddAsync(Add(admin, organization, "reg002-race-00000001", email, "VIEWER"),
            CancellationToken.None)).Entry!;
        var (subject, user) = await NewSubjectAsync();

        var accepted = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None).AsTask()));

        Assert.Equal(1, accepted.Sum());
        Assert.Equal(1, await MembershipCountAsync(user, organization));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE entity_id=@id AND action='PENDING_MEMBERSHIP_ACCEPTED'",
            ("id", entry.Id)));
    }

    [PostgreSqlContractFact]
    public async Task Each_organization_sees_and_changes_only_its_own_entries()
    {
        var service = PendingService();
        var adminA = await NewUserAsync();
        var organizationA = await NewOrganizationAsync("BUSINESS", (adminA, "BUSINESS_ADMIN"));
        var adminB = await NewUserAsync();
        var organizationB = await NewOrganizationAsync("BUSINESS", (adminB, "BUSINESS_ADMIN"));
        var email = Email("tenant");
        var entryA = (await service.AddAsync(Add(adminA, organizationA, "reg002-tenant-000001", email, "VIEWER"), CancellationToken.None)).Entry!;
        var entryB = (await service.AddAsync(Add(adminB, organizationB, "reg002-tenant-000002", email, "VIEWER"), CancellationToken.None)).Entry!;
        Assert.NotEqual(entryA.Id, entryB.Id);

        var listA = await service.ListAsync(adminA, organizationA, 200, CancellationToken.None);
        Assert.Equal([entryA.Id], listA.Select(entry => entry.Id));
        Assert.Equal(entryA, listA.Single());

        // Through RLS, organization A's context reads nothing of B even when asking for it by id.
        Assert.Equal(0, await AsAppScalarAsync<long>(adminA, organizationA,
            $"SELECT count(*) FROM organizations.pending_memberships WHERE id='{entryB.Id:D}'"));
        Assert.Equal(PendingMembershipOutcome.NotFound, (await service.RenewAsync(
            Action(adminA, organizationA, entryB.Id, "reg002-tenant-key-01"), CancellationToken.None)).Outcome);
        Assert.Equal(PendingMembershipOutcome.NotFound, (await service.RevokeAsync(
            Action(adminA, organizationA, entryB.Id, "reg002-tenant-key-02"), CancellationToken.None)).Outcome);
        Assert.Equal(PendingMembershipOutcome.Forbidden, (await service.RevokeAsync(
            Action(adminA, organizationB, entryB.Id, "reg002-tenant-key-03"), CancellationToken.None)).Outcome);
        Assert.Equal("PENDING", await EntryStatusAsync(entryB.Id));
    }

    [PostgreSqlContractFact]
    public async Task A_driver_added_by_email_gets_nothing_operational_until_its_profile_exists()
    {
        var admin = await NewUserAsync();
        var organization = await NewOrganizationAsync("ALLY", (admin, "ALLY_ADMIN"));
        var email = Email("driver");
        await PendingService().AddAsync(Add(admin, organization, "reg002-driver-000001", email, "DRIVER"), CancellationToken.None);
        var (subject, user) = await NewSubjectAsync();

        Assert.Equal(1, await IdentityRegistration().ApplyPendingMembershipsAsync(subject, email, CancellationToken.None));
        Assert.Equal("DRIVER|ACTIVE", await MembershipAsync(user, organization));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM drivers.driver_profiles WHERE user_id=@u", ("u", user)));

        // REG-DRIVER-PROFILE-LATER: driver operations resolve the ACTIVE driver profile first and refuse without it.
        await Assert.ThrowsAsync<DriverStopsForbiddenException>(() =>
            StopsQuery().ListCurrentDriverStopsAsync(user, organization, CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task Organizations_lane_rolls_back_REG002_and_reapplies_adopting_or_creating_the_table()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("reg002updown");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(9, await ExecutorFunctionsAsync(connectionString));

            // Down to REG-001: only the four REG-002 functions go; the table and its rows stay.
            await ExecuteAsync(connectionString, """
                INSERT INTO identity.users(id,identity_subject) VALUES ('0c0c0c0c-0000-0000-0000-000000000001','reg002-down');
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES ('0c0c0c0c-0000-0000-0000-000000000002','D','D','BUSINESS');
                INSERT INTO organizations.pending_memberships(id,organization_id,email_hmac,email_hmac_key_version,role,status,invited_by,created_at,expires_at)
                VALUES ('0c0c0c0c-0000-0000-0000-000000000003','0c0c0c0c-0000-0000-0000-000000000002',decode(repeat('ab',32),'hex'),1,'VIEWER','PENDING','0c0c0c0c-0000-0000-0000-000000000001',now(),now()+interval '7 days');
                """);
            await MigrateOrganizationsAsync(connectionString, AddSelfServiceRegistration.MigrationId);
            Assert.Equal(5, await ExecutorFunctionsAsync(connectionString));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM organizations.pending_memberships WHERE role='VIEWER' AND status='PENDING'"));

            // Up again adopts the canonical table with its rows.
            await MigrateOrganizationsAsync(connectionString, null);
            Assert.Equal(9, await ExecutorFunctionsAsync(connectionString));
            await AssertInstalledAsync(connectionString);

            // An installation that predates REG-002 entirely: the lane creates the table exactly as AI-06 does.
            await MigrateOrganizationsAsync(connectionString, AddSelfServiceRegistration.MigrationId);
            await ExecuteAsync(connectionString, "DROP TABLE organizations.pending_memberships;");
            await MigrateOrganizationsAsync(connectionString, null);
            Assert.Equal(9, await ExecutorFunctionsAsync(connectionString));
            Assert.Equal("t|t|pending_memberships_tenant", await ScalarAsync<string>(connectionString,
                """
                SELECT concat_ws('|',c.relrowsecurity,c.relforcerowsecurity,p.polname)
                FROM pg_class c JOIN pg_policy p ON p.polrelid=c.oid
                WHERE c.oid='organizations.pending_memberships'::regclass
                """));
            await AssertInstalledAsync(connectionString);

            // A table whose shape differs is refused, and nothing is installed.
            await MigrateOrganizationsAsync(connectionString, AddSelfServiceRegistration.MigrationId);
            await ExecuteAsync(connectionString, "ALTER TABLE organizations.pending_memberships ADD COLUMN email text;");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => MigrateOrganizationsAsync(connectionString, null));
            Assert.Contains("do not match the canonical AI-06 contract", refused.MessageText, StringComparison.Ordinal);
            Assert.Equal(5, await ExecutorFunctionsAsync(connectionString));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static async Task AssertInstalledAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertRegistrationExecutorInstalledAsync(connection, transaction);
        await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    private static Task<long> ExecutorFunctionsAsync(string connectionString) => ScalarAsync<long>(connectionString,
        "SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole");

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

    private PostgreSqlPendingMembershipService PendingService()
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        return new PostgreSqlPendingMembershipService(
            fixture.AppDataSource,
            Options.Create(new TenancyOptions { Provider = TenancyProviderKind.PostgreSql, CommandTimeoutSeconds = 30 }),
            EmailLookupTestHasher.Create(),
            new TenantTransactionContext<OrganizationsDbContext>(new OrganizationsDbContext(dbOptions, state), state),
            NullLogger<PostgreSqlPendingMembershipService>.Instance);
    }

    private PostgreSqlDriverStopsQuery StopsQuery()
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        return new PostgreSqlDriverStopsQuery(
            new TenantTransactionContext<DispatchDbContext>(new DispatchDbContext(dbOptions, state), state),
            NullLogger<PostgreSqlDriverStopsQuery>.Instance);
    }

    private PostgreSqlIdentityRegistration IdentityRegistration() => new(
        fixture.AppDataSource,
        Options.Create(new IdentityBootstrapOptions
        {
            Provider = IdentityBootstrapProviderKind.PostgreSql,
            CommandTimeoutSeconds = 30,
        }),
        EmailLookupTestHasher.Create(),
        NullLogger<PostgreSqlIdentityRegistration>.Instance);

    private static AddPendingMembershipCommand Add(Guid actor, Guid organization, string key, string email, string role) =>
        new(actor, organization, key, email, role, "reg002-contract");

    private static PendingMembershipActionCommand Action(Guid actor, Guid organization, Guid entry, string key) =>
        new(actor, organization, entry, key, "reg002-contract");

    private static string Email(string label) => $"reg002-{label}-{Guid.NewGuid():N}@example.com";

    private Task AgeAsync(Guid entryId, TimeSpan age) => ExecuteAdminAsync(
        """
        UPDATE organizations.pending_memberships
        SET created_at=created_at-@age, expires_at=expires_at-@age
        WHERE id=@id
        """,
        ("id", entryId), ("age", age));

    private Task<string> EntryStatusAsync(Guid entryId) => ScalarAsync<string>(
        "SELECT status FROM organizations.pending_memberships WHERE id=@id", ("id", entryId));

    private Task<long> MembershipCountAsync(Guid user, Guid organization) => ScalarAsync<long>(
        "SELECT count(*) FROM organizations.organization_memberships WHERE user_id=@u AND organization_id=@o",
        ("u", user), ("o", organization));

    private Task<string> MembershipAsync(Guid user, Guid organization) => ScalarAsync<string>(
        "SELECT string_agg(role || '|' || status, ',') FROM organizations.organization_memberships WHERE user_id=@u AND organization_id=@o",
        ("u", user), ("o", organization));

    private async Task<Guid> NewUserAsync() => (await NewSubjectAsync()).User;

    private async Task<(string Subject, Guid User)> NewSubjectAsync()
    {
        var user = Guid.NewGuid();
        var subject = $"reg002-{user:N}";
        await ExecuteAdminAsync(
            "INSERT INTO identity.users(id,identity_subject) VALUES (@id,@subject)", ("id", user), ("subject", subject));
        return (subject, user);
    }

    private async Task<Guid> NewOrganizationAsync(string type, params (Guid User, string Role)[] members)
    {
        var organization = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'R2','R2',@type)",
            ("id", organization), ("type", type));
        foreach (var (user, role) in members)
        {
            await ExecuteAdminAsync(
                "INSERT INTO organizations.organization_memberships(user_id,organization_id,role) VALUES (@u,@o,@r)",
                ("u", user), ("o", organization), ("r", role));
        }

        return organization;
    }

    private async Task AsAppAsync(Guid user, Guid organization, string sql)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, user, organization);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    private async Task<T> AsAppScalarAsync<T>(Guid user, Guid organization, string sql)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, user, organization);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = (T)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return value;
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, Guid organization)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT set_config('app.current_user_id', @user::text, true),
                   set_config('app.current_org_ids', ARRAY[@org]::uuid[]::text, true);
            SET LOCAL ROLE paqueteria_app;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("org", organization);
        await command.ExecuteNonQueryAsync();
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

/// <summary>A deterministic test-only email lookup key ring (never a production key).</summary>
internal static class EmailLookupTestHasher
{
    public static HmacEmailLookupHasher Create() => new(Options.Create(new EmailLookupOptions
    {
        CurrentKeyVersion = 1,
        Keys = new Dictionary<int, string>
        {
            [1] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("paquetenvia reg002 contract tests"))),
        },
    }));
}
