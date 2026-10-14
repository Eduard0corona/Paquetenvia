namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>
/// The test category of the OPS-005 suite (OPS-005-SUITE-IN-INTEGRATIONTESTS-2026-10-14: the suite lives in this
/// project under its own category). The Foundation and PR Validation <c>dotnet</c> jobs (and the development push smoke)
/// exclude it; the <c>driver-stops-pwa</c> job runs it, where PostgreSQL, the storage container, the web dependencies
/// and Chromium are already set up.
/// </summary>
internal static class PlatformEndToEndCategory
{
    internal const string Name = "PlatformEndToEnd";
}
