using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.Infrastructure.DataProtection;

/// <summary>
/// Persists the ASP.NET Data Protection key ring in PostgreSQL so that every API and Worker
/// replica resolves the same keys. The Data Protection contract is synchronous, so the
/// repository deliberately uses blocking Npgsql calls.
/// </summary>
internal sealed class PostgreSqlXmlRepository : IXmlRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _applicationName;
    private readonly string _runtimeRole;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<PostgreSqlXmlRepository> _logger;

    public PostgreSqlXmlRepository(
        NpgsqlDataSource dataSource,
        DataProtectionOptions options,
        ILogger<PostgreSqlXmlRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!DataProtectionRuntimeRoles.IsCanonical(options.RuntimeRole))
        {
            throw new InvalidOperationException(
                "DataProtection:RuntimeRole must be a canonical least-privilege runtime role.");
        }

        _dataSource = dataSource;
        _applicationName = options.ApplicationName;
        _runtimeRole = options.RuntimeRole;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
        _logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        SetRuntimeRole(connection, transaction);
        using var command = Command(
            """
            SELECT xml
            FROM platform.data_protection_keys
            WHERE application_name=@application_name
            ORDER BY created_at, id
            """,
            connection,
            transaction);
        command.Parameters.Add(P("application_name", _applicationName));
        var elements = new List<XElement>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (TryParse(reader.GetString(0), out var element))
                {
                    elements.Add(element);
                }
            }
        }

        transaction.Commit();
        return elements.AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        SetRuntimeRole(connection, transaction);
        using var command = Command(
            """
            INSERT INTO platform.data_protection_keys(application_name, friendly_name, xml)
            VALUES (@application_name, @friendly_name, @xml)
            """,
            connection,
            transaction);
        command.Parameters.Add(P("application_name", _applicationName));
        command.Parameters.Add(P(
            "friendly_name",
            string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString("D") : friendlyName));
        command.Parameters.Add(P("xml", element.ToString(SaveOptions.DisableFormatting)));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // Runtime logins are NOINHERIT, so the canonical role is assumed per transaction. The value
    // is constrained to the canonical role names in the constructor.
    private void SetRuntimeRole(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        using var command = new NpgsqlCommand($"SET LOCAL ROLE {_runtimeRole}", connection, transaction);
        command.ExecuteNonQuery();
    }

    private NpgsqlCommand Command(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction) { CommandTimeout = _commandTimeoutSeconds };

    private bool TryParse(string xml, out XElement element)
    {
        try
        {
            element = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            // A single unreadable key must not take the whole ring down; the remaining keys still
            // let this replica read payloads protected elsewhere.
            _logger.LogWarning("Discarded an unreadable Data Protection key ring entry.");
            element = default!;
            return false;
        }
    }

    private static NpgsqlParameter P(string name, string value) =>
        new(name, NpgsqlDbType.Text) { Value = value };
}
