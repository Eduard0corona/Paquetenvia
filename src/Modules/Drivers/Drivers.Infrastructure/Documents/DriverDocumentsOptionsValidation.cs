using System.Text.RegularExpressions;
using Drivers.Application.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;

namespace Drivers.Infrastructure.Documents;

/// <summary>
/// Start validation of <c>Drivers:Documents</c> (DRV-DOC-001-IN-PERSON-2026-11-02, Y2 "Ya, apagado"; AI-24
/// <c>driver_documents.switch</c>). Turning documents on where people are real needs the GATE-007 resolution row: the
/// gate id is required everywhere except Development, Testing and DEV_SYNTHETIC, and always under
/// <c>PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE</c> whatever the environment name. A host without a known environment
/// is treated as real. Enabled also requires the PostgreSQL provider, every required document type to be recordable
/// (Y11) and no non-expiring type (AI-08 DRV-DOC-001: every required document carries expires_at). No failure message
/// carries a configured value.
/// </summary>
internal sealed partial class DriverDocumentsOptionsValidation(IServiceProvider services) : IValidateOptions<DriversOptions>
{
    public const string PilotRealPeopleClass = "PILOT_REAL_PEOPLE";

    public ValidateOptionsResult Validate(string? name, DriversOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var documents = options.Documents ?? new DriverDocumentsOptions();
        var failures = new List<string>();
        var gate = documents.Gate007DecisionId ?? string.Empty;
        if (gate.Length > 0 && !Gate007Pattern().IsMatch(gate))
        {
            failures.Add("Drivers:Documents:Gate007DecisionId must name the decision-log row that resolves GATE-007 (GATE-007-...).");
        }

        if (documents.Enabled)
        {
            if (GateRequired(services.GetService<IHostEnvironment>()) && !Gate007Pattern().IsMatch(gate))
            {
                failures.Add(
                    "Drivers:Documents:Enabled outside Development, Testing and DEV_SYNTHETIC (and always under " +
                    "PILOT_REAL_PEOPLE) requires Drivers:Documents:Gate007DecisionId naming the decision-log row that " +
                    "resolves GATE-007 (DRV-DOC-001-IN-PERSON-2026-11-02).");
            }

            if (options.Provider != DriversProviderKind.PostgreSql)
            {
                failures.Add("Drivers:Documents:Enabled requires Drivers:Provider=PostgreSql.");
            }

            var required = options.Eligibility?.RequiredDocumentTypesByVehicleType ??
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (required.Values.SelectMany(types => types ?? []).Any(type => !DriverDocumentVocabulary.RecordableTypes.Contains(type)))
            {
                failures.Add(
                    "Drivers:Documents:Enabled requires every required document type to be IDENTITY, DRIVER_LICENSE, " +
                    "VEHICLE_CARD or INSURANCE (DRV-DOC-001-IN-PERSON-2026-11-02 Y11).");
            }

            if (options.Eligibility?.NonExpiringDocumentTypes is { Count: > 0 })
            {
                failures.Add(
                    "Drivers:Documents:Enabled requires Drivers:Eligibility:NonExpiringDocumentTypes to stay empty " +
                    "(every required document carries an expiry date).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// The gate id is required unless the data is synthetic for sure: PILOT_REAL_PEOPLE always requires it, DEV_SYNTHETIC
    /// never does, and otherwise only Development, Testing and DevSynthetic are exempt.
    /// </summary>
    internal static bool GateRequired(IHostEnvironment? environment)
    {
        var deploymentClass = Environment.GetEnvironmentVariable(SyntheticEnvironmentPolicy.DeploymentClassVariable);
        if (string.Equals(deploymentClass, PilotRealPeopleClass, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(deploymentClass, SyntheticEnvironmentPolicy.DeploymentClass, StringComparison.Ordinal))
        {
            return false;
        }

        return environment is null ||
            !(environment.IsDevelopment() ||
              environment.IsEnvironment("Testing") ||
              environment.IsEnvironment(SyntheticEnvironmentPolicy.EnvironmentName));
    }

    [GeneratedRegex("^GATE-007-[A-Z0-9][A-Z0-9-]{2,76}$", RegexOptions.CultureInvariant)]
    internal static partial Regex Gate007Pattern();
}
