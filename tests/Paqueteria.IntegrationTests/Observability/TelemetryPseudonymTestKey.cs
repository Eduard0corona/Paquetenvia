using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// OBS-003 PR 6: outside Development and Testing the API and the Worker refuse to start without the organization
/// pseudonym key. Hosts that run as Production, Staging or DevSynthetic in a test get this synthetic key: SHA-256 of a
/// public label, derived when the test runs, so no key-like value is written in the repository.
/// </summary>
internal static class TelemetryPseudonymTestKey
{
    private const string Label = "paquetenvia.integration-tests.synthetic-telemetry-pseudonym-key";

    /// <summary>The configuration values of the synthetic key, version 1.</summary>
    internal static IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["TelemetryPseudonym:CurrentKeyVersion"] = "1",
        ["TelemetryPseudonym:Keys:1"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Label))),
    };

    /// <summary><paramref name="settings"/> plus the synthetic key (the explicit settings win).</summary>
    internal static Dictionary<string, string?> With(IReadOnlyDictionary<string, string?> settings)
    {
        var merged = new Dictionary<string, string?>(Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            merged[key] = value;
        }

        return merged;
    }
}
