namespace Orders.Application;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(global::Orders.Domain.AssemblyReference),
        typeof(Paqueteria.Application.AssemblyReference),
    ];
}
