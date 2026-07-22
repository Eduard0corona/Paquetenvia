namespace Identity.Infrastructure;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Identity.Application.AssemblyReference),
        typeof(Identity.Domain.AssemblyReference),
        typeof(Paqueteria.Infrastructure.AssemblyReference),
    ];
}
