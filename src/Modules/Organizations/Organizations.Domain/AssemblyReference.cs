namespace Organizations.Domain;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Paqueteria.Domain.AssemblyReference),
    ];
}
