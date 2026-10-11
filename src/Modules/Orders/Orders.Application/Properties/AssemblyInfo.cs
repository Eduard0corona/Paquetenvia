using System.Runtime.CompilerServices;

// ORD-002-API-GUARD-REGISTRY: the unit tests build registries over explicit guards through the internal constructor.
[assembly: InternalsVisibleTo("Paqueteria.UnitTests")]
