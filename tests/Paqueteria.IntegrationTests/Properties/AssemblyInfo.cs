using System.Runtime.CompilerServices;

// OPS-005: the platform end-to-end suite assembles the API, the Worker and the Web with this project's harness
// (the PostgreSQL and storage fixtures, the Kestrel API host, the Next.js server and the log capture) instead of a copy.
[assembly: InternalsVisibleTo("Paqueteria.EndToEndTests")]
