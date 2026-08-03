# REL-000: puerta de liberación interna MVP-0

## Objetivo

REL-000 separa evidencia técnica trazable y decisión humana versionada para
`MVP-0_INTERNAL`. No agrega funcionalidad de producto. La evidencia técnica
permanece inmutable y la aprobación sólo se aplica cuando el registro
`paquetenvia-mvp0-owner-decision-v1` supera todas las guardas fail-closed.

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
contra artifact ID, upload digest, content digest, run, producer attempt,
source HEAD, tested SHA y base SHA. Después consulta Issue #5 e Issue #30, captura audits
reproducibles de la base fija y de la rama sin publicar su JSON crudo, ejecuta
las pruebas focales y genera cuatro JSON redactados.

### Provenance en reruns parciales

`GITHUB_RUN_ATTEMPT` identifica el intento del agregador. Cada artifact conserva
por separado el `workflow_run_attempt` del job que lo produjo. En un rerun
parcial ambos valores pueden ser distintos: un job no reejecutado conserva su
artifact del intento anterior, mientras que un job reejecutado debe aportar el
artifact de su ejecución más reciente.

Los diez artifacts autoritativos usan nombres estables y `overwrite: true` al
publicarse. GitHub Actions trata cada upload como inmutable: si un productor se
reejecuta, el upload elimina el artifact anterior con ese nombre y crea uno
nuevo, con un artifact ID y un upload digest nuevos. De este modo la consulta
del run y los outputs del productor sólo pueden asociar REL-000 con la evidencia
del intento vigente. El ID anterior queda obsoleto después del rerun y se
rechaza como `WORKFLOW_PROVENANCE_ARTIFACT_STALE_AFTER_RERUN`; un productor no
reejecutado conserva legítimamente su ID, digest y producer attempt previos.
El reemplazo no vuelve válida la evidencia de una ejecución fallida: el último
producer elegible debe seguir en `completed/success`, por lo que cualquier
producer fallido bloquea REL-000. Al conservar un único nombre estable y aceptar
sólo los outputs ID/digest del último producer elegible, nunca quedan dos
artifacts autoritativos elegibles para el mismo producer.

El artifact diagnóstico
`realtime-e2e-failure-results-attempt-<N>` mantiene un nombre por intento y no
usa reemplazo. Nunca forma parte del mapeo autoritativo ni puede satisfacer la
provenance de `rel000-execution-realtime-e2e`.

El workflow consulta de forma autenticada la metadata del run, cada endpoint de
attempt/jobs y los artifacts del run con `per_page=100`. La tabla fija de trece
jobs relaciona cada key interno con un único nombre de Foundation CI. Para cada
intento, `run_started_at` distingue ejecuciones nuevas de resultados retenidos:
un resultado anterior al inicio del intento debe coincidir de forma inequívoca
con una ejecución real ya observada. La lógica pura de `rel000.py` no accede a
la red.

Las respuestas GitHub crudas viven únicamente bajo `RUNNER_TEMP`. Se reducen al
manifiesto versionado `paquetenvia-rel000-workflow-provenance-v1`, que conserva
run, source SHA, aggregator attempt, jobs ejecutados y la asociación exacta de
job key, job name, job ID, producer attempt, artifact name, artifact ID y
digest. El cleanup elimina tanto las respuestas crudas como el manifiesto
sanitizado. El job sólo dispone de `actions: read`, `contents: read` e
`issues: read`.

Para cada productor se acepta exclusivamente el mayor intento realmente
ejecutado. Su estado debe ser `completed/success`, y la evidencia interna debe
declarar ese mismo producer attempt. Faltantes, duplicados, intentos futuros,
evidencia anterior a una reejecución, IDs/digests contradictorios, jobs
desconocidos o metadata incompleta son errores fail-closed, nunca warnings.

El reporte público añade `execution_provenance` con el aggregator attempt, el
indicador `mixed_attempt_evidence` y la lista sanitizada de producer attempts e
IDs/digests. No incluye respuestas GitHub crudas ni URLs de descarga. El
artifact público conserva exactamente los mismos cuatro JSON.

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

