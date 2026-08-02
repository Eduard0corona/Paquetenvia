# REL-000: puerta de liberación interna MVP-0

## Objetivo

REL-000 reúne evidencia técnica trazable para que el propietario de producto y
tecnología decida sobre `MVP-0_INTERNAL`. No agrega funcionalidad de producto y
no sustituye la decisión humana. El resultado técnico continúa bloqueando la
liberación; el estado del propietario permanece `PENDING`.

La normativa canónica es `docs/normative/v0.6/`. El gate la lee y verifica sus
checksums, pero nunca la modifica. El inventario MVP-0/P0 se deriva de
`AI-08_BACKLOG.yaml`; las decisiones abiertas, GATE-002 y REL-000-DEF-001
resueltas, y el alcance permitido se derivan de
`AI-10_DECISIONS_AND_GATES.yaml`.

## Límites de autorización

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

Tampoco crea endpoints, hubs, módulos de negocio, tablas, migrations, DDL,
seeds productivos, proveedores reales, secretos ni credenciales.

## Precondiciones

La rama REL-000 sólo puede partir de `main` después de comprobar:

- PR #29 fusionado desde
  `dfac04d00a7de640dac13d6db5a2066baf3faba7`, sin auto-merge;
- OPS-002 y OPS-001 aceptados como ancestros de `origin/main`;
- Foundation CI 30384725882 con sus 12 jobs en `success`;
- worktree limpio;
- `git pull --ff-only` en `main`.

La base registrada para la sincronización normativa de REL-000-DEF-001 es
`b091b6126cda1536c55b55be88dfb76bc618bdec`.

## Inventario P0 y correlación exacta

`tools/rel-000/rel000.py` selecciona directamente todos los elementos de
AI-08 con `release=MVP-0` y `priority=P0`. La línea base v0.6 sync 7 exige
exactamente 29, incluido REL-000, y 28 anteriores al gate. El input no puede sustituir IDs:
faltantes, duplicados, desconocidos o dependencias inexistentes fallan con una
razón estructurada.

FIN-001 permanece en el backlog global como `release=MVP-1`, `priority=P0` y
con el conjunto exacto de dependencias DSP-002, EXT-001 y RTE-001. El gate
rechaza cualquier drift con `FIN001_RELEASE_CLASSIFICATION_INVALID`,
`FIN001_PRIORITY_INVALID` o `FIN001_DEPENDENCY_SET_INVALID`; FIN-001 dentro del
manifest MVP-0 se rechaza como `P0_ITEM_UNKNOWN`.

`tests/fixtures/rel-000/item-evidence.json` sólo aporta atribución de evidencia.
El validador vuelve a cargar título, release, prioridad y dependencias desde
AI-08. Un elemento `VERIFIED` exige:

- implementación fusionada cuyo SHA sea ancestro de la base `main`;
- paths de implementación y tests existentes;
- jobs autoritativos dentro de un vocabulario cerrado y con resultado `success`;
- una identidad ejecutable exacta por cada fuente requerida, correlacionada con
  job, proyecto, categoría, source HEAD, tested SHA, run y attempt actuales;
- referencia de rollback;
- criterios verificables y limitaciones visibles.

El vocabulario por item es cerrado: `VERIFIED`, `PARTIAL`, `NOT_STARTED`,
`BLOCKED` y `NOT_APPLICABLE`. La existencia de un archivo por sí sola nunca
marca un item como `VERIFIED`.

## REL-000-DEF-001

El project owner resolvió la clasificación el 2026-07-31 mediante la opción A:
FIN-001 se mueve completo a MVP-1, sin dividir alcance ni crear una variante
financiera mínima en MVP-0. Conserva prioridad P0 y las dependencias DSP-002,
EXT-001 y RTE-001. EXT-001 continúa dependiendo de REL-000 y no queda
autorizado.

La resolución se registra como:

```text
REL-000-DEF-001
resolution = FIN001_MOVED_TO_MVP1
resolved_by = project_owner
resolved_on = 2026-07-31
```

La resolución normativa no constituye aprobación final:

```text
rel000_def_001_status = RESOLVED
normative_scope_status = RESOLVED
normative_scope_decision = FIN001_MOVED_TO_MVP1
owner_approval_status = PENDING
dependency_security_status = BLOCKED
```

La herramienta no incluye FIN-001 en el artifact MVP-0/P0, no lo marca como
implementado o verificado, no toma guards parciales de COD como unit economics
y no inicia EXT-001 ni RTE-001.

## Fuentes cross-tenant

`tests/fixtures/rel-000/cross-tenant-evidence.json` es un manifest cerrado de
25 categorías. Cada entrada declara ID, job, proyecto, nombre de test completo,
categoría, fuente y marcador de presencia. El gate comprueba explícitamente:

