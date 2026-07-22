using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Tracking;

namespace Orders.Infrastructure.Tracking;

public sealed class PostgreSqlPublicTrackingProjectionReader(
    NpgsqlDataSource dataSource,
    IOptions<PublicTrackingOptions> options,
    ILogger<PostgreSqlPublicTrackingProjectionReader> logger) : IPublicTrackingProjectionReader
{
    private const string Query = "SELECT security.get_public_tracking_projection(@token);";

    public async ValueTask<PublicTrackingLookupResult> FindAsync(
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

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
            command.Parameters.Add(new NpgsqlParameter<string>("token", NpgsqlDbType.Text)
            {
                TypedValue = token,
            });

            var value = await command.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (value is null or DBNull)
            {
                return PublicTrackingLookupResult.NotFound;
            }

            var projection = PublicTrackingJsonParser.Parse(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
            return PublicTrackingLookupResult.Found(projection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PublicTrackingInfrastructureException)
        {
            logger.LogError("Public tracking returned data that violates the expected contract.");
            throw;
        }
        catch (Exception exception) when (exception is not PublicTrackingInfrastructureException)
        {
            logger.LogError("Public tracking lookup failed due to a technical database error.");
            throw new PublicTrackingInfrastructureException("Public tracking is unavailable.", exception);
        }
    }
}
