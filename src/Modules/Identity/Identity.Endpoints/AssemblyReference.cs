using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Paqueteria.UnitTests")]

namespace Identity.Endpoints;

public static class AssemblyReference
{
    internal static Type[] Dependencies =>
    [
        typeof(Identity.Application.AssemblyReference),
    ];
}
