using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Paqueteria.ContractTests")]
[assembly: InternalsVisibleTo("Paqueteria.UnitTests")]
// TEN-003-PLATFORM-BOOTSTRAP-2026-10-19: the platform-bootstrap command enters the tenant execution state of its
// own transaction, so the shared append-only audit writer checks the audited organization as it does in the API.
[assembly: InternalsVisibleTo("Paqueteria.DatabaseMigrator")]