Si Vitest falla en `Validate real SignalR reconnect`, una ruta condicionada por
`failure()` conserva `TestResults/realtime-e2e` durante siete días como
`realtime-e2e-failure-results-attempt-<N>`. Es evidencia diagnóstica: nunca
satisface REL-000, nunca sustituye `rel000-execution-realtime-e2e` y no cambia
el resultado fallido del job.

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

Windows PowerShell 5.1 ejecuta únicamente las pruebas Python focales descubiertas dinámicamente:

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

La política v2 `tools/rel-000/security-remediation-policy.json` separa el
registro histórico ya fusionado de PR #33 y las autorizaciones activas.
`NORMAL_RELEASE_EVIDENCE` rechaza cualquier dependency drift. La autorización
`ISSUE-5-SHARP-035-REMEDIATION` exige simultáneamente la rama
`fix/security-sharp-035-override`, la base exacta
`78117e6551b3f758dd82190d3fae325a95dc14c6`, Issue #5, el único advisory base y
la allowlist exacta. Una rama parecida, otro ID o una base distinta no activa
el modo de remediación.

El descubrimiento upstream del 2026-08-02 confirmó que Next.js 16.2.12 es
estable, pero todavía declara la optional dependency `sharp ^0.34.5`; por ello
no existe una ruta estable normal hacia Sharp 0.35.x. Sharp 0.35.3 es estable y
parchea `GHSA-f88m-g3jw-g9cj`. La ruta seleccionada es el resultado B: el
selector pnpm exacto `next@16.2.11>sharp` fija Sharp 0.35.3 únicamente para ese
edge. Conserva sin cambios Next.js/eslint-config-next 16.2.11, React/React DOM
19.2.7 y los overrides previos de PostCSS y brace-expansion.

Fuentes primarias consultadas:

