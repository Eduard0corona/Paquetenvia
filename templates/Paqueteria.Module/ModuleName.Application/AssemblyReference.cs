namespace ModuleName.Application;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(ModuleName.Domain.AssemblyReference),
        typeof(Paqueteria.Application.AssemblyReference),
    ];
}
