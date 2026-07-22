using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Outbox;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class EfOutboxInsertContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Ef_core_emits_insert_without_returning_for_both_outbox_lanes()
    {
        var organizationId = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'DBA-001 EF','DBA-001 EF','BUSINESS')",
            new NpgsqlParameter<Guid>("id", organizationId));

        try
        {
            var businessId = Guid.NewGuid();
            var businessSql = await InsertBusinessOutboxWithEfAsync(organizationId, businessId);
            AssertInsertOnlySql(businessSql, "platform.outbox_events");
            Assert.Equal(1, await CountAsAdminAsync("platform.outbox_events", businessId));
            await AssertRuntimeCannotReadAsync("platform.outbox_events", organizationId);

            var locationId = Guid.NewGuid();
            var locationSql = await InsertLocationOutboxWithEfAsync(organizationId, locationId);
            AssertInsertOnlySql(locationSql, "platform.location_outbox_events");
            Assert.Equal(1, await CountAsAdminAsync("platform.location_outbox_events", locationId));
            await AssertRuntimeCannotReadAsync("platform.location_outbox_events", organizationId);
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM platform.outbox_events WHERE owner_org_id=@org; DELETE FROM platform.location_outbox_events WHERE owner_org_id=@org; DELETE FROM organizations.organizations WHERE id=@org",
                new NpgsqlParameter<Guid>("org", organizationId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Ef_core_rejects_tracked_update_and_delete_lifecycle_operations()
    {
        var options = new DbContextOptionsBuilder<PlatformOutboxDbContext>()
            .UseNpgsql(fixture.DeploymentConnectionString)
            .Options;
        await using var context = new PlatformOutboxDbContext(options);
        var record = CreateBusinessRecord(Guid.NewGuid(), Guid.NewGuid());
        context.OutboxEvents.Attach(record);
        context.Entry(record).State = EntityState.Modified;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("canonical security functions", exception.Message, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<string>> InsertBusinessOutboxWithEfAsync(Guid organizationId, Guid id)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        var capture = new CommandCaptureInterceptor();
        var options = new DbContextOptionsBuilder<PlatformOutboxDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(capture)
            .Options;
        await using var context = new PlatformOutboxDbContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await SetRuntimeContextAsync(connection, (NpgsqlTransaction)transaction.GetDbTransaction(), "paqueteria_app", organizationId);
        context.OutboxEvents.Add(CreateBusinessRecord(organizationId, id));
        Assert.Equal(1, await context.SaveChangesAsync());
        await transaction.CommitAsync();
        return capture.Commands;
    }

    private async Task<IReadOnlyList<string>> InsertLocationOutboxWithEfAsync(Guid organizationId, Guid id)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        var capture = new CommandCaptureInterceptor();
        var options = new DbContextOptionsBuilder<PlatformOutboxDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(capture)
            .Options;
        await using var context = new PlatformOutboxDbContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await SetRuntimeContextAsync(connection, (NpgsqlTransaction)transaction.GetDbTransaction(), "paqueteria_worker", organizationId);
        context.LocationOutboxEvents.Add(new LocationOutboxEventRecord
        {
            Id = id,
            OwnerOrganizationId = organizationId,
            DriverPositionId = Guid.NewGuid(),
            Topic = "dba001.location",
            Payload = "{}",
            Status = "PENDING",
            Attempts = 0,
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        Assert.Equal(1, await context.SaveChangesAsync());
        await transaction.CommitAsync();
        return capture.Commands;
    }

    private static OutboxEventRecord CreateBusinessRecord(Guid organizationId, Guid id) => new()
    {
        Id = id,
        OwnerOrganizationId = organizationId,
        TenantContext = "{}",
        Topic = "dba001.business",
        AggregateType = "Order",
        AggregateId = Guid.NewGuid(),
        AggregateVersion = 1,
        Payload = "{}",
        Priority = 50,
        Status = "PENDING",
        Attempts = 0,
        AvailableAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task SetRuntimeContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roleName,
        Guid organizationId)
    {
        var roleSql = roleName switch
        {
            "paqueteria_app" => "SET LOCAL ROLE paqueteria_app",
            "paqueteria_worker" => "SET LOCAL ROLE paqueteria_worker",
            _ => throw new ArgumentOutOfRangeException(nameof(roleName)),
        };
        await using var role = new NpgsqlCommand(roleSql, connection, transaction);
        await role.ExecuteNonQueryAsync();
        await using var context = new NpgsqlCommand(
            "SELECT set_config('app.current_user_id',@user::uuid::text,true), set_config('app.current_org_ids',@orgs::uuid[]::text,true)",
            connection,
            transaction);
        context.Parameters.Add(new NpgsqlParameter<Guid>("user", NpgsqlDbType.Uuid) { TypedValue = Guid.NewGuid() });
        context.Parameters.Add(new NpgsqlParameter<Guid[]>("orgs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = [organizationId] });
        await context.ExecuteNonQueryAsync();
    }

    private static void AssertInsertOnlySql(IReadOnlyList<string> commands, string table)
    {
        var insert = Assert.Single(commands, command => command.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(table, insert.Replace("\"", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RETURNING", insert, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", insert, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertRuntimeCannotReadAsync(string table, Guid organizationId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.NewGuid(),
            [organizationId]);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", tenant.Connection, tenant.Transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private async Task<int> CountAsAdminAsync(string table, Guid id)
    {
        await using var command = fixture.AdminDataSource.CreateCommand($"SELECT count(*)::integer FROM {table} WHERE id=@id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        internal IReadOnlyList<string> Commands => _commands;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
