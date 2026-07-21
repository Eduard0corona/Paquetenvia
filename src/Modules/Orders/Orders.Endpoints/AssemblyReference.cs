namespace Orders.Endpoints;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Orders.Application.AssemblyReference),
    ];
}
