namespace Organizations.Application;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Organizations.Domain.AssemblyReference),
        typeof(Paqueteria.Application.AssemblyReference),
    ];
}
