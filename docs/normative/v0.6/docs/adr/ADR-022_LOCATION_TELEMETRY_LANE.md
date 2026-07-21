# ADR-022 — Lane independiente para telemetría GPS

**Estado:** Aprobado

El endpoint recibe lotes de 1–20 posiciones. Toda posición válida se persiste, pero solo puntos significativos/throttled generan `location_outbox_events`. El consumer, claim, lag, retención y particionamiento son independientes del outbox de negocio.
