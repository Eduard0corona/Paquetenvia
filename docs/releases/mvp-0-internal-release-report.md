# Reporte de decisión interna MVP-0

## Estado del paquete

Este documento acompaña la evidencia automatizada de REL-000. No representa
una aprobación del propietario.

```text
format_version = paquetenvia-rel000-v1
release = MVP-0_INTERNAL
base_main_sha = b091b6126cda1536c55b55be88dfb76bc618bdec
normative_version = 0.6
rel000_def_001_status = RESOLVED
normative_scope_status = RESOLVED
normative_scope_decision = FIN001_MOVED_TO_MVP1
dependency_security_status = BLOCKED
owner_approval_status = PENDING
release_candidate_status = BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION
```

El estado técnico final de cada corrida sólo es autoritativo en
`rel000-internal-release-report.json`, generado con artifacts OPS-001, OPS-002
y resultados estructurados por test de esa misma corrida. La documentación no
reemplaza la correlación dinámica de SHAs, run, attempt, artifact ID y digests.

## Inventario MVP-0/P0

AI-08 contiene 29 items con `release=MVP-0` y `priority=P0`, incluido REL-000.
La evaluación de la base fusionada es:

```text
P0 expected = 29
P0 evaluated = 29
P0 verified = 28
P0 blocked = 1
blocked_ids = ["REL-000"]
```

- `VERIFIED` (28): ARC-001, ARC-002, AUD-001, DBA-001, DRV-001, DRV-002,
  DRV-003, DSP-001, DSP-002, FND-001, FND-002, GEO-001, OBS-001, OPS-001,
  OPS-002, ORD-001, ORD-002, POD-001, PRC-001, PRC-002, RTM-001, RTM-002,
  SEC-001, SEC-002, TEN-001, TEN-002, TEN-003 y TRK-001.
- `PARTIAL` (0): ninguno.
- `NOT_STARTED` (0): ninguno dentro de la evidencia atribuible evaluada.
- `BLOCKED` (1): REL-000.

REL-000 permanece bloqueado por la deuda de seguridad heredada y por la
aprobación final pendiente del propietario; no se cuenta como aprobado.
FIN-001 pertenece a MVP-1 y por ello no aparece en este inventario, sin que se
infiera implementado o verificado a partir de los guards COD existentes.

## REL-000-DEF-001

Estado: **RESOLVED**.

El project owner aprobó la opción A el 2026-07-31. FIN-001 pasa completo a
MVP-1, conserva prioridad P0 y las dependencias DSP-002, EXT-001 y RTE-001. No
existe una variante mínima de FIN-001 dentro de MVP-0. REL-000 deja de depender
normativamente de FIN-001; EXT-001 continúa dependiendo de REL-000 y no queda
autorizado ni iniciado. La decisión no constituye aprobación de REL-000 o
MVP-0_INTERNAL.

```text
resolution = FIN001_MOVED_TO_MVP1
resolved_by = project_owner
resolved_on = 2026-07-31
```

## Cobertura cross-tenant

El manifest cerrado contiene 25 categorías: identidad, organización activa,
RLS transaccional, pooling/retry, provisioning, memberships, quotes, orders,
acceptances, transiciones, drivers, assignments, stops, ubicación, ambos
outbox, OperationsHub, DriverHub, tracking, dashboard, POD, proofs, auditoría,
restore y tenant señuelo.

Los conteos executed/passed sólo se derivan de resultados estructurados que
coinciden exactamente en job, proyecto, nombre completo, categoría, SHA, run y
attempt, con `executed=true`, `outcome=PASSED` y `skipped=false`. La fuente y su
marker son una guardia adicional, no evidencia de ejecución. Missing, failed,
skipped e incidents deben ser cero.

## OPS-001 y OPS-002

OPS-001 se acepta únicamente desde `delivery-simulation-results` de la misma
corrida. Exige 20 entregas, 180 eventos sin huecos, realtime 160/160, auditoría
340/340, tenant secundario cero, recuperación de lease, poison aislado, dos
corridas consecutivas y cancelación real seguida de recuperación completa.

OPS-002 se acepta únicamente desde `ops002-backup-restore-results` de la misma
corrida. Exige restore green, 70/70 guardas, pérdida observada cero, RPO no
establecido, target limpio, source destruido, restart real, RLS, append-only
completo antes/después, objetos íntegros y cero plaintext residual.

Los producers registran provenance por archivo. El agregador valida nombres,
IDs, digest de upload, digest de contenido, run, attempt, source HEAD, tested
SHA, base main y relación Git.

Los jobs que sustentan el manifest publican artifacts internos de ejecución
derivados de TRX, JUnit o JSON del runner real. Esos inputs no forman parte del
artifact público REL-000: la allowlist final conserva exactamente cuatro JSON y
excluye TRX, JUnit y resultados raw.

## Gates y riesgos abiertos

GATE-002 permanece resuelto por la decisión ya registrada en AI-10. Continúan
abiertos:

