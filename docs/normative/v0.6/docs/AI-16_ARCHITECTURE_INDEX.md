# Índice de arquitectura para agentes — v0.6

1. `specs/AI-02_PRODUCT_CONTRACT.yaml` — producto y decisiones aprobadas.
2. `specs/AI-03_ARCHITECTURE.md` — arquitectura normativa.
3. `specs/AI-04_DOMAIN_MODEL.yaml` — dominio, estados y seguridad.
4. `specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml` — extensiones, hashing, leases, canonicalización y privilegios runtime.
5. `specs/AI-13_DOTNET_SOLUTION_BLUEPRINT.md` — estructura de solución .NET.
6. `specs/AI-15_SCALABILITY_CONTRACT.yaml` — fases, métricas y particionamiento.
7. `database/AI-06_SCHEMA.sql` — esquema ejecutable.
8. `database/AI-18_DATABASE_ROLE_MODEL.sql` — roles, grants y ownership de funciones.
9. `contracts/AI-05_OPENAPI.yaml` — REST.
10. `contracts/AI-12_SIGNALR_CONTRACT.yaml` — tiempo real.
11. `tests/AI-09_ACCEPTANCE_TESTS.feature` — aceptación normativa.
12. `specs/AI-10_DECISIONS_AND_GATES.yaml` — gates abiertos.
13. `docs/adr/` — decisiones aprobadas.

Jerarquía: Product > Domain/Runtime Security > Architecture > API/SQL/SignalR > Backlog/Tests > ADR explicativos. Una contradicción bloquea la tarea.

## ADR de endurecimiento

- ADR-016 — Append-only y privilegios iniciales.
- ADR-017 — Bootstrap RLS.
- ADR-018 — Esquemas físicos.
- ADR-019 — Contexto tenant transaccional.
- ADR-020 — POD firmado.
- ADR-021 — Snapshots de precio.
- ADR-022 — Lane de telemetría.
- ADR-023 — Política HTTP.
- ADR-024 — Ventana de reclamación.
- ADR-025 — Worker least privilege.
- ADR-026 — Extensiones y hash de tracking.
- ADR-027 — Outbox con lease.
- ADR-028 — Proyección pública fail-closed.
- ADR-029 — Evidencia legal canónica.
- ADR-030 — Retención por maintenance.
- ADR-031 — Provisioning bajo RLS.

## Referencias de diseño aceptadas para v0.7 — no implementadas en v0.6

Estos documentos están aceptados como referencia de diseño y congelados para el futuro delta v0.7. No alteran la jerarquía ni el comportamiento normativo de v0.6 y no autorizan migraciones o implementación.

- `docs/adr/ADR-032_LIVE_TRACKING_AND_SECURE_DELIVERY.md` — tracking live, estado `PICKUP_IN_PROGRESS` y entrega segura.
- `docs/adr/ADR-033_SEAL_INVENTORY_CHAIN_OF_CUSTODY.md` — inventario de sellos y cadena de custodia física.
- `docs/adr/ADR-032-033_COMPOSITE_GUARD_CONTRACT.md` — contrato conjunto del guard de pickup/entrega.
- `docs/adr/ADR-032-033_TEST_PLAN.md` — plan obligatorio SQL/Testcontainers previo a implementación.

