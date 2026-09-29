using Custody.Application.ProofUploads;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.Proofs;
using Incidents.Application.Incidents;
using Incidents.Infrastructure.Incidents;
using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29 against a real PostgreSQL baseline and the runtime application role:
/// listIncidents, getIncident and listOrderProofs page exactly the tenant's rows under FORCE RLS, settle
/// capability before any row, answer a foreign and a missing resource with the same not-found, and never
/// write.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class IncidentAndProofReadPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-09-29T15:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------- listIncidents

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_dispatcher_pages_every_tenant_incident_newest_first_across_ties_without_foreign_rows()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");
        var foreignProof = Assert.Single(await SeedProofsAsync(foreign, 1, CreatedAt));
        await SeedIncidentsAsync(foreign, foreignProof, 3, CreatedAt.AddHours(1));

        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proof = Assert.Single(await SeedProofsAsync(scenario, 1, CreatedAt));
        // One page and one more, all at the same created_at, so the id breaks every tie.
        var tied = await SeedIncidentsAsync(scenario, proof, IncidentReadPolicy.PageSize + 1, CreatedAt);
        var newest = Assert.Single(await SeedIncidentsAsync(scenario, proof, 1, CreatedAt.AddMinutes(1)));
        var before = await CountWritesAsync(scenario);

        var seen = new List<IncidentResult>();
        string? cursor = null;
        var pages = 0;
        do
        {
            await using var scope = CreateIncidentScope();
            IncidentCursor? position = null;
            Assert.True(cursor is null || IncidentCursorCodec.TryDecode(cursor, out position));
            var page = await scope.Service.ListAsync(
                new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, null, null, position),
                CancellationToken.None);
            Assert.True(page.Items.Count <= IncidentReadPolicy.PageSize);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(2, pages);
        Assert.Equal(IncidentReadPolicy.PageSize + 2, seen.Count);
        Assert.Equal(newest, seen[0].Id);
        Assert.Equal(tied.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal), seen.Skip(1).Select(item => item.Id));
        Assert.Equal(seen.Count, seen.Select(item => item.Id).Distinct().Count());
        Assert.All(seen, item =>
        {
            Assert.Equal(scenario.OrderId, item.OrderId);
            Assert.Equal("OPEN", item.Status);
            Assert.Equal("MEDIUM", item.Severity);
            Assert.Equal("RECIPIENT_ABSENT", item.ReasonCode);
            Assert.Equal("RESCHEDULED", item.NextAction);
            Assert.Equal([proof], item.EvidenceProofIds);
        });
        Assert.Equal(before, await CountWritesAsync(scenario));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Status_and_order_filters_keep_exactly_the_matching_incidents()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proof = Assert.Single(await SeedProofsAsync(scenario, 1, CreatedAt));
        var open = await SeedIncidentsAsync(scenario, proof, 2, CreatedAt);
        var resolved = await SeedIncidentsAsync(scenario, proof, 1, CreatedAt.AddMinutes(1), status: "RESOLVED");
        await using var scope = CreateIncidentScope();

        var onlyResolved = await scope.Service.ListAsync(
            new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, "RESOLVED", null, null),
            CancellationToken.None);
        Assert.Equal(resolved, onlyResolved.Items.Select(item => item.Id));
        Assert.Null(onlyResolved.NextCursor);

        var byOrder = await scope.Service.ListAsync(
            new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, "OPEN", scenario.OrderId, null),
            CancellationToken.None);
        Assert.Equal(open.Order(), byOrder.Items.Select(item => item.Id).Order());

        var otherOrder = await scope.Service.ListAsync(
            new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, null, Guid.NewGuid(), null),
            CancellationToken.None);
        Assert.Empty(otherOrder.Items);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Only_a_dispatcher_or_an_MFA_satisfied_platform_admin_reads_incidents()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proof = Assert.Single(await SeedProofsAsync(scenario, 1, CreatedAt));
        var incident = Assert.Single(await SeedIncidentsAsync(scenario, proof, 1, CreatedAt));

        await SetRoleAsync(scenario, "PLATFORM_ADMIN");
        await using (var scope = CreateIncidentScope())
        {
            await Assert.ThrowsAsync<IncidentForbiddenException>(() => scope.Service.ListAsync(
                new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, null, null, null),
                CancellationToken.None));
            await Assert.ThrowsAsync<IncidentForbiddenException>(() => scope.Service.GetAsync(
                new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, false, incident),
                CancellationToken.None));
            var page = await scope.Service.ListAsync(
                new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, true, null, null, null),
                CancellationToken.None);
            Assert.Equal([incident], page.Items.Select(item => item.Id));
        }

        foreach (var role in new[] { "DRIVER", "VIEWER", "FINANCE" })
        {
            await SetRoleAsync(scenario, role);
            await using var scope = CreateIncidentScope();
            foreach (var mfa in new[] { false, true })
            {
                await Assert.ThrowsAsync<IncidentForbiddenException>(() => scope.Service.ListAsync(
                    new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, mfa, null, null, null),
                    CancellationToken.None));
                // Capability precedes existence: a real and a random incident are the same refusal.
                await Assert.ThrowsAsync<IncidentForbiddenException>(() => scope.Service.GetAsync(
                    new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, mfa, incident),
                    CancellationToken.None));
                await Assert.ThrowsAsync<IncidentForbiddenException>(() => scope.Service.GetAsync(
                    new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, mfa, Guid.NewGuid()),
                    CancellationToken.None));
            }
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Get_returns_the_tenant_incident_and_a_foreign_or_missing_one_is_the_same_not_found()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");
        var foreignProof = Assert.Single(await SeedProofsAsync(foreign, 1, CreatedAt));
        var foreignIncident = Assert.Single(await SeedIncidentsAsync(foreign, foreignProof, 1, CreatedAt));
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proof = Assert.Single(await SeedProofsAsync(scenario, 1, CreatedAt));
        var incident = Assert.Single(await SeedIncidentsAsync(scenario, proof, 1, CreatedAt));
        await using var scope = CreateIncidentScope();

        var result = await scope.Service.GetAsync(
            new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, false, incident),
            CancellationToken.None);
        Assert.Equal(incident, result.Id);
        Assert.Equal(scenario.OrderId, result.OrderId);
        Assert.Equal([proof], result.EvidenceProofIds);
        Assert.Equal(CreatedAt.AddMinutes(-5), result.OccurredAt);
        Assert.Equal(CreatedAt.AddDays(1), result.SlaDueAt);

        await Assert.ThrowsAsync<IncidentNotFoundException>(() => scope.Service.GetAsync(
            new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, false, foreignIncident),
            CancellationToken.None));
        await Assert.ThrowsAsync<IncidentNotFoundException>(() => scope.Service.GetAsync(
            new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, false, Guid.NewGuid()),
            CancellationToken.None));
        var foreignOrder = await scope.Service.ListAsync(
            new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, null, foreign.OrderId, null),
            CancellationToken.None);
        Assert.Empty(foreignOrder.Items);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_stored_incident_outside_the_published_vocabulary_fails_closed()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proof = Assert.Single(await SeedProofsAsync(scenario, 1, CreatedAt));
        var incident = Assert.Single(await SeedIncidentsAsync(scenario, proof, 1, CreatedAt));
        await scenario.ExecuteAdminAsync(
            "UPDATE incidents.incidents SET reason_code='LEGACY_REASON' WHERE id=@incident;",
            SyntheticOrderScenario.P("incident", incident));
        await using var scope = CreateIncidentScope();

        await Assert.ThrowsAsync<IncidentInfrastructureException>(() => scope.Service.GetAsync(
            new GetIncidentQuery(scenario.UserId, scenario.OrganizationId, false, incident),
            CancellationToken.None));
        await Assert.ThrowsAsync<IncidentInfrastructureException>(() => scope.Service.ListAsync(
            new ListIncidentsQuery(scenario.UserId, scenario.OrganizationId, false, null, null, null),
            CancellationToken.None));
    }

    // ----------------------------------------------------------------------- listOrderProofs

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_dispatcher_pages_the_proof_metadata_of_one_order_only()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofs = await SeedProofsAsync(scenario, ProofReadPolicy.PageSize + 1, CreatedAt);
        var before = await CountWritesAsync(scenario);

        var seen = new List<ProofSummary>();
        ProofCursor? position = null;
        var pages = 0;
        while (true)
        {
            await using var scope = CreateProofScope();
            var page = await scope.Service.ListOrderProofsAsync(
                new ListOrderProofsQuery(scenario.UserId, scenario.OrganizationId, false, scenario.OrderId, position),
                CancellationToken.None);
            seen.AddRange(page.Items);
            pages++;
            if (page.NextCursor is null)
            {
                break;
            }

            Assert.True(ProofCursorCodec.TryDecode(page.NextCursor, scenario.OrderId, out position));
            // A cursor issued for this order never pages another one.
            Assert.False(ProofCursorCodec.TryDecode(page.NextCursor, Guid.NewGuid(), out _));
        }

        Assert.Equal(2, pages);
        Assert.Equal(proofs.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal), seen.Select(item => item.Id));
        Assert.All(seen, proof =>
        {
            Assert.Equal("DELIVERY_PHOTO", proof.ProofType);
            Assert.Equal(string.Concat(Enumerable.Repeat("04", 32)), proof.Sha256);
        });
        Assert.Equal(before, await CountWritesAsync(scenario));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_foreign_or_missing_order_is_the_same_not_found_and_capability_comes_first()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");
        await SeedProofsAsync(foreign, 2, CreatedAt);
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await SeedProofsAsync(scenario, 1, CreatedAt);

        await using (var scope = CreateProofScope())
        {
            await Assert.ThrowsAsync<ProofNotFoundException>(() => scope.Service.ListOrderProofsAsync(
                new ListOrderProofsQuery(scenario.UserId, scenario.OrganizationId, false, foreign.OrderId, null),
                CancellationToken.None));
            await Assert.ThrowsAsync<ProofNotFoundException>(() => scope.Service.ListOrderProofsAsync(
                new ListOrderProofsQuery(scenario.UserId, scenario.OrganizationId, false, Guid.NewGuid(), null),
                CancellationToken.None));
        }

        await SetRoleAsync(scenario, "PLATFORM_ADMIN");
        await using (var scope = CreateProofScope())
        {
            await Assert.ThrowsAsync<ProofForbiddenException>(() => scope.Service.ListOrderProofsAsync(
                new ListOrderProofsQuery(scenario.UserId, scenario.OrganizationId, false, scenario.OrderId, null),
                CancellationToken.None));
            var page = await scope.Service.ListOrderProofsAsync(
                new ListOrderProofsQuery(scenario.UserId, scenario.OrganizationId, true, scenario.OrderId, null),
                CancellationToken.None);
            Assert.Single(page.Items);
        }

        foreach (var role in new[] { "DRIVER", "VIEWER", "FINANCE" })
        {
            await SetRoleAsync(scenario, role);
            await using var scope = CreateProofScope();
            foreach (var order in new[] { scenario.OrderId, foreign.OrderId, Guid.NewGuid() })
            {
                await Assert.ThrowsAsync<ProofForbiddenException>(() => scope.Service.ListOrderProofsAsync(
                    new ListOrderProofsQuery(scenario.UserId, scenario.OrganizationId, true, order, null),
                    CancellationToken.None));
            }
        }
    }

    // ------------------------------------------------------------------------------- helpers

    private static Task SetRoleAsync(SyntheticOrderScenario scenario, string role) =>
        scenario.ExecuteAdminAsync(
            "UPDATE organizations.organization_memberships SET role=@role WHERE user_id=@user AND organization_id=@org;",
            SyntheticOrderScenario.P("role", role),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));

    private async Task<IReadOnlyList<Guid>> SeedProofsAsync(
        SyntheticOrderScenario scenario,
        int count,
        DateTimeOffset createdAt)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            SELECT p, @order, @org, @user, 'quarantine/' || p::text, 'image/jpeg', 1024, 'READY',
                   clock_timestamp() + interval '1 day'
            FROM unnest(@ids) AS p;
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,recipient_name_ciphertext,pii_key_version,captured_at,created_by,created_at)
            SELECT p, @order, @org, p, 'DELIVERY_PHOTO', 'proofs/' || p::text, decode(repeat('04',32),'hex'),
                   'image/jpeg', 100, decode('ff','hex'), 'test-v1', @created, @user, @created
            FROM unnest(@ids) AS p;
            """,
            SyntheticOrderScenario.P("ids", ids),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("created", createdAt));
        return ids;
    }

    private async Task<IReadOnlyList<Guid>> SeedIncidentsAsync(
        SyntheticOrderScenario scenario,
        Guid proofId,
        int count,
        DateTimeOffset createdAt,
        string status = "OPEN")
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              description_ciphertext,pii_key_version,reason_code,next_action,occurred_at,sla_due_at,created_by,created_at)
            SELECT i, @order, @org, 'FAILED_DELIVERY_ATTEMPT', 'MEDIUM', @status, false,
                   decode('00','hex'), 'test-v1', 'RECIPIENT_ABSENT', 'RESCHEDULED',
                   @created - interval '5 minutes', @created + interval '1 day', @user, @created
            FROM unnest(@ids) AS i;
            INSERT INTO incidents.incident_evidence(id,incident_id,order_id,owner_org_id,proof_id,created_by)
            SELECT gen_random_uuid(), i, @order, @org, @proof, @user
            FROM unnest(@ids) AS i;
            """,
            SyntheticOrderScenario.P("ids", ids),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("proof", proofId),
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("created", createdAt));
        return ids;
    }

    /// <summary>Every row a read could have written for the scenario: none may appear.</summary>
    private async Task<(long Audits, long Keys, long Incidents, long Proofs)> CountWritesAsync(
        SyntheticOrderScenario scenario)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org),
              (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org),
              (SELECT count(*) FROM incidents.incidents WHERE owner_org_id=@org AND resolved_at IS NOT NULL),
              (SELECT count(*) FROM custody.proofs WHERE owner_org_id=@org)
            """);
        command.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private Scope<IIncidentReadService> CreateIncidentScope()
    {
        var state = new TenantDatabaseExecutionState();
        var context = new IncidentsDbContext(
            new DbContextOptionsBuilder<IncidentsDbContext>()
                .UseNpgsql(fixture.AppDataSource)
                .AddInterceptors(
                    new TenantTransactionGuardInterceptor(state),
                    new TenantSaveChangesGuardInterceptor(state))
                .Options,
            state);
        return new Scope<IIncidentReadService>(
            context,
            new PostgreSqlIncidentReadService(new TenantTransactionContext<IncidentsDbContext>(context, state)));
    }

    private Scope<IProofReadService> CreateProofScope()
    {
        var state = new TenantDatabaseExecutionState();
        var context = new CustodyDbContext(
            new DbContextOptionsBuilder<CustodyDbContext>()
                .UseNpgsql(fixture.AppDataSource, postgres => postgres.UseNetTopologySuite())
                .AddInterceptors(
                    new TenantTransactionGuardInterceptor(state),
                    new TenantSaveChangesGuardInterceptor(state))
                .Options,
            state);
        return new Scope<IProofReadService>(
            context,
            new PostgreSqlProofReadService(new TenantTransactionContext<CustodyDbContext>(context, state)));
    }

    private sealed class Scope<T>(DbContext context, T service) : IAsyncDisposable
    {
        internal T Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