- [release de Next.js 16.2.12](https://github.com/vercel/next.js/releases/tag/v16.2.12);
- [`packages/next/package.json` de 16.2.12](https://github.com/vercel/next.js/blob/v16.2.12/packages/next/package.json);
- [advisory GHSA-f88m-g3jw-g9cj](https://github.com/advisories/GHSA-f88m-g3jw-g9cj);
- [issue vercel/next.js#96064](https://github.com/vercel/next.js/issues/96064) y la
  [recomendación del mantenedor](https://github.com/vercel/next.js/issues/96064#issuecomment-5052143831);
- [issue lovell/sharp#4567](https://github.com/lovell/sharp/issues/4567).

La base histórica anterior a la remediación contenía un advisory high en
`sharp 0.34.5`. La base aprobada `3b23a26d97e31424ba023aa4ecf204142ece0445`
ya contiene sólo `sharp 0.35.3`, audit cero e Issues #5/#30 cerrados:

```text
issue_5_remediation_status = REMEDIATED
issue_30_remediation_status = REMEDIATED
sharp_remediation_status = REMEDIATED
dependency_security_status = PASSED
release_candidate_status = APPROVED_FOR_MVP0_INTERNAL
owner_approval_status = APPROVED
```

El smoke test resuelve Sharp desde el paquete Next.js, carga el módulo nativo y
genera una imagen PNG sintética sólo en memoria. Se ejecuta localmente y en
Linux dentro de los jobs `web` y REL-000. No valida el runtime específico de
Vercel. Paquetenvia no importa Sharp, no usa `next/image` ni procesa imágenes en
una Server Action; cualquier uso futuro debe reevaluar `lovell/sharp#4567`.

El modo de remediación falla ante base distinta, audit omitido o no parseable,
critical, advisory o paquete nuevo, aumento de severidad, issue cerrado con su
advisory presente, archivo no autorizado, lockfile inconsistente, prerelease,
override incompatible, versión vulnerable duplicada o smoke test ausente o
fallido. La suite focal deriva su conteo de los tests descubiertos y exige que
todos los casos descubiertos sean ejecutados y aprobados, sin fallos ni skips.

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

## Fase de decisión humana

La fase técnica y la fase humana son independientes:

1. La evidencia técnica aprobada sigue siendo el artifact `8834236041` de
   Foundation CI `30750187893`, attempt 1, sobre
   `3b23a26d97e31424ba023aa4ecf204142ece0445`, con digest
   `sha256:66f8465a79fd4f082cc715724087f507bb9b528fc57a31aa1241accfab676172`.
2. La decisión humana proviene exclusivamente de
   `docs/releases/mvp-0-owner-decision.json`, formato
   `paquetenvia-mvp0-owner-decision-v1`.
3. `approved_evidence_sha` conserva el SHA de la evidencia técnica;
   `decision_record_sha` registra el HEAD que versiona la decisión. Nunca se
   sustituyen entre sí.

### Snapshot durable de la evidencia aprobada

El artifact `8834236041` fue la fuente externa original y expira el
`2026-08-16T13:38:41Z`. Durante una captura administrativa única, mientras
seguía disponible, se validaron su metadata, digest ZIP
`66f8465a79fd4f082cc715724087f507bb9b528fc57a31aa1241accfab676172`,
contenido técnico y escaneo de secretos/PII. Los cuatro JSON se copiaron
byte-for-byte a `docs/releases/evidence/rel-000-owner-001/`; no se versionaron
el ZIP, URLs de descarga, headers ni metadata cruda.

`capture-approved-evidence` es el único comando que consulta la expiración y
exige el ZIP vivo. Rechaza anclas, tamaños, hashes, JSON o contenido distintos,
outputs preexistentes, links/reparse points y limpia cualquier output parcial.
No forma parte del flujo normal de Foundation CI.

La validación normal usa exclusivamente:

```text
REL000_APPROVED_EVIDENCE_MANIFEST_PATH = docs/releases/evidence/rel-000-owner-001/approved-evidence-manifest.json
REL000_APPROVED_EVIDENCE_DIRECTORY = docs/releases/evidence/rel-000-owner-001
REL000_DECISION_RECORD_PATH = docs/releases/mvp-0-owner-decision.json
```

El validador comprueba paths canónicos, cinco Git blobs versionados, allowlist,
ausencia de links o traversal, manifest exacto, cuatro tamaños y hashes, JSON,
provenance, estados técnicos históricos y escaneo de secretos/PII. No usa la
fecha actual: la expiración original es provenance histórica, no una futura
dependencia operacional de CI.

El artifact histórico no contiene `ext001_started` y esa ausencia no se toma
como `false`. La fuente separada es
`tests/fixtures/rel-000/item-evidence.json` en el SHA aprobado, blob
`5bdf2c7845aceb84806f3cc0f0fdf3bcc6bbe9ae`, donde el booleano top-level es
explícitamente `false`. La evidencia nueva publica esta provenance como:

```text
technical_artifact_contains_ext001_started = false
ext001_state_source = VERSIONED_ITEM_EVIDENCE
ext001_state_source_sha = 3b23a26d97e31424ba023aa4ecf204142ece0445
ext001_started = false
```

El runner acepta `-DecisionRecordPath`, `-ApprovedEvidenceManifestPath` y
`-ApprovedEvidenceDirectory`, pero rechaza cualquier ruta distinta de las
ubicaciones canónicas. Una variable de entorno aislada nunca concede
aprobación. El orden es: validar el snapshot técnico histórico y la evidencia
actual, validar el decision record versionado, comprobar la fuente EXT-001,
ejecutar la suite, aplicar la transición y publicar exclusivamente los cuatro
JSON permitidos.

La validación falla ante registro ausente, JSON/schema/ID/statement/razón/fecha
o actor incorrectos, anclas diferentes, snapshot ausente o no versionado,
manifest/tamaños/hashes/contenido/provenance distintos, links o archivos extra,
issues de seguridad abiertos,
seguridad/evidencia técnica no `PASSED`, decisión duplicada, fuente EXT-001
ausente o modificada, o cualquier flag de alcance habilitado.

El estado resultante es:

```text
owner_approval_status = APPROVED
release_candidate_status = APPROVED_FOR_MVP0_INTERNAL
technical_gate_outcome = OWNER_APPROVED_INTERNAL_RELEASE
result = REL000_OWNER_APPROVED
release_scope = MVP-0_INTERNAL
synthetic_data_only = true
MVP-0/P0 = 29/29 VERIFIED
blocked_ids = []
pilot_authorized = false
production_authorized = false
ext001_started = false
```

La aprobación no inicia EXT-001. Sólo lo vuelve elegible para una autorización
posterior, separada y explícita. Tampoco autoriza piloto, producción,
deployment, clientes reales, PII, pricing, pagos, facturación, repartidores
externos, SLA, RPO productivo o PITR.
