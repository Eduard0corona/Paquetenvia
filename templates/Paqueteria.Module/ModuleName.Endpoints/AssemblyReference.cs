namespace ModuleName.Endpoints;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(ModuleName.Application.AssemblyReference),
    ];
}
