using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Provisioning;
using Organizations.Domain;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.Domain.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.Provisioning;

public sealed class PostgreSqlInitialOrganizationProvisioner(
    IInitialOrganizationProvisioningAuthorizer authorizer,
    IProvisioningFailureInjector failureInjector,
    TenantTransactionContext<OrganizationsDbContext> transactionContext)
    : IInitialOrganizationProvisioner
{
    public async Task<InitialOrganizationProvisioningResult> ProvisionAsync(
        InitialOrganizationProvisioningCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await authorizer.IsAuthorizedAsync(command, cancellationToken))
        {
            throw new InitialOrganizationProvisioningForbiddenException();
        }

        Validate(command);
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(userId, [organizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    await InsertUserAsync(connection, transaction, userId, command.IdentitySubject, occurredAt, token);
                    await failureInjector.AfterAsync(ProvisioningStage.UserInserted, token);
                    await InsertOrganizationAsync(connection, transaction, organizationId, command, occurredAt, token);
                    await failureInjector.AfterAsync(ProvisioningStage.OrganizationInserted, token);
                    await InsertMembershipAsync(connection, transaction, membershipId, userId, organizationId, command, occurredAt, token);
                    await failureInjector.AfterAsync(ProvisioningStage.MembershipInserted, token);
                    await InsertAuditAsync(connection, transaction, auditId, userId, organizationId, command.RequestId, occurredAt, token);
                    await failureInjector.AfterAsync(ProvisioningStage.AuditInserted, token);
                    return new InitialOrganizationProvisioningResult(userId, organizationId, membershipId, auditId);
                },
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InitialOrganizationProvisioningConflictException(
                "The initial organization has already been provisioned.", exception);
        }
    }

    private static void Validate(InitialOrganizationProvisioningCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.IdentitySubject) ||
            string.IsNullOrWhiteSpace(command.LegalName) ||
            string.IsNullOrWhiteSpace(command.DisplayName) ||
            !Enum.IsDefined(command.OrganizationType) ||
            !Enum.IsDefined(command.InitialRole))
        {
            throw new ArgumentException("The initial organization provisioning command is invalid.", nameof(command));
        }
    }

    private static async Task InsertUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string subject,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        const string sql =
            "INSERT INTO identity.users (id, identity_subject, email_ciphertext, status, created_at) " +
            "VALUES (@id, @subject, NULL, 'ACTIVE', @created_at);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = userId });
        command.Parameters.Add(new NpgsqlParameter<string>("subject", NpgsqlDbType.Text) { TypedValue = subject });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", NpgsqlDbType.TimestampTz) { TypedValue = createdAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOrganizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        InitialOrganizationProvisioningCommand request,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        const string sql =
            "INSERT INTO organizations.organizations " +
            "(id, legal_name, display_name, organization_type, status, created_at) " +
            "VALUES (@id, @legal_name, @display_name, @organization_type, 'ACTIVE', @created_at);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string>("legal_name", NpgsqlDbType.Text) { TypedValue = request.LegalName });
        command.Parameters.Add(new NpgsqlParameter<string>("display_name", NpgsqlDbType.Text) { TypedValue = request.DisplayName });
        command.Parameters.Add(new NpgsqlParameter<string>("organization_type", NpgsqlDbType.Text)
        {
            TypedValue = request.OrganizationType.ToContractValue(),
        });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", NpgsqlDbType.TimestampTz) { TypedValue = createdAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMembershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid membershipId,
        Guid userId,
        Guid organizationId,
        InitialOrganizationProvisioningCommand request,
        DateTimeOffset grantedAt,
        CancellationToken cancellationToken)
    {
        const string sql =
            "INSERT INTO organizations.organization_memberships " +
            "(id, user_id, organization_id, role, status, is_default, granted_at, revoked_at) " +
            "VALUES (@id, @user_id, @organization_id, @role, 'ACTIVE', TRUE, @granted_at, NULL);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = membershipId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid) { TypedValue = userId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string>("role", NpgsqlDbType.Text)
        {
            TypedValue = request.InitialRole.ToContractValue(),
        });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("granted_at", NpgsqlDbType.TimestampTz) { TypedValue = grantedAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid auditId,
        Guid userId,
        Guid organizationId,
        string? requestId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        const string sql =
            "INSERT INTO platform.audit_logs " +
            "(id, org_id, actor_id, action, entity_type, entity_id, request_id, payload_redacted, occurred_at) " +
            "VALUES (@id, @org_id, @actor_id, 'INITIAL_ORGANIZATION_PROVISIONED', 'ORGANIZATION', @entity_id, @request_id, '{}'::jsonb, @occurred_at);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = auditId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("org_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("actor_id", NpgsqlDbType.Uuid) { TypedValue = userId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("entity_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string?>("request_id", NpgsqlDbType.Text) { TypedValue = requestId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", NpgsqlDbType.TimestampTz) { TypedValue = occurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
