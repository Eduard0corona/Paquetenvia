# Reporte de liberación interna MVP-0

## Estado

Este documento resume la evidencia y la decisión de REL-000. No sustituye
ninguna de sus dos fuentes autoritativas:

- los cuatro JSON históricos preservados byte por byte son la fuente de
  evidencia técnica;
- `docs/releases/mvp-0-owner-decision.json` es la fuente de aprobación humana;
- este Markdown sólo explica la correlación entre ambas capas.

```text
release = MVP-0_INTERNAL
approved_evidence_main_sha = 3b23a26d97e31424ba023aa4ecf204142ece0445
approved_workflow_run_id = 30750187893
approved_workflow_run_attempt = 1
approved_artifact_id = 8834236041
approved_artifact_digest = sha256:66f8465a79fd4f082cc715724087f507bb9b528fc57a31aa1241accfab676172
approved_evidence_storage = VERSIONED_REDACTED_SNAPSHOT
approved_evidence_snapshot_manifest_path = docs/releases/evidence/rel-000-owner-001/approved-evidence-manifest.json
approved_evidence_snapshot_file_count = 4
approved_evidence_live_artifact_required = false
approved_artifact_original_expires_at = 2026-08-16T13:38:41Z
dependency_security_status = PASSED
technical_evidence_status = PASSED
owner_approval_status = APPROVED
release_candidate_status = APPROVED_FOR_MVP0_INTERNAL
```

El SHA del commit que contiene la decisión se registra dinámicamente como
`decision_record_sha`; no reemplaza `approved_evidence_main_sha`.

## Decisión del project owner

```text
decision_id = REL-000-OWNER-001
decision_statement = Apruebo REL-000
decision_reason = Aprobación explícita del project owner posterior al cierre técnico y a la validación completa de la evidencia REL-000.
decided_by = project_owner
decided_on = 2026-08-02
```

La decisión aprueba exclusivamente `MVP-0_INTERNAL` con datos sintéticos. No
autoriza piloto, producción, deployment, go-live, clientes reales, PII real,
pricing real, pagos, facturación, repartidores externos, SLA, RPO productivo o
PITR. EXT-001 no comenzó y no se inicia automáticamente por esta aprobación.

## Evidencia técnica aprobada

La evidencia aprobada corresponde a Foundation CI `30750187893`, attempt 1,
ejecutada sobre `3b23a26d97e31424ba023aa4ecf204142ece0445` con 13/13 jobs en
`success`. El artifact `8834236041`, nombre
`rel000-mvp0-internal-release-evidence`, tiene digest:

```text
sha256:66f8465a79fd4f082cc715724087f507bb9b528fc57a31aa1241accfab676172
```

El ZIP contiene exclusivamente:

- `rel000-internal-release-report.json`;
- `rel000-p0-evidence.json`;
- `rel000-cross-tenant-evidence.json`;
- `rel000-rollback-evidence.json`.

Su reporte registra seguridad `PASSED`, evidencia técnica `PASSED`, provenance
de diez producers en attempt 1, `aggregator_attempt=1` y
`mixed_attempt_evidence=false`. Históricamente registra 29 elementos evaluados,
28 `VERIFIED`, REL-000 como único `BLOCKED` y owner approval `PENDING`.

## Preservación durable de la evidencia

El artifact histórico expira originalmente el `2026-08-16T13:38:41Z`. Mientras
seguía vivo se descargó una sola vez, se verificó el digest del ZIP y se
capturaron sus cuatro JSON sin reformatearlos ni regenerarlos. El ZIP y la
metadata cruda de GitHub no se versionan.

El snapshot durable vive en
`docs/releases/evidence/rel-000-owner-001/` y su manifest conserva el ID, run,
attempt, SHA, digest y expiración originales. Los archivos preservados son:

