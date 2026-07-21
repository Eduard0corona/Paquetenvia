# ADR-027 — Lifecycle del outbox protegido por lease

**Estado:** Aprobado

Claim asigna `lease_token`/expiración. Settle requiere token vigente y estado `PROCESSING`; requeue recupera leases vencidos y envía poison messages a `DEAD`.

Runtime no tiene `SELECT/UPDATE/DELETE` directo sobre los lanes. Productores insertan valores generados por aplicación sin `RETURNING`. Claim/settle/requeue pertenecen a `paqueteria_outbox_executor`.
