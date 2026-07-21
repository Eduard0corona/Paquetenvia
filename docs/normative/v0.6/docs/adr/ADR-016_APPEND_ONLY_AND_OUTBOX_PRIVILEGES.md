# ADR-016 — Privilegios append-only y contenido de outbox

**Estado:** Aprobado

`order_events`, `proofs` y `audit_logs` niegan UPDATE/DELETE a roles runtime. El contenido del outbox es inmutable; únicamente el Worker puede actualizar columnas operativas de procesamiento. Triggers de defensa en profundidad impiden mutaciones de contenido.
