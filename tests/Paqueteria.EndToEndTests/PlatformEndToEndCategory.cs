namespace Paqueteria.EndToEndTests;

/// <summary>
/// The test category of the OPS-005 suite. The Foundation and PR Validation <c>dotnet</c> jobs (and the development
/// push smoke) exclude it; the <c>driver-stops-pwa</c> job runs it, where PostgreSQL, the storage container, the web
/// dependencies and Chromium are already set up.
/// </summary>
internal static class PlatformEndToEndCategory
{
    internal const string Name = "PlatformEndToEnd";
}
