using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.Database.Baseline;

return await DatabaseMigratorProgram.RunAsync(args).ConfigureAwait(false);

internal static partial class DatabaseMigratorProgram
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var options = CommandOptions.Parse(arguments);
            var verifier = new DatabaseBaselineVerifier();
            var baseline = await verifier.VerifyAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
            var verifiedModules = ModuleMigrationCoordinator.VerifySources();

            if (options.Command == "verify")
            {
                PrintVerifiedBaseline(baseline);
                PrintModuleMigrations(verifiedModules);
                return 0;
            }

            var connectionString = ReadConnectionString(options.ConnectionEnvironment!);
            E002SemanticAssertions.AssertConnectionReset(connectionString);
            if (options.Command == "master-data-load")
            {
                // MDM-001-OPERATOR-LOADER: an operator login with EXECUTE only; never the migration connection.
                using var result = await MasterDataLoader.RunAsync(
                    connectionString, options.MasterData!, MasterDataEnvironment.Current(), Console.Out,
                    cancellation.Token).ConfigureAwait(false);
                return 0;
            }

            if (options.Command == "master-data-gate")
            {
                // MDM-001 B1 / GATE-007: the deployment (migration) connection records the database class.
                await MasterDataGate.SetAsync(
                    connectionString, options.MasterDataGate!,
                    Environment.GetEnvironmentVariable(SyntheticEnvironmentPolicy.DeploymentClassVariable), Console.Out,
                    cancellation.Token).ConfigureAwait(false);
                return 0;
            }

            var deployer = new DatabaseBaselineDeployer();
            var moduleMigrations = new ModuleMigrationCoordinator();
            switch (options.Command)
            {
                case "preflight":
                    var preflight = await AzureRolePreflight.RunAsync(
                        connectionString, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("PG18_ROLE_SWITCH_PREFLIGHT_OK");
                    Console.WriteLine($"createrole_self_grant={preflight.CreateroleSelfGrant}");
                    Console.WriteLine($"admin_set_role={preflight.AdminSetRole}");
                    Console.WriteLine($"runtime_set_role={preflight.RuntimeSetRole}");
                    Console.WriteLine($"runtime_nobypassrls={preflight.RuntimeNoBypassRls}");
                    Console.WriteLine($"bypassrls_role_created={preflight.BypassRlsRoleCreated}");
                    return 0;

                case "runtime-logins":
                    // ENV-001: least-privilege runtime LOGIN roles for the pilot API and Worker (SCRAM verifiers only).
                    await deployer.AssertAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                    await RuntimeLoginProvisioner.RunAsync(connectionString, ReadSetting, cancellation.Token)
                        .ConfigureAwait(false);
                    Console.WriteLine($"RUNTIME_LOGINS_OK api={RuntimeLoginProvisioner.ApiLogin} worker={RuntimeLoginProvisioner.WorkerLogin}");
                    return 0;

                case "ownership-diagnostic":
                    var diagnosticSelection = ReadAzureOwnershipBridgeSelection(connectionString);
                    await AzureRolePreflight.RunAsync(connectionString, cancellation.Token).ConfigureAwait(false);
                    await AzureOwnershipBridgeDiagnostic.RunAsync(
                        connectionString, diagnosticSelection, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("AZR001_E002_DISPOSABLE_DIAGNOSTIC_OK");
                    return 0;

                case "plan":
                    var plan = await deployer.PlanAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                    PrintPlan(plan);
                    IReadOnlyList<ModuleMigrationState> modulePlan;
                    if (plan.State.Status != DatabaseBaselineStatus.Clean)
                    {
                        if (plan.State.Status == DatabaseBaselineStatus.Applied)
                        {
                            await deployer.AssertAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                            await AssertE002SemanticAsync(connectionString, cancellation.Token).ConfigureAwait(false);
                        }

                        modulePlan = await moduleMigrations.PlanAsync(connectionString, cancellation.Token);
                    }
                    else
                    {
                        modulePlan = verifiedModules.Select(module => module with { Status = "PENDING" }).ToArray();
                    }
                    PrintModulePlan(modulePlan);
                    return plan.State.Status == DatabaseBaselineStatus.Partial ||
                        modulePlan.Any(module => module.Status == "DRIFT") ? 4 : 0;

                case "apply":
                    if (!options.ConfirmInitialBaseline)
                    {
                        Console.Error.WriteLine("Apply requires --confirm-initial-baseline.");
                        return 2;
                    }

                    AzureOwnershipBridgeSelection? ownershipBridge = null;
                    if (options.AzureOwnershipBridge)
                    {
                        ownershipBridge = ReadAzureOwnershipBridgeSelection(connectionString);
                    }

                    var result = await deployer.ApplyAsync(
                        baseline, connectionString, cancellation.Token, ownershipBridge).ConfigureAwait(false);
                    await AssertE002SemanticAsync(connectionString, cancellation.Token).ConfigureAwait(false);
                    var beforeModules = await moduleMigrations.PlanAsync(connectionString, cancellation.Token)
                        .ConfigureAwait(false);
                    if (result.Status == DatabaseBaselineApplyStatus.AlreadyApplied)
                    {
                        // E-002 v0.8 §35: a fully applied database is a read-only no-op; a pending module set is the
                        // ordinary Applied-baseline path (v0.12 Amendment 3), with bridge activation = 0 either way.
                        var pendingModules = beforeModules.Where(module => module.Status == "PENDING")
                            .Select(module => module.Module).ToArray();
                        Console.WriteLine(pendingModules.Length == 0
                            ? $"E002_APPLIED_PATH_NO_OP modules={beforeModules.Count}/{beforeModules.Count} APPLIED bridge_activation=0"
                            : $"E002_APPLIED_PATH_PENDING modules={string.Join(",", pendingModules)} bridge_activation=0");
                    }

                    await moduleMigrations.ApplyAsync(connectionString, cancellation.Token,
                        ownershipBridge is not null).ConfigureAwait(false);
                    await AssertE002SemanticAsync(connectionString, cancellation.Token).ConfigureAwait(false);
                    PrintApplyResult(result);
                    PrintModulePlan(await moduleMigrations.AssertAsync(connectionString, cancellation.Token));
                    return 0;

                case "assert":
                    var report = await deployer.AssertAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                    await AssertE002SemanticAsync(connectionString, cancellation.Token).ConfigureAwait(false);
                    var moduleReport = await moduleMigrations.AssertAsync(connectionString, cancellation.Token).ConfigureAwait(false);
                    PrintAssertionReport(report);
                    PrintModulePlan(moduleReport);
                    return 0;

                default:
                    throw new CommandLineException($"Unknown command '{options.Command}'.");
            }
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }
        catch (BaselineVerificationException exception)
        {
            Console.Error.WriteLine($"Baseline verification failed: {exception.Message}");
            return 3;
        }
        catch (PartialDatabaseBaselineException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 4;
        }
        catch (MasterDataLoadException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.ExitCode;
        }
        catch (RuntimeLoginException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 7;
        }
        catch (AzureRolePreflightException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 6;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Database baseline operation was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Database baseline operation failed ({exception.GetType().Name}): {exception.Message}");
            return 5;
        }
    }

    private static string ReadConnectionString(string environmentName)
    {
        if (!EnvironmentVariableName().IsMatch(environmentName))
        {
            throw new CommandLineException("--connection-env must be a valid environment variable name.");
        }

        var value = ReadSetting(environmentName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException(
                $"'{environmentName}' is set neither as an environment variable nor as a mapped Key Vault secret.");
        }

        return value;
    }

    private static readonly object KeyVaultSettingsLock = new();
    private static IReadOnlyDictionary<string, string>? keyVaultSettings;

    /// <summary>
    /// The single lookup for named settings (the <c>--connection-env</c> connection and the runtime-login
    /// verifiers <c>PAQUETERIA_API_LOGIN_VERIFIER</c> / <c>PAQUETERIA_WORKER_LOGIN_VERIFIER</c>).
    /// PILOT-KEYVAULT-PRIVATE-APP-READ: a name mapped to a Key Vault secret returns the secret; any other
    /// name reads the environment <b>at call time</b> (never a snapshot). Key Vault is read on first use
    /// only when <c>KeyVaultSecrets__VaultUri</c> is set, so <c>verify</c> never reaches it.
    /// </summary>
    internal static string? ReadSetting(string name) =>
        ResolveSetting(name, KeyVaultSettings(), Environment.GetEnvironmentVariable);

    /// <summary>Mapped Key Vault value first, then the live environment.</summary>
    internal static string? ResolveSetting(
        string name,
        IReadOnlyDictionary<string, string> keyVault,
        Func<string, string?> environment) =>
        keyVault.TryGetValue(name, out var value) ? value : environment(name);

    private static IReadOnlyDictionary<string, string> KeyVaultSettings()
    {
        lock (KeyVaultSettingsLock)
        {
            if (keyVaultSettings is not null)
            {
                return keyVaultSettings;
            }

            var loaded = KeyVaultSecretsConfiguration.LoadMappedSecrets(
                new ConfigurationBuilder().AddEnvironmentVariables().Build());
            if (loaded.Count > 0)
            {
                // Only an enabled source is cached (its secrets are read once per process).
                keyVaultSettings = loaded;
            }

            return loaded;
        }
    }

    private static async Task<E002NotificationState> AssertE002SemanticAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = await E002NotificationStateReader.ReadAsync(connection,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (state == E002NotificationState.Drift)
        {
            throw new InvalidOperationException("E002_ROUTINE_MAP_STATE_UNRESOLVED; STOP_FOR_CONTRACT_REVIEW");
        }

        var semantic = await new E002SemanticAssertions().AssertAsync(
            connection, state, cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"E002_SEMANTIC_APPLIED state={state} map={semantic.RoutineMap} identities={semantic.ControlledIdentities} execute_rows={semantic.NormalizedExecuteRows}");
        return state;
    }

    private static AzureOwnershipBridgeSelection ReadAzureOwnershipBridgeSelection(string connectionString)
    {
        var selection = new AzureOwnershipBridgeSelection(
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? string.Empty,
            Environment.GetEnvironmentVariable("PAQUETERIA_DEPLOYMENT_CLASS") ?? string.Empty,
            Environment.GetEnvironmentVariable("PAQUETERIA_DB_DEPLOYMENT_PROVIDER") ?? string.Empty);
        selection.AssertAllowed();
        var host = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Host;
        if (host is null || !host.EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("E-002 requires an Azure PostgreSQL Flexible Server host.");
        }

        return selection;
    }

    private static void PrintVerifiedBaseline(VerifiedDatabaseBaseline baseline)
    {
        Console.WriteLine($"Baseline {baseline.Version}: VERIFIED");
        foreach (var step in baseline.Steps)
        {
            Console.WriteLine($"{step.Order:D4} {step.Id} {step.RelativePath} sha256={step.Sha256[..12]}...");
        }
    }

    private static void PrintModuleMigrations(IEnumerable<ModuleMigrationState> modules)
    {
        foreach (var module in modules)
        {
            Console.WriteLine($"{module.Module}: {module.MigrationId} source={module.Status}");
        }
    }

    private static void PrintModulePlan(IEnumerable<ModuleMigrationState> modules)
    {
        foreach (var module in modules)
        {
            Console.WriteLine($"{module.Module}: {module.Status} migration={module.MigrationId} history={module.HistoryTable}");
        }
    }

    private static void PrintPlan(DatabaseBaselinePlan plan)
    {
        Console.WriteLine($"Target: {plan.SanitizedTarget}");
        Console.WriteLine($"Baseline: {plan.Baseline}");
        Console.WriteLine($"State: {plan.State.Status}");
        foreach (var step in plan.Steps)
        {
            Console.WriteLine($"Step {step.Order}: {step.Id} sha256={step.Sha256[..12]}... credential={step.Credential}");
        }

        Console.WriteLine("Post-deployment assertions:");
        foreach (var assertion in plan.Assertions)
        {
            Console.WriteLine($"- {assertion}");
        }

        if (plan.State.Status == DatabaseBaselineStatus.Partial)
        {
            Console.WriteLine($"Present critical objects: {string.Join(", ", plan.State.PresentCriticalObjects)}");
            Console.WriteLine($"Missing critical objects: {string.Join(", ", plan.State.MissingCriticalObjects)}");
        }
    }

    private static void PrintApplyResult(DatabaseBaselineApplyResult result)
    {
        Console.WriteLine($"Result: {result.Status}");
        Console.WriteLine($"PostgreSQL: {result.Assertions.PostgreSqlVersion}");
        Console.WriteLine($"PostGIS: {result.Assertions.PostGisVersion}");
        Console.WriteLine($"AI-06: {result.Timings.Schema.TotalMilliseconds:F0} ms");
        Console.WriteLine($"AI-18: {result.Timings.Roles.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Assertions: {result.Timings.Assertions.TotalMilliseconds:F0} ms ({result.Assertions.Checks} checks)");
    }

    private static void PrintAssertionReport(DatabaseAssertionReport report)
    {
        Console.WriteLine("Result: ASSERTIONS_OK");
        Console.WriteLine($"PostgreSQL: {report.PostgreSqlVersion}");
        Console.WriteLine($"PostGIS: {report.PostGisVersion}");
        Console.WriteLine($"Assertions: {report.Duration.TotalMilliseconds:F0} ms ({report.Checks} checks)");
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: Paqueteria.DatabaseMigrator <verify|preflight|ownership-diagnostic|plan|apply|assert|runtime-logins> [--connection-env NAME] [--confirm-initial-baseline] [--azure-ownership-bridge]" +
        Environment.NewLine +
        "       Paqueteria.DatabaseMigrator master-data-load --connection-env NAME --file PATH --organization-id UUID [--dry-run] [--allow-real-driver-profiles]" +
        Environment.NewLine +
        "       Paqueteria.DatabaseMigrator master-data-gate --connection-env NAME --deployment-class SYNTHETIC|REAL [--gate-007-closed]");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();
}

