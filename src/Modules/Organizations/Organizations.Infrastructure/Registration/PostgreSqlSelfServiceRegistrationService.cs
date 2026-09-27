using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application;
using Organizations.Application.Registration;
using Paqueteria.Application.Idempotency;

namespace Organizations.Infrastructure.Registration;

/// <summary>
/// REG-001 through the five SECURITY DEFINER functions owned by
/// <c>paqueteria_registration_executor</c>. Every call runs as <c>paqueteria_app</c> inside an explicit
/// transaction. Onboarding happens before any tenant context exists and the ally decision crosses
/// tenants, so neither can use tenant RLS; the functions check the actor themselves. Never logs names,
/// subjects or identifiers.
/// </summary>
public sealed class PostgreSqlSelfServiceRegistrationService(
    NpgsqlDataSource dataSource,
    IOptions<TenancyOptions> options,
    ISelfServiceOrganizationAuthorizer authorizer,
    ILogger<PostgreSqlSelfServiceRegistrationService> logger) : ISelfServiceRegistrationService
{
    private const string InsufficientPrivilege = "42501";

    public async Task<SelfServiceOrganizationResult> CreateOrganizationAsync(
        CreateSelfServiceOrganizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        if (!authorizer.IsAuthorized(command))
        {
            return new SelfServiceOrganizationResult(SelfServiceOrganizationOutcome.Forbidden, null);
        }

        return await ExecuteAsync(
            """
            SELECT outcome, organization_id, organization_type, legal_name, display_name, status, membership_role
            FROM security.create_self_service_organization(
              @user_id, @organization_id, @membership_id, @audit_id,
              @organization_type, @legal_name, @display_name, @request_id)
            """,
            parameters =>
            {
                parameters.Add(Uuid("user_id", command.UserId));
                parameters.Add(Uuid("organization_id", DeriveOrganizationId(command.UserId, command.IdempotencyKey)));
                parameters.Add(Uuid("membership_id", Guid.NewGuid()));
                parameters.Add(Uuid("audit_id", Guid.NewGuid()));
                parameters.Add(Text("organization_type", command.OrganizationType));
                parameters.Add(Text("legal_name", command.LegalName));
                parameters.Add(Text("display_name", command.DisplayName));
                parameters.Add(Text("request_id", RequestId(command.RequestId)));
            },
            async reader =>
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new SelfServiceRegistrationUnavailableException("Onboarding returned no outcome.");
                }

                var outcome = reader.GetString(0) switch
                {
                    "CREATED" => SelfServiceOrganizationOutcome.Created,
                    "REPLAYED" => SelfServiceOrganizationOutcome.Replayed,
                    "IDEMPOTENCY_CONFLICT" => SelfServiceOrganizationOutcome.IdempotencyConflict,
                    "LIMIT_REACHED" => SelfServiceOrganizationOutcome.LimitReached,
                    "USER_INACTIVE" => SelfServiceOrganizationOutcome.Forbidden,
                    _ => throw new SelfServiceRegistrationUnavailableException("Onboarding returned an unknown outcome."),
                };
                if (outcome is not (SelfServiceOrganizationOutcome.Created or SelfServiceOrganizationOutcome.Replayed))
                {
                    return new SelfServiceOrganizationResult(outcome, null);
                }

                return new SelfServiceOrganizationResult(outcome, new SelfServiceOrganization(
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<OrganizationApplication>> ListOwnApplicationsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user is required.", nameof(userId));
        }

        return ExecuteAsync<IReadOnlyList<OrganizationApplication>>(
            """
            SELECT organization_id, organization_type, display_name, status, created_at
            FROM security.list_own_organization_applications(@user_id)
            """,
            parameters => parameters.Add(Uuid("user_id", userId)),
            async reader =>
            {
                var applications = new List<OrganizationApplication>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    applications.Add(new OrganizationApplication(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetFieldValue<DateTimeOffset>(4)));
                }

                return applications.AsReadOnly();
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingAllyOrganization>?> ListPendingAlliesAsync(
        Guid actorUserId,
        Guid platformOrganizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || platformOrganizationId == Guid.Empty ||
            limit is < 1 or > SelfServiceRegistrationLimits.MaximumPendingPageSize)
        {
            throw new ArgumentException("The pending ALLY request is invalid.");
        }

        try
        {
            return await ExecuteAsync<IReadOnlyList<PendingAllyOrganization>>(
                """
                SELECT organization_id, legal_name, display_name, created_at
                FROM security.list_pending_ally_organizations(@actor, @platform, @limit)
                """,
                parameters =>
                {
                    parameters.Add(Uuid("actor", actorUserId));
                    parameters.Add(Uuid("platform", platformOrganizationId));
                    parameters.Add(new NpgsqlParameter<int>("limit", NpgsqlDbType.Integer) { TypedValue = limit });
                },
                async reader =>
                {
                    var pending = new List<PendingAllyOrganization>();
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        pending.Add(new PendingAllyOrganization(
                            reader.GetGuid(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetFieldValue<DateTimeOffset>(3)));
                    }

                    return pending.AsReadOnly();
                },
                cancellationToken);
        }
        catch (SelfServiceRegistrationUnavailableException exception) when (IsInsufficientPrivilege(exception))
        {
            return null;
        }
    }

    public async Task<AllyDecisionOutcome> DecideAllyAsync(
        Guid actorUserId,
        Guid platformOrganizationId,
        Guid allyOrganizationId,
        bool approve,
        string? requestId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || platformOrganizationId == Guid.Empty || allyOrganizationId == Guid.Empty)
        {
            throw new ArgumentException("The ALLY decision is invalid.");
        }

        try
        {
            return await ExecuteAsync(
                "SELECT security.decide_ally_organization(@actor, @platform, @ally, @approve, @request_id)",
                parameters =>
                {
                    parameters.Add(Uuid("actor", actorUserId));
                    parameters.Add(Uuid("platform", platformOrganizationId));
                    parameters.Add(Uuid("ally", allyOrganizationId));
                    parameters.Add(new NpgsqlParameter<bool>("approve", NpgsqlDbType.Boolean) { TypedValue = approve });
                    parameters.Add(Text("request_id", RequestId(requestId)));
                },
                async reader =>
                {
                    if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
                    {
                        throw new SelfServiceRegistrationUnavailableException("The ALLY decision returned no outcome.");
                    }

                    return reader.GetString(0) switch
                    {
                        "ACTIVE" => AllyDecisionOutcome.Approved,
                        "CLOSED" => AllyDecisionOutcome.Rejected,
                        "NOT_FOUND" => AllyDecisionOutcome.NotFound,
                        "CONFLICT" => AllyDecisionOutcome.Conflict,
                        _ => throw new SelfServiceRegistrationUnavailableException("The ALLY decision returned an unknown outcome."),
                    };
                },
                cancellationToken);
        }
        catch (SelfServiceRegistrationUnavailableException exception) when (IsInsufficientPrivilege(exception))
        {
            return AllyDecisionOutcome.Forbidden;
        }
    }

    /// <summary>
    /// ONBOARDING-IDEMPOTENCY-DERIVED-ID: the organization id is derived from the user and the
    /// Idempotency-Key (the GEO-001 pattern), so a replay reaches the same row without widening
    /// <c>platform.idempotency_keys</c>, whose owner organization does not exist yet.
    /// </summary>
    public static Guid DeriveOrganizationId(Guid userId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"REG-001\0{userId:D}\0{idempotencyKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void Validate(CreateSelfServiceOrganizationCommand command)
    {
        if (command.UserId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            !SelfServiceRegistrationLimits.OrganizationTypes.Contains(command.OrganizationType ?? string.Empty) ||
            !IsValidName(command.LegalName, SelfServiceRegistrationLimits.LegalNameMaxLength) ||
            !IsValidName(command.DisplayName, SelfServiceRegistrationLimits.DisplayNameMaxLength))
        {
            throw new ArgumentException("The onboarding request is invalid.", nameof(command));
        }
    }

    public static bool IsValidName(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value == value.Trim() &&
        !value.Any(char.IsControl);

    private static string? RequestId(string? value) =>
        value is { Length: > 0 and <= 128 } ? value : null;

    private static bool IsInsufficientPrivilege(SelfServiceRegistrationUnavailableException exception) =>
        exception.InnerException is PostgresException { SqlState: InsufficientPrivilege };

    private async Task<T> ExecuteAsync<T>(
        string sql,
        Action<NpgsqlParameterCollection> bind,
        Func<NpgsqlDataReader, Task<T>> read,
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

            T result;
            await using (var command = new NpgsqlCommand(sql, connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            })
            {
                bind(command.Parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                result = await read(reader);
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
            logger.LogError("Self-service registration returned data that violates the expected contract.");
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == InsufficientPrivilege)
        {
            throw new SelfServiceRegistrationUnavailableException("The actor is not permitted.", exception);
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or InvalidCastException)
        {
            logger.LogError("Self-service registration failed due to a technical database error.");
            throw new SelfServiceRegistrationUnavailableException("Self-service registration is unavailable.", exception);
        }
    }

    private static NpgsqlParameter<Guid> Uuid(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { TypedValue = value };

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}

/// <summary>Tenancy disabled: onboarding and ally decisions are unavailable (503), never silently skipped.</summary>
public sealed class DisabledSelfServiceRegistrationService : ISelfServiceRegistrationService
{
    public Task<SelfServiceOrganizationResult> CreateOrganizationAsync(
        CreateSelfServiceOrganizationCommand command, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Self-service registration is disabled.");

    public Task<IReadOnlyList<OrganizationApplication>> ListOwnApplicationsAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Self-service registration is disabled.");

    public Task<IReadOnlyList<PendingAllyOrganization>?> ListPendingAlliesAsync(
        Guid actorUserId, Guid platformOrganizationId, int limit, CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Self-service registration is disabled.");

    public Task<AllyDecisionOutcome> DecideAllyAsync(
        Guid actorUserId, Guid platformOrganizationId, Guid allyOrganizationId, bool approve, string? requestId,
        CancellationToken cancellationToken) =>
        throw new SelfServiceRegistrationUnavailableException("Self-service registration is disabled.");
}
