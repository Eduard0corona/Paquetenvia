namespace ModuleName.Infrastructure;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(ModuleName.Application.AssemblyReference),
        typeof(ModuleName.Domain.AssemblyReference),
        typeof(Paqueteria.Infrastructure.AssemblyReference),
    ];
}
