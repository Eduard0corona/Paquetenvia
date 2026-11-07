using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Security;

/// <summary>
/// Exactly one source: <paramref name="FilePath"/>, or <paramref name="Blob"/> (MDM-001 route S2), which also requires
/// <paramref name="ExpectedSha256"/>. <paramref name="DeleteAfterLoad"/> applies only to a Blob source on a real
/// (non-dry-run) load; <see cref="CommandOptions"/> refuses every other combination.
/// </summary>
internal sealed record MasterDataLoadOptions(
    string? FilePath,
    Guid OrganizationId,
    bool DryRun,
    bool AllowRealDriverProfiles,
    MasterDataBlobReference? Blob = null,
    byte[]? ExpectedSha256 = null,
    bool DeleteAfterLoad = false);

/// <summary>The environment classification that decides whether driver profiles may be loaded without the GATE-007 flag.</summary>
internal sealed record MasterDataEnvironment(string? EnvironmentName, string? DeploymentClass)
{
    internal static MasterDataEnvironment Current() => new(
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        Environment.GetEnvironmentVariable(SyntheticEnvironmentPolicy.DeploymentClassVariable));

    /// <summary>
    /// Development, Testing and DevSynthetic/DEV_SYNTHETIC hold synthetic people only. A pilot deployment
    /// class (PILOT_REAL_PEOPLE) is never synthetic, whatever DOTNET_ENVIRONMENT says.
    /// </summary>
    internal bool IsSyntheticOnly =>
        !string.Equals(DeploymentClass, "PILOT_REAL_PEOPLE", StringComparison.Ordinal) &&
        (EnvironmentName is "Development" or "Testing" ||
         (string.Equals(EnvironmentName, SyntheticEnvironmentPolicy.EnvironmentName, StringComparison.Ordinal) &&
          string.Equals(DeploymentClass, SyntheticEnvironmentPolicy.DeploymentClass, StringComparison.Ordinal)));
}

internal sealed class MasterDataLoadException(string message, int exitCode) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
}

/// <summary>
/// MDM-001-OPERATOR-LOADER operator job: validates a reviewed document, then loads it through
/// <c>security.load_master_data</c> as <c>paqueteria_master_data_loader</c> inside one explicit transaction
/// that carries exactly the target organization as tenant context. A dry run uses a READ ONLY transaction
/// and rolls back. Output is counts and reference-only diffs; no value from the document is printed.
/// The document comes from a local file or (MDM-001 route S2) from a private Blob container; either way its
/// SHA-256 is checked against <c>--expected-sha256</c> when given (always for a Blob) before any validation or
/// database connection, and a Blob is deleted only after a committed, non-dry-run load when asked to.
/// </summary>
internal static class MasterDataLoader
{
    internal const string LoaderRole = "paqueteria_master_data_loader";
    internal const int ValidationExitCode = 8;
    internal const int DatabaseExitCode = 9;
    internal const int GateExitCode = 10;