```text
GATE-001
GATE-003
GATE-004
GATE-005
GATE-006
GATE-007
GATE-008
GATE-009
GATE-010
GATE-011
GATE-012
GATE-013
GATE-014
GATE-015
GATE-016
GATE-017
RTM-001-CUSTOMER-SUPPORT-ROLE
Issue #5
Issue #30
```

REL-000-DEF-001 figura en `resolved_decisions`; no es un gate abierto.

Issue #5 sigue abierto. El baseline aceptado es un finding high heredado de
`sharp 0.34.5` (GHSA-f88m-g3jw-g9cj); no cubre los once findings actuales.
Issue #30 registra los nueve advisories de Next.js y el advisory de
brace-expansion.

| Fuente | Total | High | Moderate | Alcance |
|---|---:|---:|---:|---|
| Issue #5 | 1 | 1 | 0 | sharp |
| Base main real | 11 | 6 | 5 | next, sharp, brace-expansion |
| Rama REL-000 | 11 | 6 | 5 | sin cambios de dependencias |
| Delta de la rama | 0 | 0 | 0 | ninguna regresión |

Los 11 advisories son deuda de seguridad preexistente heredada de `main`.
`audit_tracking_gap_detected=true` y `audit_tracking_gap_count=10` expresan la
diferencia entre el alcance específico de Issue #5 y el audit real; Issue #30
mantiene esos diez findings bajo seguimiento. El gate valida ambos audits,
IDs, severidades, rangos afectados/parchados, paths y disponibilidad de fix.
Falla ante cualquier empeoramiento, critical, omisión, salida no parseable o
cambio de manifests/lockfile. REL-000 no actualiza paquetes ni agrega
overrides.

## Rollback matrix

Cada item MVP-0/P0 cuenta con una fila estructurada en
`tests/fixtures/rel-000/rollback-evidence.json`; la salida validada es
`rel000-rollback-evidence.json`. Las 29 filas tienen referencia, comando y
prueba. FIN-001 no aparece porque pertenece a MVP-1; no se afirma que sus
rollbacks funcionales fueron ejecutados. La matriz distingue referencia
declarada de ejecución demostrada y no afirma que cada comando individual fue
ejecutado.

| Componentes | Items | Tipo | Preserva datos | Estado |
|---|---|---|---:|---|
| Foundation y arquitectura | FND-001, ARC-001, ARC-002, FND-002 | revert de tooling/código | sí | VERIFIED |
| Seguridad y tenancy | SEC-001, SEC-002, TEN-001, TEN-002, TEN-003, DBA-001, AUD-001 | revert de capa aplicativa/tooling | sí | VERIFIED |
| Geografía, pricing y órdenes | GEO-001, PRC-001, PRC-002, ORD-001, ORD-002 | revert de capa aplicativa | sí | VERIFIED |
| Drivers y dispatch | DSP-001, DSP-002, DRV-001, DRV-002, DRV-003 | revert aplicativa/UI | sí | VERIFIED |
| Realtime, tracking y dashboard | RTM-001, RTM-002, TRK-001, OBS-001 | revert aplicativa/worker/UI | sí | VERIFIED |
| POD y operaciones | POD-001, OPS-001, OPS-002 | revert aplicativa/test tooling | sí | VERIFIED |
| Gate REL-000 | REL-000 | revert no destructivo del gate | sí | VERIFIED |

La matriz no autoriza rollback de datos, DDL inverso ni eliminación de backups,
keys o artifacts externos.

## Rollback REL-000

Para retirar exclusivamente REL-000:

- retirar el job agregador y provenance de la evidencia fuente;
- retirar tooling, manifests y documentos REL-000;
- conservar los doce jobs previos y toda funcionalidad MVP-0;
- conservar migrations, datos y artifacts externos;
- no ejecutar DDL;
- no borrar backups ni keys;
- no modificar `docs/normative/v0.6/`.

Las pruebas focales ejecutan 76 casos Python, dos guardas físicas y 14 escenarios
end-to-end del wrapper/generador. Demuestran que una generación
fallida/cancelada no publica un reporte exitoso, que se eliminan staging/output
parciales, que un output previo o evidencia de otro SHA/run/manifest se rechaza,
que cleanup no sigue enlaces y que inputs y destinos ajenos se preservan. Los
conteos del JSON final se calculan a partir de lo realmente
descubierto/ejecutado/pasado.

## Restricciones de liberación

MVP-0_INTERNAL usa únicamente datos sintéticos.
No autoriza PII real.
No autoriza cotizaciones reales.
No autoriza pricing público.
No autoriza mensajes a clientes.
No autoriza repartidores externos.
No autoriza piloto.
No autoriza producción.
No resuelve hosting ni residencia.
No establece SLA.
No establece RPO productivo.
No implementa PITR.
No inicia EXT-001.

Technical evidence prepared.
Owner approval pending.
REL-000 not approved.
EXT-001 not started.
No merge.
No auto-merge.
