using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application;
using Organizations.Application.Registration;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Security;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.Registration;

/// <summary>
/// REG-002 (REG-JOIN-EXISTING-BY-EMAIL). Add, renew and revoke go through the SECURITY DEFINER
/// functions owned by <c>paqueteria_registration_executor</c>, which check the actor, the role ceiling
/// and the organization themselves; the list is a plain read under the tenant's RLS. The email is
/// normalized and hashed here and never leaves this class, and nothing identifying is logged.
/// </summary>
public sealed class PostgreSqlPendingMembershipService(
    NpgsqlDataSource dataSource,
    IOptions<TenancyOptions> options,
    IEmailLookupHasher emailLookupHasher,
    TenantTransactionContext<OrganizationsDbContext> transactionContext,
    ILogger<PostgreSqlPendingMembershipService> logger) : IPendingMembershipService
{
    private const string InsufficientPrivilege = "42501";

    public async Task<PendingMembershipResult> AddAsync(
        AddPendingMembershipCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ActorUserId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            !PendingMembershipLimits.Roles.Contains(command.Role ?? string.Empty) ||
            !EmailLookupNormalizer.TryNormalize(command.Email, out var normalized))
        {
            throw new ArgumentException("The pending membership request is invalid.", nameof(command));
        }

        if (!emailLookupHasher.IsAvailable)
        {
            throw new SelfServiceRegistrationUnavailableException("No email lookup key is configured.");
        }

        var hash = emailLookupHasher.HashForStorage(normalized);
        return await ExecuteEntryAsync(
            """
            SELECT outcome, pending_id, role, status, created_at, expires_at
            FROM security.add_pending_membership(
              @actor, @organization, @pending_id, @email_hmac, @key_version, @role, @idempotency_key, @request_id)
            """,
            parameters =>
            {
                parameters.Add(Uuid("actor", command.ActorUserId));
                parameters.Add(Uuid("organization", command.OrganizationId));
                parameters.Add(Uuid("pending_id", DerivePendingMembershipId(
                    command.ActorUserId, command.OrganizationId, command.IdempotencyKey)));
                parameters.Add(new NpgsqlParameter<byte[]>("email_hmac", NpgsqlDbType.Bytea) { TypedValue = hash.Hash });
                parameters.Add(new NpgsqlParameter<int>("key_version", NpgsqlDbType.Integer) { TypedValue = hash.KeyVersion });
                parameters.Add(Text("role", command.Role));
                parameters.Add(Text("idempotency_key", command.IdempotencyKey));
                parameters.Add(Text("request_id", RequestId(command.RequestId)));
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMembership>> ListAsync(
        Guid actorUserId,
        Guid organizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty ||
            limit is < 1 or > PendingMembershipLimits.MaximumPageSize)
        {
            throw new ArgumentException("The pending membership list request is invalid.");
        }

        try
        {
            return await transactionContext.ExecuteAsync<IReadOnlyList<PendingMembership>>(
                new TenantDatabaseExecutionContext(actorUserId, [organizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    await using var command = new NpgsqlCommand(
                        """
                        SELECT id, role, status, created_at, expires_at
                        FROM organizations.pending_memberships
                        WHERE organization_id = @organization
                        ORDER BY created_at DESC, id
                        LIMIT @limit
                        """,
                        connection,
                        transaction)
                    {
                        CommandTimeout = options.Value.CommandTimeoutSeconds,
                    };
                    command.Parameters.Add(Uuid("organization", organizationId));
                    command.Parameters.Add(new NpgsqlParameter<int>("limit", NpgsqlDbType.Integer) { TypedValue = limit });
                    await using var reader = await command.ExecuteReaderAsync(token);
                    var entries = new List<PendingMembership>();
                    while (await reader.ReadAsync(token))
                    {
                        entries.Add(new PendingMembership(
                            reader.GetGuid(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetFieldValue<DateTimeOffset>(3),
                            reader.GetFieldValue<DateTimeOffset>(4)));
                    }

                    return entries.AsReadOnly();
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or InvalidCastException or InvalidOperationException)
        {
            logger.LogError("Pending membership list failed due to a technical database error.");
            throw new SelfServiceRegistrationUnavailableException("Pending memberships are unavailable.", exception);
        }
    }

    public Task<PendingMembershipResult> RenewAsync(
        PendingMembershipActionCommand command,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync("security.renew_pending_membership", command, cancellationToken);

    public Task<PendingMembershipResult> RevokeAsync(
        PendingMembershipActionCommand command,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync("security.revoke_pending_membership", command, cancellationToken);

    /// <summary>
    /// The entry id is derived from the actor, the organization and the Idempotency-Key (the
    /// ONBOARDING-IDEMPOTENCY-DERIVED-ID pattern), so a replay reaches the same row.
    /// </summary>
    public static Guid DerivePendingMembershipId(Guid actorUserId, Guid organizationId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"REG-002\0{actorUserId:D}\0{organizationId:D}\0{idempotencyKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private Task<PendingMembershipResult> ExecuteActionAsync(
        string function,
        PendingMembershipActionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ActorUserId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            command.PendingMembershipId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw new ArgumentException("The pending membership request is invalid.", nameof(command));
        }

        return ExecuteEntryAsync(
            $"""
            SELECT outcome, pending_id, role, status, created_at, expires_at
            FROM {function}(@actor, @organization, @pending_id, @idempotency_key, @request_id)
            """,
            parameters =>
            {
                parameters.Add(Uuid("actor", command.ActorUserId));
                parameters.Add(Uuid("organization", command.OrganizationId));
                parameters.Add(Uuid("pending_id", command.PendingMembershipId));
                parameters.Add(Text("idempotency_key", command.IdempotencyKey));
                parameters.Add(Text("request_id", RequestId(command.RequestId)));
            },
            cancellationToken);
    }

    private async Task<PendingMembershipResult> ExecuteEntryAsync(
        string sql,
        Action<NpgsqlParameterCollection> bind,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app;", connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            })
            {
                await role.ExecuteNonQueryAsync(cancellationToken);
            }

            PendingMembershipResult result;
            await using (var command = new NpgsqlCommand(sql, connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            })
            {
                bind(command.Parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
                {
                    throw new SelfServiceRegistrationUnavailableException("The pending membership returned no outcome.");
                }

                result = reader.GetString(0) switch
                {
                    "ADDED" or "RENEWED" or "REPLAYED" or "REVOKED" => new PendingMembershipResult(
                        PendingMembershipOutcome.Succeeded,
                        new PendingMembership(
                            reader.GetGuid(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetFieldValue<DateTimeOffset>(4),
                            reader.GetFieldValue<DateTimeOffset>(5))),
                    "IDEMPOTENCY_CONFLICT" => new PendingMembershipResult(PendingMembershipOutcome.IdempotencyConflict, null),
                    "NOT_FOUND" => new PendingMembershipResult(PendingMembershipOutcome.NotFound, null),
                    "NOT_PENDING" => new PendingMembershipResult(PendingMembershipOutcome.NotPending, null),
                    "ROLE_NOT_ALLOWED" => new PendingMembershipResult(PendingMembershipOutcome.Forbidden, null),
                    _ => throw new SelfServiceRegistrationUnavailableException("The pending membership returned an unknown outcome."),
                };
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SelfServiceRegistrationUnavailableException)
        {
            logger.LogError("Pending membership returned data that violates the expected contract.");
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == InsufficientPrivilege)
        {
            return new PendingMembershipResult(PendingMembershipOutcome.Forbidden, null);
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or InvalidCastException)
        {
            logger.LogError("Pending membership failed due to a technical database error.");
            throw new SelfServiceRegistrationUnavailableException("Pending memberships are unavailable.", exception);
        }
    }

    private static string? RequestId(string? value) =>
        value is { Length: > 0 and <= 128 } ? value : null;

    private static NpgsqlParameter<Guid> Uuid(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { TypedValue = value };

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}

/// <summary>Tenancy disabled: pending memberships are unavailable (503), never silently skipped.</summary>
public sealed class DisabledPendingMembershipService : IPendingMembershipService
{
    public Task<PendingMembershipResult> AddAsync(AddPendingMembershipCommand command, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Pending memberships are disabled.");

    public Task<IReadOnlyList<PendingMembership>> ListAsync(
        Guid actorUserId, Guid organizationId, int limit, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Pending memberships are disabled.");

    public Task<PendingMembershipResult> RenewAsync(PendingMembershipActionCommand command, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Pending memberships are disabled.");

    public Task<PendingMembershipResult> RevokeAsync(PendingMembershipActionCommand command, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Pending memberships are disabled.");
}