    /// <param name="blobSource">
    /// The Blob source for <see cref="MasterDataLoadOptions.Blob"/>; the migrator builds it with the job's managed
    /// identity (<see cref="MasterDataBlobSource.Create"/>), tests with a stand-in transport.
    /// </param>
    internal static async Task<JsonDocument> RunAsync(
        string connectionString,
        MasterDataLoadOptions options,
        MasterDataEnvironment environment,
        TextWriter output,
        CancellationToken cancellationToken,
        MasterDataBlobSource? blobSource = null)
    {
        if ((options.FilePath is null) == (options.Blob is null) ||
            (options.Blob is not null && (blobSource is null || options.ExpectedSha256 is null)) ||
            (options.DeleteAfterLoad && (options.Blob is null || options.DryRun)))
        {
            throw new InvalidOperationException("master-data-load needs exactly one source; see CommandOptions.");
        }

        MasterDataBlobDownload? download = null;
        byte[] content;
        if (blobSource is not null)
        {
            download = await blobSource.DownloadAsync(cancellationToken).ConfigureAwait(false);
            content = download.Content;
        }
        else
        {
            content = await ReadFileAsync(options.FilePath!, cancellationToken).ConfigureAwait(false);
        }

        // MDM-001 route S2: the bytes must be the reviewed bytes before anything else looks at them.
        var actual = SHA256.HashData(content);
        var reference = blobSource is null ? "source=file" : $"source=blob {blobSource.Reference.Describe()}";
        output.WriteLine($"MDM001_SOURCE {reference} bytes={content.Length} sha256={Convert.ToHexStringLower(actual)}");
        if (options.ExpectedSha256 is { } expected && !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new MasterDataLoadException(
                $"MDM001_FILE_SHA256_MISMATCH sha256={Convert.ToHexStringLower(actual)} " +
                $"expected={Convert.ToHexStringLower(expected)}; the document was neither validated nor loaded",
                ValidationExitCode);
        }

        var result = await LoadAsync(connectionString, options, environment, content, output, cancellationToken)
            .ConfigureAwait(false);
        if (options.DeleteAfterLoad && !options.DryRun)
        {
            // Only after the commit above: a dry run, a rejection or any failure returns or throws before this.
            try
            {
                await blobSource!.DeleteAsync(download!.ETag, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                result.Dispose();
                throw;
            }

            output.WriteLine($"MDM001_BLOB_DELETED {blobSource.Reference.Describe()}");
        }

        return result;
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MasterDataDocumentValidator.MaximumFileBytes)
            {
                throw new MasterDataLoadException("MDM001_FILE_UNREADABLE", ValidationExitCode);
            }

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new MasterDataLoadException("MDM001_FILE_UNREADABLE", ValidationExitCode);
        }
        catch (UnauthorizedAccessException)
        {
            throw new MasterDataLoadException("MDM001_FILE_UNREADABLE", ValidationExitCode);
        }
    }

    private static async Task<JsonDocument> LoadAsync(
        string connectionString,
        MasterDataLoadOptions options,
        MasterDataEnvironment environment,
        byte[] content,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var validation = MasterDataDocumentValidator.Validate(content, options.OrganizationId);
        var sha = Convert.ToHexStringLower(validation.Sha256);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                output.WriteLine($"MDM001_VALIDATION_ERROR {error}");
            }

            throw new MasterDataLoadException(
                $"MDM001_DOCUMENT_REJECTED errors={validation.Errors.Count} sha256={sha}", ValidationExitCode);
        }

        // GATE-007: real driver profiles are worker personal data. Outside a synthetic-only environment the
        // operator must attest, with an explicit flag, that GATE-007 is closed.
        if (validation.HasDriverProfiles && !environment.IsSyntheticOnly && !options.AllowRealDriverProfiles)
        {
            throw new MasterDataLoadException(
                "MDM001_DRIVER_PROFILES_REQUIRE_GATE_007: driver profiles load only in Development, Testing or " +
                "DEV_SYNTHETIC unless --allow-real-driver-profiles attests that GATE-007 is closed.",
                GateExitCode);
        }

        var loadId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AssertOperatorLoginAsync(connection, cancellationToken).ConfigureAwait(false);

        // AI-01 §4.1-§4.2: explicit transaction, role and tenant context set after BEGIN and scoped to it.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (options.DryRun)
            {
                await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken)
                    .ConfigureAwait(false);
            }

            // MDM-001 m2: a bounded load. The lock wait covers a concurrent load of the same organization or
            // of global rows (cities, driver profiles); both limits are transaction-scoped.
            await ExecuteAsync(connection, transaction,
                "SET LOCAL statement_timeout = '300s'; SET LOCAL lock_timeout = '30s'", cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {LoaderRole}", cancellationToken)
                .ConfigureAwait(false);
            await using (var context = new NpgsqlCommand(
                """
                SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true),
                       set_config('app.current_user_id', '', true)
                """, connection, transaction))
            {
                context.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", [options.OrganizationId]));
                await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            string resultJson;
            await using (var load = new NpgsqlCommand(
                "SELECT security.load_master_data(@organization_id, @load_id, @document, @sha256, @dry_run)::text",
                connection, transaction))
            {
                load.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", options.OrganizationId));
                load.Parameters.Add(new NpgsqlParameter<Guid>("load_id", loadId));
                // json, not jsonb: PostgreSQL then sees the exact text, so it judges number tokens and
                // duplicate keys exactly as the job does (MDM-001 m5).
                load.Parameters.Add(new NpgsqlParameter("document", NpgsqlDbType.Json)
                {
                    Value = System.Text.Encoding.UTF8.GetString(content),
                });
                load.Parameters.Add(new NpgsqlParameter<byte[]>("sha256", validation.Sha256));
                load.Parameters.Add(new NpgsqlParameter<bool>("dry_run", options.DryRun));
                resultJson = (string)(await load.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            if (options.DryRun)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var result = JsonDocument.Parse(resultJson);
            Print(result.RootElement, loadId, options.DryRun, output);
            return result;
        }
        catch (PostgresException exception)
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // Loader failures carry a code (MDM001_*) and a reference hint (section[n]); anything else is
            // reported by SQLSTATE only, never with statement text or values.
            var code = exception.MessageText.StartsWith("MDM001_", StringComparison.Ordinal)
                ? exception.MessageText
                : $"MDM001_DATABASE_ERROR SQLSTATE={exception.SqlState}";
            var hint = string.IsNullOrEmpty(exception.Hint) ? string.Empty : $" at {exception.Hint}";
            throw new MasterDataLoadException(
                $"{code}{hint}; nothing was written (sha256={sha})", DatabaseExitCode);
        }
    }

    /// <summary>
    /// The connection must be an operator LOGIN that can SET ROLE to the loader grantee and is neither a
    /// superuser nor BYPASSRLS nor a runtime or migrator login, so the job never runs with more than EXECUTE.
    /// </summary>
    private static async Task AssertOperatorLoginAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT NOT r.rolsuper AND NOT r.rolbypassrls
               AND pg_catalog.to_regrole('paqueteria_master_data_loader') IS NOT NULL
               AND pg_catalog.pg_has_role(session_user, 'paqueteria_master_data_loader', 'SET')
               AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_app', 'MEMBER')
               AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_worker', 'MEMBER')
               AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_migrator', 'MEMBER')
            FROM pg_catalog.pg_roles r WHERE r.rolname = session_user
            """, connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new MasterDataLoadException(
                "MDM001_OPERATOR_LOGIN_REQUIRED: connect as an operator login that is a member of " +
                "paqueteria_master_data_loader only (no superuser, BYPASSRLS, runtime or migrator role).",
                GateExitCode);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Print(JsonElement result, Guid loadId, bool dryRun, TextWriter output)
    {
        output.WriteLine(dryRun
            ? $"MDM001_DRY_RUN classification={result.GetProperty("classification").GetString()} deployment={result.GetProperty("deployment_class").GetString()} (nothing written)"
            : $"MDM001_LOAD_OK load_id={loadId:D} classification={result.GetProperty("classification").GetString()} deployment={result.GetProperty("deployment_class").GetString()}");
        output.WriteLine($"  file_sha256={result.GetProperty("file_sha256_reported").GetString()}");
        output.WriteLine($"  document_sha256_computed_by_postgresql={result.GetProperty("document_sha256_computed").GetString()}");
        foreach (var entity in new[] { "cities", "service_areas", "operating_zones", "tariff_rules", "driver_profiles", "driver_service_areas" })
        {
            var counts = result.GetProperty("counts").GetProperty(entity);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {entity}: created={counts.GetProperty("created").GetInt64()} updated={counts.GetProperty("updated").GetInt64()} unchanged={counts.GetProperty("unchanged").GetInt64()}"));
        }

        foreach (var change in result.GetProperty("changes").EnumerateArray())
        {
            var fields = string.Join(',', change.GetProperty("fields").EnumerateArray().Select(field => field.GetString()));
            output.WriteLine(fields.Length == 0
                ? $"  {change.GetProperty("action").GetString()} {change.GetProperty("ref").GetString()}"
                : $"  {change.GetProperty("action").GetString()} {change.GetProperty("ref").GetString()} fields={fields}");
        }
    }
}