internal sealed record CommandOptions(string Command, string? ConnectionEnvironment,
    bool ConfirmInitialBaseline, bool AzureOwnershipBridge, MasterDataLoadOptions? MasterData = null,
    MasterDataGateOptions? MasterDataGate = null)
{
    internal static CommandOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new CommandLineException("A command is required.");
        }

        var command = arguments[0].ToLowerInvariant();
        if (command is not ("verify" or "preflight" or "ownership-diagnostic" or "plan" or "apply" or "assert" or "runtime-logins"
            or "master-data-load" or "master-data-gate"))
        {
            throw new CommandLineException($"Unknown command '{arguments[0]}'.");
        }

        string? connectionEnvironment = null;
        var confirm = false;
        var azureOwnershipBridge = false;
        string? file = null;
        string? organization = null;
        var dryRun = false;
        var allowRealDriverProfiles = false;
        string? deploymentClass = null;
        var gate007Closed = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--connection-env" when index + 1 < arguments.Count:
                    connectionEnvironment = arguments[++index];
                    break;
                case "--confirm-initial-baseline":
                    confirm = true;
                    break;
                case "--azure-ownership-bridge":
                    azureOwnershipBridge = true;
                    break;
                case "--file" when command == "master-data-load" && index + 1 < arguments.Count:
                    file = arguments[++index];
                    break;
                case "--organization-id" when command == "master-data-load" && index + 1 < arguments.Count:
                    organization = arguments[++index];
                    break;
                case "--dry-run" when command == "master-data-load":
                    dryRun = true;
                    break;
                case "--allow-real-driver-profiles" when command == "master-data-load":
                    allowRealDriverProfiles = true;
                    break;
                case "--deployment-class" when command == "master-data-gate" && index + 1 < arguments.Count:
                    deploymentClass = arguments[++index];
                    break;
                case "--gate-007-closed" when command == "master-data-gate":
                    gate007Closed = true;
                    break;
                default:
                    throw new CommandLineException($"Unknown or incomplete option '{arguments[index]}'.");
            }
        }

        if (command == "verify" && (connectionEnvironment is not null || confirm || azureOwnershipBridge))
        {
            throw new CommandLineException("verify does not accept connection or confirmation options.");
        }

        if (command != "verify" && string.IsNullOrWhiteSpace(connectionEnvironment))
        {
            throw new CommandLineException($"{command} requires --connection-env NAME.");
        }

        if (command != "apply" && confirm)
        {
            throw new CommandLineException("--confirm-initial-baseline is valid only for apply.");
        }

        if (azureOwnershipBridge && command != "apply")
        {
            throw new CommandLineException("--azure-ownership-bridge is valid only for apply.");
        }

        if (command == "master-data-gate")
        {
            if (deploymentClass is not ("SYNTHETIC" or "REAL"))
            {
                throw new CommandLineException("master-data-gate requires --deployment-class SYNTHETIC or REAL.");
            }

            return new CommandOptions(command, connectionEnvironment, confirm, azureOwnershipBridge,
                MasterDataGate: new MasterDataGateOptions(deploymentClass, gate007Closed));
        }

        if (command != "master-data-load")
        {
            return new CommandOptions(command, connectionEnvironment, confirm, azureOwnershipBridge);
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            throw new CommandLineException("master-data-load requires --file PATH.");
        }

        // The organization is named on the command line and again inside the reviewed document; both must match.
        if (organization is null ||
            !Guid.TryParseExact(organization, "D", out var organizationId) ||
            !string.Equals(organization, organizationId.ToString("D"), StringComparison.Ordinal))
        {
            throw new CommandLineException("master-data-load requires --organization-id as a lowercase UUID.");
        }

        return new CommandOptions(command, connectionEnvironment, confirm, azureOwnershipBridge,
            new MasterDataLoadOptions(file, organizationId, dryRun, allowRealDriverProfiles));
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
