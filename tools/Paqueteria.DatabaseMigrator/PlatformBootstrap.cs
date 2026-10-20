using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Provisioning;
using Organizations.Domain;
using Organizations.Infrastructure.Persistence;
using Organizations.Infrastructure.Provisioning;
using Paqueteria.Application.Tenancy;
using Paqueteria.Domain.Tenancy;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

/// <summary>The AuthCenter subject of the first PLATFORM_ADMIN and the names of the PLATFORM organization.</summary>
internal sealed record PlatformBootstrapOptions(string IdentitySubject, string LegalName, string DisplayName);

internal sealed class PlatformBootstrapException(string message, int exitCode) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
}

/// <summary>
/// TEN-003-PLATFORM-BOOTSTRAP-2026-10-19 (owner decision E1, "Comando único auditado"): creates, once and before
/// the owner's first sign-in, the ACTIVE PLATFORM organization, the <c>identity.users</c> row of the owner's
/// AuthCenter subject and its ACTIVE, default PLATFORM_ADMIN membership, with one append-only audit row, through
/// the TEN-003 provisioner in one transaction of the deployment (migration) connection.
/// </summary>
/// <remarks>
/// The transaction (<see cref="PlatformBootstrapTransaction"/>) takes a fixed advisory lock, refuses unless the
/// database has no PLATFORM organization, no PLATFORM_ADMIN membership and no user for the subject (read as the
/// deployment login, which sees every tenant), then sets the transaction-local RLS context of the new UUIDs and
/// writes as <c>paqueteria_migrator</c> (NOBYPASSRLS; FORCE RLS applies). No role, grant or function is added.
/// Output names no subject, name or id.
/// </remarks>
internal static class PlatformBootstrap
{
    /// <summary>Refused before anything was written (already bootstrapped, subject known, wrong login).</summary>
    internal const int RefusedExitCode = 11;

    /// <summary>A database error; the transaction rolled back, so nothing was written.</summary>
    internal const int DatabaseExitCode = 12;

    /// <summary>The audit row's request id, which tells this run apart from any other TEN-003 provisioning.</summary>
    internal const string RequestId = "ten-003-platform-bootstrap";

    /// <summary>
    /// The fixed <c>pg_advisory_xact_lock(bigint)</c> key that serializes platform-bootstrap runs, so two
    /// concurrent first runs cannot both see an empty database (MDM-001 uses 2026092801-2026092803).
    /// </summary>
    internal const long AdvisoryLockKey = 2026101901L;

