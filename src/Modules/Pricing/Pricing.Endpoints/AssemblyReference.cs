namespace Pricing.Endpoints;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Pricing.Application.AssemblyReference),
        typeof(Organizations.Application.AssemblyReference),
        typeof(Organizations.Endpoints.AssemblyReference),
        typeof(Paqueteria.Application.AssemblyReference),
    ];
}