- identidad y organización activa;
- RLS transaccional, pooling/retry y provisioning;
- memberships, quotes, orders, acceptances y transiciones;
- perfiles/documentos, assignments, stops y ubicación de driver;
- outbox de negocio y ubicación;
- OperationsHub y DriverHub;
- tracking público y dashboard;
- sesiones POD, proofs y auditoría;
- backup/restore y tenant señuelo de OPS-001.

El job autoritativo debe estar en `success` y el resultado estructurado de la
misma corrida debe demostrar `executed=true`, `outcome=PASSED` y
`skipped=false` para esa identidad exacta. La presencia de fuente/marker se
conserva sólo como guardia estructural adicional y nunca produce `PASSED`.
TRX vacío, malformado, stale, de otro proyecto/categoría, ausente, skipped,
fallido o contradictorio falla cerrado. La salida distingue expected,
executed, passed, missing, failed, skipped e incidents. No publica IDs de
usuarios, organizaciones, órdenes, drivers, tokens ni objetos.

## Evidencia OPS-001

El job agregador descarga `delivery-simulation-results` de su propia corrida.
El producer agrega provenance con run, attempt, SHAs, relación Git y SHA-256 por
archivo. `actions/download-artifact` valida el digest del artifact y REL-000
vuelve a validar el digest canónico de contenido.

El validador exige los conteos exactos de 20 órdenes, assignments y ambos POD;
180 eventos sin versiones faltantes; correlación realtime 160/160 sin faltantes,
inesperados ni inconsistentes; 340/340 auditorías sin faltantes, duplicados ni
inconsistencias; tenant secundario en cero; poison/recovery/lease correctos.

También analiza el TRX y exige las dos pruebas: dos corridas consecutivas, y
cancelación real seguida de una corrida completa. Un conteo adicional nunca
compensa un ID faltante.

## Evidencia OPS-002

El job agregador descarga `ops002-backup-restore-results` de la misma corrida y
valida su provenance. El input sólo admite JSON redactado y `.tar.gz.age`.

Se exigen `RESTORE_DRILL_PASSED`, 70/70 guardas, pérdida observada cero, RPO
`NOT_ESTABLISHED`, destrucción de source, target limpio, baseline/migrations,
RLS, append-only, objetos, exclusión de Redis/Mailpit y ausencia de plaintext.
El contrato append-only completo se valida antes y después del restart: cuatro
tablas, permisos, triggers, UPDATE, DELETE, ocho fallos `42501`, cero fallos de
permisos, ocho filas intactas y mensaje calificado.

El artifact REL-000 no duplica el backup cifrado.

## Trazabilidad Git y CI

En pull request:

```text
source_head_sha = github.event.pull_request.head.sha
tested_git_sha = github.sha
git_relationship = source_head_is_ancestor_of_tested_commit
```

En local, source y tested son el HEAD actual y la relación es `same_commit`.
`base_main_sha` se registra por separado. Se rechazan SHAs cortos o ausentes,
checkout distinto, source no ancestro, base no ancestro, artifact de otro run,
attempt o SHA y archivo no declarado en provenance.

El job `Validate MVP-0 internal release evidence` usa `if: always()` y depende
de los doce jobs autoritativos. Primero comprueba que todos terminaron en
`success`; luego descarga los artifacts actuales y los artifacts internos de
ejecución. Estos últimos contienen TRX/JUnit/JSON estructurado, y se validan
contra artifact ID, upload digest, content digest, run, attempt, source HEAD,
tested SHA y base SHA. Después consulta Issue #5 e Issue #30, captura audits
reproducibles de la base fija y de la rama sin publicar su JSON crudo, ejecuta
las pruebas focales y genera cuatro JSON redactados.

## Artifact publicado

Nombre:

```text
rel000-mvp0-internal-release-evidence
```

Retención: 14 días. Allowlist:

```text
rel000-internal-release-report.json
rel000-p0-evidence.json
rel000-cross-tenant-evidence.json
rel000-rollback-evidence.json
```

No se publican env files, identities, dumps, SQL, backups duplicados, logs de
contenedores, TRX/JUnit, resultados raw, tokens, URLs firmadas, object keys,
connection strings, payloads ni IDs sintéticos de entidades.

## Seguridad física, fallo y cleanup

El wrapper reutiliza las guardas físicas de OPS-002. Staging y output deben
resolver fuera del repositorio, sin reparse points, symlinks, junctions ni
ancestros enlazados. La identidad física se comprueba de nuevo antes de copiar
o eliminar.

La generación ocurre primero en staging externo. Sólo después de validar todo
se copian los cuatro archivos permitidos a un output externo nuevo. Un fallo o
cancelación no publica un reporte exitoso; limpia parciales. Un output anterior
se rechaza para impedir que se confunda con la corrida actual. El cleanup
rechaza enlaces descendientes y no los sigue.

## Ejecución local

