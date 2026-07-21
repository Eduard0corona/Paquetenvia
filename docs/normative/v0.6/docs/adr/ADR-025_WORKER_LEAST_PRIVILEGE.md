# ADR-025 — Worker sujeto a RLS y elevación por función

**Estado:** Aprobado

`paqueteria_worker` es NOBYPASSRLS. Reclama mensajes cross-tenant mediante funciones propiedad de `paqueteria_outbox_executor NOLOGIN BYPASSRLS`; después procesa cada mensaje dentro del contexto tenant. Cualquier mantenimiento elevado exige función/consumer/ADR específico.


## Adenda v0.6

El Worker no tiene `SELECT`, `UPDATE` ni `DELETE` directo sobre outbox. Claim, settle y requeue se ejecutan mediante funciones propiedad de `paqueteria_outbox_executor`.

La eliminación irreversible se separa en `paqueteria_maintenance`, limitado por ADR-030 a funciones de purga de estados terminales antiguos.
