namespace Pricing.Application;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Pricing.Domain.AssemblyReference),
        typeof(Paqueteria.Application.AssemblyReference),
    ];
}
