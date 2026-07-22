namespace Identity.Application;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Identity.Domain.AssemblyReference),
        typeof(Paqueteria.Application.AssemblyReference),
    ];
}