    internal static async Task<InitialOrganizationProvisioningResult> RunAsync(
        string connectionString,
        PlatformBootstrapOptions options,
        TextWriter output,
        CancellationToken cancellationToken,
        IProvisioningFailureInjector? failureInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = new TenantDatabaseExecutionState();
        var provisioner = new PostgreSqlInitialOrganizationProvisioner(
            new PlatformBootstrapAuthorizer(options.IdentitySubject),
            failureInjector ?? new NoOpProvisioningFailureInjector(),
            new PlatformBootstrapTransaction(connection, state, options.IdentitySubject),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new SystemClock());

        InitialOrganizationProvisioningResult result;
        try
        {
            result = await provisioner.ProvisionAsync(
                new InitialOrganizationProvisioningCommand(
                    options.IdentitySubject,
                    options.LegalName,
                    options.DisplayName,
                    OrganizationType.Platform,
                    OrganizationRole.PlatformAdmin,
                    RequestId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InitialOrganizationProvisioningConflictException)
        {
            // UNIQUE(identity_subject): the account signed in while the command ran.
            throw new PlatformBootstrapException(
                "TEN003_PLATFORM_BOOTSTRAP_SUBJECT_ALREADY_SIGNED_IN: this AuthCenter account signed in to " +
                "Paquetenvia while the command ran; it creates the administrator only before the first sign-in. " +
                "Nothing was written.",
                RefusedExitCode);
        }
        catch (PostgresException exception)
        {
            // Never statement text, parameters or values: SQLSTATE only.
            throw new PlatformBootstrapException(
                $"TEN003_PLATFORM_BOOTSTRAP_DATABASE_ERROR SQLSTATE={exception.SqlState}; nothing was written.",
                DatabaseExitCode);
        }

        output.WriteLine(
            "TEN003_PLATFORM_BOOTSTRAP_OK organization_type=PLATFORM status=ACTIVE role=PLATFORM_ADMIN (audited)");
        return result;
    }

    /// <summary>Authorizes exactly one provisioning: the PLATFORM organization and PLATFORM_ADMIN of one subject.</summary>
    private sealed class PlatformBootstrapAuthorizer(string identitySubject) : IInitialOrganizationProvisioningAuthorizer
    {
        public ValueTask<bool> IsAuthorizedAsync(
            InitialOrganizationProvisioningCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                string.Equals(command.IdentitySubject, identitySubject, StringComparison.Ordinal) &&
                command.OrganizationType == OrganizationType.Platform &&
                command.InitialRole == OrganizationRole.PlatformAdmin);
    }
}

/// <summary>
/// The one transaction of the platform bootstrap on the deployment connection: <c>BEGIN</c>; bounded waits; the
/// fixed advisory lock; the refusal checks as the deployment login; the parameterized transaction-local
/// <c>set_config(..., true)</c> of the new user and the <c>uuid[]</c> of the new organization; <c>SET LOCAL ROLE
/// paqueteria_migrator</c>; the TEN-003 inserts; <c>COMMIT</c>. Any failure rolls the whole transaction back.
/// </summary>
/// <remarks>
/// The Azure deployment login holds SET and USAGE on <c>paqueteria_migrator</c> (E-002 capability gate) but no SET
/// on <c>paqueteria_app</c>, so the API's <see cref="TenantTransactionContext{TDbContext}"/> cannot run here.
/// <c>paqueteria_migrator</c> is NOBYPASSRLS and every table written is FORCE ROW LEVEL SECURITY, so the
/// AI-06 policies admit only the pre-authorized new user and organization, exactly as for <c>paqueteria_app</c>.
/// </remarks>
internal sealed class PlatformBootstrapTransaction(
    NpgsqlConnection connection,
    TenantDatabaseExecutionState state,
    string identitySubject) : ITenantTransactionRunner<OrganizationsDbContext>
{
    private const string SetUserSql = "SELECT set_config('app.current_user_id', @user_id::uuid::text, true);";
    private const string SetOrganizationsSql = "SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);";
    private const string SetRoleSql = "SET LOCAL ROLE paqueteria_migrator;";

    public async Task<TResult> ExecuteAsync<TResult>(
        TenantDatabaseExecutionContext executionContext,
        Func<OrganizationsDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(operation);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entered = false;
        try
        {
            await ExecuteAsync(transaction, "SET LOCAL lock_timeout = '30s'; SET LOCAL statement_timeout = '60s';",
                cancellationToken).ConfigureAwait(false);
            await using (var serialize = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, transaction))
            {
                serialize.Parameters.Add(new NpgsqlParameter<long>("key", PlatformBootstrap.AdvisoryLockKey));
                await serialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AssertDeploymentLoginAsync(transaction, cancellationToken).ConfigureAwait(false);
            await RefuseUnlessFirstRunAsync(transaction, cancellationToken).ConfigureAwait(false);

            // AI-01 §4.1-§4.2 and AI-02 provisioning: the new UUIDs enter the RLS context only for this transaction.
            await using (var user = new NpgsqlCommand(SetUserSql, connection, transaction))
            {
                user.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
                {
                    TypedValue = executionContext.UserId,
                });
                await user.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var organizations = new NpgsqlCommand(SetOrganizationsSql, connection, transaction))
            {
                organizations.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    TypedValue = executionContext.OrganizationIds.ToArray(),
                });
                await organizations.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(transaction, SetRoleSql, cancellationToken).ConfigureAwait(false);
            await AssertRowSecurityAppliesAsync(transaction, cancellationToken).ConfigureAwait(false);

            var options = new DbContextOptionsBuilder<OrganizationsDbContext>().UseNpgsql(connection).Options;
            await using var context = new OrganizationsDbContext(options, state);
            await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            state.Enter(executionContext.UserId, executionContext.OrganizationIds);
            entered = true;

            var result = await operation(context, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (entered)
            {
                state.Exit();
            }
        }
    }

    /// <summary>
    /// The refusal checks read every tenant, so they need a login that bypasses RLS (the Azure deployment login is
    /// BYPASSRLS, E-002 preflight; a local one is a superuser) and can SET ROLE to <c>paqueteria_migrator</c>. Any
    /// other login, a runtime login included, is refused: under FORCE RLS it would see no row and miss a first run.
    /// </summary>
    private async Task AssertDeploymentLoginAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT (r.rolsuper OR r.rolbypassrls)
               AND pg_catalog.to_regrole('paqueteria_migrator') IS NOT NULL
               AND pg_catalog.pg_has_role(session_user, 'paqueteria_migrator', 'SET')
            FROM pg_catalog.pg_roles r
            WHERE r.rolname = current_user AND current_user = session_user
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new PlatformBootstrapException(
                "TEN003_PLATFORM_BOOTSTRAP_DEPLOYMENT_LOGIN_REQUIRED: connect with the migration connection (a " +
                "deployment login that bypasses row security and can SET ROLE paqueteria_migrator). Nothing was written.",
                PlatformBootstrap.RefusedExitCode);
        }
    }

    private async Task RefuseUnlessFirstRunAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM organizations.organizations o WHERE o.organization_type = 'PLATFORM')
                OR EXISTS (SELECT 1 FROM organizations.organization_memberships m WHERE m.role = 'PLATFORM_ADMIN'),
                   EXISTS (SELECT 1 FROM identity.users u WHERE u.identity_subject = @subject)
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("subject", NpgsqlDbType.Text) { TypedValue = identitySubject });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var bootstrapped = reader.GetBoolean(0);
        var signedIn = reader.GetBoolean(1);
        await reader.CloseAsync().ConfigureAwait(false);
        if (bootstrapped)
        {
            throw new PlatformBootstrapException(
                "TEN003_PLATFORM_BOOTSTRAP_ALREADY_DONE: a PLATFORM organization or a PLATFORM_ADMIN membership " +
                "already exists, and the platform bootstrap runs only once. Nothing was written.",
                PlatformBootstrap.RefusedExitCode);
        }

        if (signedIn)
        {
            throw new PlatformBootstrapException(
                "TEN003_PLATFORM_BOOTSTRAP_SUBJECT_ALREADY_SIGNED_IN: this AuthCenter account already signed in to " +
                "Paquetenvia; the command creates the administrator only before the first sign-in. Nothing was written.",
                PlatformBootstrap.RefusedExitCode);
        }
    }

    /// <summary>Defence in depth: the writes must run under FORCE RLS, never as a role that bypasses it.</summary>
    private async Task AssertRowSecurityAppliesAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT current_user = 'paqueteria_migrator' AND NOT r.rolsuper AND NOT r.rolbypassrls
            FROM pg_catalog.pg_roles r WHERE r.rolname = current_user
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new PlatformBootstrapException(
                "TEN003_PLATFORM_BOOTSTRAP_ROW_SECURITY_REQUIRED: paqueteria_migrator must be NOSUPERUSER NOBYPASSRLS " +
                "(AI-18). Nothing was written.",
                PlatformBootstrap.RefusedExitCode);
        }
    }

    private async Task ExecuteAsync(NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
