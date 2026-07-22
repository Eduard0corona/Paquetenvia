namespace Organizations.Infrastructure;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Organizations.Application.AssemblyReference),
        typeof(Organizations.Domain.AssemblyReference),
        typeof(Paqueteria.Infrastructure.AssemblyReference),
    ];
}
