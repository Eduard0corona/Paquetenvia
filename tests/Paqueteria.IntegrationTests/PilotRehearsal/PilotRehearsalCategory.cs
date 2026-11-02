namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// The test category of the REL-001 synthetic rehearsal (REL-001-PILOT-READINESS-2026-11-02, Z4 and Z5). Only the
/// on-demand <c>pilot-readiness.yml</c> workflow runs it ("Solo en el ensayo"): every <c>dotnet</c> job of Foundation CI,
/// PR Validation and the development push smoke excludes it, and no other job selects it.
/// </summary>
internal static class PilotRehearsalCategory
{
    internal const string Name = "PilotRehearsal";
}
