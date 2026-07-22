using Identity.Application.Bootstrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Identity.Infrastructure.Bootstrap;

public sealed class PostgreSqlIdentityContextResolver(
    NpgsqlDataSource dataSource,
    IOptions<IdentityBootstrapOptions> options,
    ILogger<PostgreSqlIdentityContextResolver> logger) : IIdentityContextResolver
{
    private const string Query = "SELECT security.resolve_identity_context(@identity_subject);";

    public async ValueTask<IdentityContextResolution> ResolveAsync(
        string identitySubject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identitySubject))
        {
            return IdentityContextResolution.NoAuthorizedContext;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var roleCommand = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app;", connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            })
            {
                await roleCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(Query, connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            };
            command.Parameters.Add(new NpgsqlParameter<string>("identity_subject", NpgsqlDbType.Text)
            {
                TypedValue = identitySubject,
            });

            var value = await command.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (value is null or DBNull)
            {
                return IdentityContextResolution.NoAuthorizedContext;
            }

            var context = IdentityContextJsonParser.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
            return IdentityContextResolution.Resolved(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityContextInfrastructureException)
        {
            logger.LogError("Identity bootstrap returned data that violates the expected contract.");
            throw;
        }
        catch (Exception exception) when (exception is not IdentityContextInfrastructureException)
        {
            logger.LogError("Identity bootstrap failed due to a technical database error.");
            throw new IdentityContextInfrastructureException("Identity bootstrap is unavailable.", exception);
        }
    }
}
