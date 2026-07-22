using System.Text.RegularExpressions;
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

            if (options.Command == "verify")
            {
                PrintVerifiedBaseline(baseline);
                return 0;
            }

            var connectionString = ReadConnectionString(options.ConnectionEnvironment!);
            var deployer = new DatabaseBaselineDeployer();
            switch (options.Command)
            {
                case "plan":
                    var plan = await deployer.PlanAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                    PrintPlan(plan);
                    return plan.State.Status == DatabaseBaselineStatus.Partial ? 4 : 0;

                case "apply":
                    if (!options.ConfirmInitialBaseline)
                    {
                        Console.Error.WriteLine("Apply requires --confirm-initial-baseline.");
                        return 2;
                    }

                    var result = await deployer.ApplyAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                    PrintApplyResult(result);
                    return 0;

                case "assert":
                    var report = await deployer.AssertAsync(baseline, connectionString, cancellation.Token).ConfigureAwait(false);
                    PrintAssertionReport(report);
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

        var value = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException($"Environment variable '{environmentName}' is not set.");
        }

        return value;
    }

    private static void PrintVerifiedBaseline(VerifiedDatabaseBaseline baseline)
    {
        Console.WriteLine($"Baseline {baseline.Version}: VERIFIED");
        foreach (var step in baseline.Steps)
        {
            Console.WriteLine($"{step.Order:D4} {step.Id} {step.RelativePath} sha256={step.Sha256[..12]}...");
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
        "Usage: Paqueteria.DatabaseMigrator <verify|plan|apply|assert> [--connection-env NAME] [--confirm-initial-baseline]");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();
}

internal sealed record CommandOptions(string Command, string? ConnectionEnvironment, bool ConfirmInitialBaseline)
{
    internal static CommandOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            throw new CommandLineException("A command is required.");
        }

        var command = arguments[0].ToLowerInvariant();
        if (command is not ("verify" or "plan" or "apply" or "assert"))
        {
            throw new CommandLineException($"Unknown command '{arguments[0]}'.");
        }

        string? connectionEnvironment = null;
        var confirm = false;
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
                default:
                    throw new CommandLineException($"Unknown or incomplete option '{arguments[index]}'.");
            }
        }

        if (command == "verify" && (connectionEnvironment is not null || confirm))
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

        return new CommandOptions(command, connectionEnvironment, confirm);
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
