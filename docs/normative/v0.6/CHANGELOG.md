# Changelog

## Remediación contractual DSP-002 — 2026-07-23

- AI-05 declara 409 para conflictos DSP-002 mediante Problem Details con los
  códigos públicos `INVALID_REQUEST`, `CONFLICT`, `DRIVER_INELIGIBLE` y
  `DRIVER_DOCUMENT_EXPIRED`.
- La matriz de `assignDriver` queda 201/401/403/404/409 conforme a AI-04 y
  ADR-023; orden o conductor ausente/cross-tenant usa 404 uniforme.
- `route_id` es nullable, pero DSP-002 solo admite ausencia o `null` hasta
  RTE-001.
- El vocabulario global conserva `OWN`, `EXTERNAL`, `ALLY_CAPACITY`; DSP-002
  habilita solo `OWN`, EXT-001 reserva `EXTERNAL` y ALY-004 reserva
  `ALLY_CAPACITY`.
- AI-06 y AI-18 permanecen intactos. La adopción de Dispatch se endurece para
  rechazar drift de checks, FKs, acciones, índice parcial, RLS y policy.
- La decisión queda registrada como `DSP-002-CONTRACT-REMEDIATION`.

## Resolución de gates — 2026-07-21

- GATE-002 (proveedor de identidad) RESUELTO: se adopta AuthCenter, servidor OIDC propio, con SLA comprometido de 99.5%, hospedaje en Azure Mexico Central y custodia de llave en Azure Key Vault.
- Migración verificada de access tokens a RS256: los consumidores validan contra JWKS público sin poseer material de firma.
- Separación normativa registrada: AuthCenter autentica, Paquetería autoriza la tenencia por organización.
- Pendiente antes de producción: rotación de llaves, rotación de la llave de desarrollo previamente embebida y confirmación de topología para el SLA.
- Actualización únicamente documental: no modifica contratos normativos v0.6, SQL, roles, endpoints ni código.

## Revisión canónica v0.6 sync 3 — 2026-07-21

- ARC-002 pasa de `PARTIAL` a `DONE` después de que la remediación normativa y
  los cinco jobs del PR pasaran.
- `purge_outbox` y `purge_location_outbox` ya no usan `FOR UPDATE SKIP LOCKED`.
- El `DELETE` revalida ID, estado terminal y cutoff sobre la fila objetivo.
- ADR-030 se conserva: maintenance continúa con `SELECT,DELETE`, sin `UPDATE`.
- AI-18 permanece sin cambios y conserva su checksum.
- La ejecución canónica se alinea a PostgreSQL 18/PostGIS 3.6.
- Purga real, límites, idempotencia y concurrencia se validan en Testcontainers.
- Bundle emitido como `v0.6-full-canonical-sync-3-arc002-purge-remediation`.

## Registro de decisiones v0.7 — 2026-07-21

- ADR-032 aceptado como referencia de diseño para v0.7: tracking en vivo, `PICKUP_IN_PROGRESS` y verificación segura de entrega.
- ADR-033 aceptado como referencia de diseño para v0.7: inventario de sellos de integridad y cadena de custodia física.
- Se registran también el contrato conjunto del guard ADR-032↔ADR-033 y su plan de pruebas SQL/Testcontainers.
- Esta actualización es únicamente aditiva y documental: no modifica contratos normativos v0.6, SQL, roles, endpoints, migraciones ni código.
- La implementación continúa bloqueada por GATE-007/GATE-013, el delta normativo coordinado y la ejecución de pruebas en PostgreSQL/PostGIS real.

## v0.6 — 2026-07-20

- `pgcrypto` aislado en `extensions`; PostGIS permanece en `public` protegido.
- Token de tracking con contrato SHA-256 simétrico C#/SQL sobre UTF-8 exacto.
- Outbox business/GPS con `lease_token`, settle, stale requeue, dead-letter y purga.
- Runtime sin SELECT/UPDATE/DELETE directo sobre outbox; inserts sin `RETURNING`.
- Estados públicos normativos y timeline privado por defecto con `public_event_code`.
- `order_acceptances` RLS/append-only con canonicalización legal fija.
- Todo dinero migrado a `bigint`/OpenAPI `int64`.
- Provisioning transaccional de usuarios/organizaciones bajo RLS.
- `external_offers` recupera aceptante, fecha y versión.
- Lane GPS sin FK y con `UNIQUE(driver_position_id)`.
- OPS-004 y ADR-026 a ADR-031.
- Backlog ampliado a 56 tareas.

## v0.5 — 2026-07-20

- Roles runtime NOBYPASSRLS; bootstrap y claim cross-tenant mediante funciones de roles NOLOGIN dedicados.
- Grants append-only y mutabilidad por columna del outbox.
- Contexto tenant transaction-local, retry-safe y preparado para PgBouncer.
- Esquema físico por módulo y FKs cross-schema gobernadas.
- Cotización de uso único, `pricing_tier`, piso congelado y snapshot sin PII.
- Modelo multi-ciudad inicial.
- POD por URL firmada/cuarentena y GPS batch con lane separado.
- Política 401/403/404, COD mínimo, líneas de liquidación y ventana de reclamación.
- Backlog ampliado a 53 tareas y nuevos ADR-016 a ADR-025.


## v0.4 — 2026-07-20

- Corrige contrato Quote→Order con ubicaciones y snapshots persistidos.
- Introduce membresías y roles por organización.
- Endurece RLS con FORCE, roles separados y políticas para tablas sensibles/hijas.
- Corrige máquina de estados de cancelación, custodia y reintentos.
- Añade ingesta REST de posiciones y contrato SignalR consistente.
- Corrige OpenAPI, idempotencia, audit schema y `Order.version`.
- Corrige FK de tarifas, índice de outbox y nombres de tablas de escalabilidad.
- Corrige numeración ADR, plantilla de lectura y referencia a documentos inexistentes.
- Backlog: 48 tareas, incluyendo ARC-002 y DRV-003; OBS-001 asciende a P0.

## v0.3 — 2026-07-19

- Arquitectura y escalabilidad progresiva formalizadas.