| Archivo | Bytes | SHA-256 |
| --- | ---: | --- |
| `rel000-cross-tenant-evidence.json` | 27804 | `d3a9dc7343ddb2c9f00e3484e89789c639ee555c3fd17e2aff81c047515e5bcd` |
| `rel000-internal-release-report.json` | 17749 | `ed30fbbad93369aece753be930e1b6b9d51e12a219e5e9e7560e74082ce68a92` |
| `rel000-p0-evidence.json` | 54623 | `5db1e0a0e8c4759198e55c1a8be6f4d9090a9ca6edb1e3cb807f8812ecad7525` |
| `rel000-rollback-evidence.json` | 18565 | `267e01eb480349eefb50060d9ee8fe2c7c5dc78ca812a543a2e547f93af37eeb` |

Foundation CI ya no consulta ni descarga `8834236041`. La expiración queda
registrada como provenance histórica y no invalida el snapshot en el futuro.
La validación normal falla cerrado ante drift del manifest, paths, Git blobs,
allowlist, tamaños, hashes, JSON, contenido técnico, secretos o PII.

## Estado de EXT-001

El schema del artifact histórico no modelaba `ext001_started`; su ausencia no se
interpreta como `false`. La fuente autoritativa separada es:

```text
approved_evidence_ext001_source_path = tests/fixtures/rel-000/item-evidence.json
approved_evidence_ext001_source_sha = 3b23a26d97e31424ba023aa4ecf204142ece0445
approved_evidence_ext001_source_blob_sha = 5bdf2c7845aceb84806f3cc0f0fdf3bcc6bbe9ae
approved_evidence_ext001_started = false
technical_artifact_contains_ext001_started = false
ext001_state_source = VERSIONED_ITEM_EVIDENCE
```

Ambas fuentes están ancladas al mismo SHA aprobado. La nueva evidencia de la
rama modela explícitamente `ext001_started=false` dentro de
`rel000-internal-release-report.json`, sin crear un quinto archivo.

## Inventario MVP-0/P0

La decisión válida permite la transición fail-closed:

```text
P0 expected = 29
P0 evaluated = 29
P0 verified = 29
P0 blocked = 0
blocked_ids = []
REL-000 = VERIFIED
MVP-0 approved = YES, limitado a MVP-0_INTERNAL
```

Sólo cambia REL-000. Los otros 28 elementos permanecen byte por byte iguales
en el fixture de atribución. FIN-001 continúa en MVP-1 y EXT-001 permanece no
iniciado.

## Separación y validación fail-closed

El validador rechaza un decision record ausente, inválido, duplicado, no
versionado en el HEAD o con cualquier drift de ID, statement, razón, actor,
fecha, SHA, run, attempt, artifact o digest. También rechaza:

- snapshot ausente, no versionado, enlazado, con manifest, tamaños, hashes o
  contenido diferente;
- seguridad o evidencia técnica distinta de `PASSED`;
- Issues #5 o #30 abiertos;
- fuente EXT-001 ausente, blob distinto, schema inválido, tipo no booleano o
  valor `true`;
- cualquier ampliación a piloto, producción o uso de datos reales;
- aprobación inferida por CI, merge, artifact o metadata del PR.

Una variable de entorno no concede aprobación: el runner exige las rutas
canónicas del registro, manifest y directorio versionados, comprueba que sus
bytes pertenecen al source HEAD y valida las fuentes aprobadas antes de aplicar
la transición. La validación de expiración y acceso vivo sólo pertenece al
comando administrativo de captura inicial.

## Rollback REL-000

El rollback es administrativo y no destructivo:

1. revertir el decision record, el cambio de REL-000 en el fixture y la capa de
   validación de aprobación;
2. conservar la evidencia técnica histórica, los doce jobs productores y sus
   artifacts;
3. conservar aplicaciones, migrations, datos, backups y claves;
4. no ejecutar DDL ni eliminar evidencia externa.

La decisión no altera retroactivamente PR #35, run `30750187893`, artifact
`8834236041` ni sus cuatro JSON.

## Estado de la rama de decisión

La aprobación es `APPROVED IN BRANCH EVIDENCE`. Será final en `main` sólo tras
revisión independiente, autorización separada para promover/fusionar, Foundation
CI verde sobre el nuevo `main` y validación del artifact resultante.

No merge. No auto-merge. EXT-001 no iniciado.
