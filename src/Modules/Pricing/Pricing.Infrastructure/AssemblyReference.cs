namespace Pricing.Infrastructure;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Pricing.Application.AssemblyReference),
        typeof(Pricing.Domain.AssemblyReference),
        typeof(Paqueteria.Infrastructure.AssemblyReference),
    ];
}
