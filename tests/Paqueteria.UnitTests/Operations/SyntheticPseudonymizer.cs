using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// The organization pseudonymizer the Worker composes in Development and Testing when no key is configured: the public
/// synthetic key, version 0 (<c>v0-…</c>). OBS-003-WORKER-PSEUDONYM-2026-10-14: the Worker lanes and proof validation
/// take it as a required dependency.
/// </summary>
internal static class SyntheticPseudonymizer
{
    internal static IOrganizationPseudonymizer Create() =>
        new HmacOrganizationPseudonymizer(Options.Create(new TelemetryPseudonymOptions()), allowsSyntheticKey: true);
}
