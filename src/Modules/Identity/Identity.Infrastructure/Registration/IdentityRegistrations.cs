using Identity.Application.Bootstrap;
using Identity.Application.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Identity.Infrastructure.Registration;

/// <summary>
/// REG-001: <c>security.register_identity_subject(text,uuid)</c>, owned by
/// <c>paqueteria_registration_executor</c>, inside an explicit transaction as <c>paqueteria_app</c>.
/// The application generates the candidate user id; UNIQUE(identity_subject) decides which concurrent
/// sign-in wins, and the losers leave the existing user untouched. Never logs the subject.
/// </summary>
public sealed class PostgreSqlIdentityRegistration(
    NpgsqlDataSource dataSource,
    IOptions<IdentityBootstrapOptions> options,
    ILogger<PostgreSqlIdentityRegistration> logger) : IIdentityRegistration
{
    private const string Query = "SELECT security.register_identity_subject(@identity_subject, @user_id);";

    public async ValueTask RegisterAsync(string identitySubject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identitySubject);
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
            command.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
            {
                TypedValue = Guid.NewGuid(),
            });
            var value = await command.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (value is not Guid userId || userId == Guid.Empty)
            {
                throw new IdentityRegistrationUnavailableException("Identity registration returned no user.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityRegistrationUnavailableException)
        {
            logger.LogError("Identity registration returned data that violates the expected contract.");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("Identity registration failed due to a technical database error.");
            throw new IdentityRegistrationUnavailableException("Identity registration is unavailable.", exception);
        }
    }
}

/// <summary>Mock bootstrap: the synthetic subjects already exist, so nothing is registered.</summary>
public sealed class MockIdentityRegistration : IIdentityRegistration
{
    public ValueTask RegisterAsync(string identitySubject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identitySubject);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Disabled bootstrap: nothing can be registered, so sign-in fails closed.</summary>
public sealed class DisabledIdentityRegistration : IIdentityRegistration
{
    public ValueTask RegisterAsync(string identitySubject, CancellationToken cancellationToken) =>
        throw new IdentityRegistrationUnavailableException("Identity registration is disabled.");
}