Windows PowerShell 5.1 ejecuta únicamente las 76 pruebas Python:

```powershell
powershell -File ./tools/test-rel-000-internal-release.ps1 -PythonOnly
```

La generación completa requiere los artifacts actuales de OPS-001 y OPS-002,
Issue #5, Issue #30 y ambos audits sanitizados, metadata de artifact, resultados
de los jobs y SHAs completos. CI configura esas entradas. El host local
registra exactamente `physical_path_tests_local=NOT_EXECUTED` y
`physical_path_tests_local_reason=POWERSHELL_7_UNAVAILABLE`. Las dos pruebas
físicas y los 14 escenarios end-to-end de rollback son obligatorios en CI con
PowerShell 7. Los conteos `focused_tests_*` se derivan de los resultados Python
y físicos realmente descubiertos/ejecutados; un caso no descubierto produce
`REL000_FOCUSED_TEST_COUNT_MISMATCH`. Las suites Testcontainers no deben
ejecutarse en paralelo.

Validación complementaria:

```powershell
python ./docs/normative/v0.6/tools/validate_contracts.py
python ./tools/rel-000/test_rel000.py
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm --dir apps/web install --frozen-lockfile
pnpm --dir apps/web lint
pnpm --dir apps/web typecheck
pnpm --dir apps/web test
pnpm --dir apps/web build
```

Docker habilita además infraestructura, PostgreSQL/Testcontainers, SignalR,
PWA, dashboard, OPS-001 y OPS-002. Si el host no puede ejecutarlos, el reporte
debe decirlo y usar la CI limpia como autoridad.

## Gates, Issues #5/#30 y audit

GATE-002 se carga como resuelto. GATE-001, GATE-003 a GATE-017 y
RTM-001-CUSTOMER-SUPPORT-ROLE permanecen abiertos según AI-10. Los advisories
de dependencias se relacionan de forma exhaustiva con Issue #5 o Issue #30;
`audit_tracking_gap_detected=false` y `audit_tracking_gap_count=0`.

La política versionada `tools/rel-000/security-remediation-policy.json` separa
dos modos explícitos. `NORMAL_RELEASE_EVIDENCE` rechaza cualquier cambio en los
archivos de dependencias y exige que el audit de la rama sea idéntico al de su
base real. `SECURITY_REMEDIATION` sólo está autorizado para la rama
`fix/security-next-sharp-brace-expansion` sobre la base exacta
`1ac8054026b3e4cb06612001f2be053d351fd2cf` y permite únicamente los tres
archivos web declarados por la política.

La base autorizada contiene 11 advisories únicos: 6 high y 5 moderate. La rama
actualiza `next` y `eslint-config-next` de 16.2.10 a 16.2.11, eliminando los
nueve advisories de Next.js. Los overrides segmentados mantienen
`brace-expansion` dentro de los rangos de todos sus consumidores: 1.1.17 para
la rama 1.x y 5.0.8 para la rama 5.x. No existe override cross-major.

Next.js 16.2.11 declara `sharp ^0.34.5`; ese rango excluye 0.35.x. El audit de
la rama conserva por ello sólo GHSA-f88m-g3jw-g9cj en `sharp 0.34.5`:

```text
sharp_remediation_status = BLOCKED_BY_UPSTREAM_COMPATIBILITY
issue_30_remediation_status = REMEDIATED_PENDING_MERGE
dependency_security_status = BLOCKED
release_candidate_status = BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION
owner_approval_status = PENDING
```

El modo de remediación falla ante base distinta, audit omitido o no parseable,
critical, advisory o paquete nuevo, aumento de severidad, issue cerrado con su
advisory presente, archivo no autorizado, lockfile inconsistente, prerelease,
override incompatible o versión vulnerable duplicada. La suite focal deriva su
conteo de los tests descubiertos; la corrida actual exige y pasa 100/100, sin
fallos ni skips.

## Rollback REL-000

El rollback de este bloque es no destructivo:

1. retirar el job agregador y el provenance agregado a los producers;
2. retirar `tools/test-rel-000-internal-release.ps1`, `tools/rel-000/`,
   `tests/fixtures/rel-000/` y la documentación REL-000;
3. conservar toda funcionalidad MVP-0, migrations, datos y artifacts externos;
4. no ejecutar DDL, no borrar backups, no eliminar keys y no modificar la
   normativa v0.6.

La matriz completa se conserva en el reporte de liberación y en
`rel000-rollback-evidence.json`. Cada fila distingue
`rollback_reference_verified` de `rollback_execution_verified`: la presencia de
un comando no afirma que ese rollback individual haya sido ejecutado. Para el
gate REL-000, 14 escenarios lanzan el wrapper/generador real en subprocess,
verifican cancelación/fallo en distintas fases, rechazos por SHA/run/manifest,
guardas contra links, limpieza de staging/output y preservación de inputs y
destinos ajenos.
