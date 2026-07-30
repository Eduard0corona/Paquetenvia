# REL-000: puerta de liberación interna MVP-0

## Objetivo

REL-000 reúne evidencia técnica trazable para que el propietario de producto y
tecnología decida sobre `MVP-0_INTERNAL`. No agrega funcionalidad de producto y
no sustituye la decisión humana. El máximo resultado técnico del gate es
`READY_FOR_OWNER_DECISION`; el estado del propietario permanece `PENDING`.

La normativa canónica es `docs/normative/v0.6/`. El gate la lee y verifica sus
checksums, pero nunca la modifica. El inventario MVP-0/P0 se deriva de
`AI-08_BACKLOG.yaml`; las decisiones abiertas, la decisión resuelta GATE-002 y
el alcance permitido se derivan de `AI-10_DECISIONS_AND_GATES.yaml`.

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

La base registrada para este bloque es
`9418504bec0b2eab96ff6c2f3cebb54048482a1f`.

## Inventario P0 y correlación exacta

`tools/rel-000/rel000.py` selecciona directamente todos los elementos de
AI-08 con `release=MVP-0` y `priority=P0`. La línea base v0.6 exige exactamente
30, incluido REL-000, y 29 anteriores al gate. El input no puede sustituir IDs:
faltantes, duplicados, desconocidos o dependencias inexistentes fallan con una
razón estructurada.

`tests/fixtures/rel-000/item-evidence.json` sólo aporta atribución de evidencia.
El validador vuelve a cargar título, release, prioridad y dependencias desde
AI-08. Un elemento `VERIFIED` exige:

- implementación fusionada cuyo SHA sea ancestro de la base `main`;
- paths de implementación y tests existentes;
- jobs autoritativos;
- referencia de rollback;
- criterios verificables y limitaciones visibles.

El vocabulario por item es cerrado: `VERIFIED`, `PARTIAL`, `NOT_STARTED`,
`BLOCKED` y `NOT_APPLICABLE`. La existencia de un archivo por sí sola nunca
marca un item como `VERIFIED`.

## REL-000-DEF-001

AI-08 clasifica FIN-001 como MVP-0/P0 y lo hace depender de EXT-001 y RTE-001.
EXT-001 depende a su vez de REL-000, mientras REL-000 exige todos los P0
completos. La interpretación literal forma un ciclo de secuencia que el código
no puede resolver sin cambiar la normativa.

El bloqueo se registra como:

```text
REL-000-DEF-001
Clasificación inconsistente de FIN-001 dentro de MVP-0
```

Hasta una decisión explícita:

```text
normative_scope_status = BLOCKED_BY_OWNER_DECISION
owner_approval_status = PENDING
```

La herramienta no marca FIN-001 como completo, no toma los guards parciales de
COD como unit economics, no inicia EXT-001 ni RTE-001 y no altera AI-08.

## Fuentes cross-tenant

`tests/fixtures/rel-000/cross-tenant-evidence.json` es un manifest cerrado de
25 categorías. Cada entrada declara ID, job, proyecto, filtro, fuente y marcador
de presencia. El gate comprueba explícitamente:

- identidad y organización activa;
- RLS transaccional, pooling/retry y provisioning;
- memberships, quotes, orders, acceptances y transiciones;
- perfiles/documentos, assignments, stops y ubicación de driver;
- outbox de negocio y ubicación;
- OperationsHub y DriverHub;
- tracking público y dashboard;
- sesiones POD, proofs y auditoría;
- backup/restore y tenant señuelo de OPS-001.

El job autoritativo debe estar en `success`, la fuente y su marcador deben
existir y ninguna categoría requerida puede aparecer como skipped. La salida
distingue expected, executed, passed, missing, failed, skipped e incidents. No
publica IDs de usuarios, organizaciones, órdenes, drivers, tokens ni objetos.

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
`success`; luego descarga los artifacts actuales, consulta Issue #5 e Issue
#30, captura audits reproducibles de la base fija y de la rama sin publicar su
JSON crudo, ejecuta las pruebas focales y genera cuatro JSON redactados.

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
contenedores, tokens, URLs firmadas, object keys, connection strings, payloads
ni IDs sintéticos de entidades.

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

Windows PowerShell 5.1 ejecuta únicamente las 49 pruebas Python:

```powershell
powershell -File ./tools/test-rel-000-internal-release.ps1 -PythonOnly
```

La generación completa requiere los artifacts actuales de OPS-001 y OPS-002,
Issue #5, Issue #30 y ambos audits sanitizados, metadata de artifact, resultados
de los jobs y SHAs completos. CI configura esas entradas. El host local
registra exactamente `physical_path_tests_local=NOT_EXECUTED` y
`physical_path_tests_local_reason=POWERSHELL_7_UNAVAILABLE`. Las dos pruebas
físicas son obligatorias en CI con PowerShell 7 y deben producir
`physical_path_tests_ci=PASSED`. Las suites Testcontainers no deben ejecutarse
en paralelo.

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

## Gates, Issue #5 y audit

GATE-002 se carga como resuelto. GATE-001, GATE-003 a GATE-017 y
RTM-001-CUSTOMER-SUPPORT-ROLE permanecen abiertos según AI-10. Issue #5 se
consulta en GitHub y debe permanecer abierto. El baseline aceptado es un
finding high de `sharp 0.34.5`, GHSA-f88m-g3jw-g9cj, y sólo ese finding.

El audit real de la base fija contiene 11 advisories: 6 high y 5 moderate;
nueve pertenecen a Next.js, uno a sharp y uno a brace-expansion. Issue #30
mantiene abiertos los diez no cubiertos por Issue #5. La rama debe ejecutar su
propio audit y mantener delta cero, conjunto idéntico y diff limpio para
`apps/web/package.json`, `apps/web/pnpm-lock.yaml` y
`apps/web/pnpm-workspace.yaml`.

Ese baseline exacto permite generar evidencia técnica, pero siempre emite:

```text
dependency_security_status = BLOCKED
release_candidate_status = BLOCKED_BY_SECURITY_ADVISORIES_AND_OWNER_DECISION
```

El gate falla si la rama empeora respecto de la base real, aparece un critical,
falta un advisory, el audit se omite/no se parsea, cambia un archivo de
dependencias, se oculta el gap de diez o se presenta Issue #5 como cobertura de
los once. REL-000 no actualiza paquetes ni agrega overrides.

## Rollback REL-000

El rollback de este bloque es no destructivo:

1. retirar el job agregador y el provenance agregado a los producers;
2. retirar `tools/test-rel-000-internal-release.ps1`, `tools/rel-000/`,
   `tests/fixtures/rel-000/` y la documentación REL-000;
3. conservar toda funcionalidad MVP-0, migrations, datos y artifacts externos;
4. no ejecutar DDL, no borrar backups, no eliminar keys y no modificar la
   normativa v0.6.

La matriz completa se conserva en el reporte de liberación y en
`rel000-rollback-evidence.json`.
