namespace Orders.Infrastructure;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(global::Orders.Application.AssemblyReference),
        typeof(global::Orders.Domain.AssemblyReference),
        typeof(Paqueteria.Infrastructure.AssemblyReference),
    ];
}
