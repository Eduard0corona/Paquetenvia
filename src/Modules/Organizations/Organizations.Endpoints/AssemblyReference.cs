namespace Organizations.Endpoints;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Organizations.Application.AssemblyReference),
    ];
}
