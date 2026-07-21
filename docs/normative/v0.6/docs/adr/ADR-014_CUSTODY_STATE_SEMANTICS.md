# ADR-014 — Cancelación, intentos fallidos y custodia

**Estado:** Aprobado.

Se permite `AT_PICKUP -> CANCELLED` solo antes de adquirir custodia. `FAILED_ATTEMPT` registra etapa y custodia; no puede saltar directamente a `DELIVERED`. Retorno exige custodia adquirida.
