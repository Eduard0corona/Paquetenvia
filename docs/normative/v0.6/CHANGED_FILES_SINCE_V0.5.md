# Archivos consolidados desde v0.5

## Revisión controlada sync 4 para DSP-002

Esta revisión alinea AI-05, AI-08 y AI-10 con el comportamiento incremental de
DSP-002, registra la decisión y actualiza los artefactos de integridad.
AI-06 y AI-18 permanecen byte por byte sin cambios. La adopción EF y sus
pruebas PostgreSQL viven fuera del bundle normativo.

## Revisión controlada sync 3 para ARC-002

Esta revisión modifica exclusivamente AI-06 para remediar purge y actualiza los
artefactos de integridad, trazabilidad y validación que dependen de su identidad.
AI-18 y ADR-030 no cambian. Los ADR-032/033 sólo actualizan la referencia
hardcodeada al hash canónico de AI-06; su decisión v0.7 no cambia.

El detalle exacto de archivos y hashes vigentes está en `MANIFEST.json` y
`CHECKSUMS_SHA256.txt`.

Esta lista compara el contenido de v0.5 con la línea base canónica v0.6 antes de agregar los documentos de entrega de este bundle.

- Modificados: **21**
- Nuevos: **11**
- Sin cambios: **31**
- Eliminados: **0**

## Modificados

- `AI-00_README_FIRST.md`
- `AI-01_AGENT_OPERATING_CONTRACT.md`
- `AI-11_TASK_PROMPT_TEMPLATE.md`
- `CHANGELOG.md`
- `contracts/AI-05_OPENAPI.yaml`
- `contracts/AI-12_SIGNALR_CONTRACT.yaml`
- `database/AI-06_SCHEMA.sql`
- `database/AI-18_DATABASE_ROLE_MODEL.sql`
- `decision-log.md`
- `docs/AI-14_TECH_REFERENCES.md`
- `docs/AI-16_ARCHITECTURE_INDEX.md`
- `docs/adr/ADR-025_WORKER_LEAST_PRIVILEGE.md`
- `specs/AI-02_PRODUCT_CONTRACT.yaml`
- `specs/AI-03_ARCHITECTURE.md`
- `specs/AI-04_DOMAIN_MODEL.yaml`
- `specs/AI-07_UI_CONTRACTS.yaml`
- `specs/AI-08_BACKLOG.yaml`
- `specs/AI-10_DECISIONS_AND_GATES.yaml`
- `specs/AI-13_DOTNET_SOLUTION_BLUEPRINT.md`
- `specs/AI-15_SCALABILITY_CONTRACT.yaml`
- `tests/AI-09_ACCEPTANCE_TESTS.feature`

## Nuevos

- `docs/AI-25_V0.6_CHANGESET.md`
- `docs/AI-26_VALIDATION_REPORT.md`
- `docs/AI-27_RELEASE_SUMMARY_V0.6.md`
- `docs/adr/ADR-026_EXTENSION_PLACEMENT_TOKEN_HASH.md`
- `docs/adr/ADR-027_OUTBOX_LEASE_LIFECYCLE.md`
- `docs/adr/ADR-028_PUBLIC_TRACKING_PROJECTION.md`
- `docs/adr/ADR-029_ORDER_ACCEPTANCE_EVIDENCE.md`
- `docs/adr/ADR-030_OUTBOX_RETENTION_MAINTENANCE.md`
- `docs/adr/ADR-031_RLS_PROVISIONING.md`
- `specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml`
- `tools/validate_contracts.py`

## Eliminados

- Ninguno.

## Sin cambios

- `docs/AI-17_REVIEW_REMEDIATION.md`
- `docs/AI-19_VERIFICATION_REPORT_V0.4.md`
- `docs/AI-21_V0.5_CHANGESET.md`
- `docs/AI-22_VALIDATION_REPORT.md`
- `docs/AI-23_RELEASE_SUMMARY_V0.5.md`
- `docs/adr/ADR-001_MODULAR_MONOLITH.md`
- `docs/adr/ADR-002_CLEAN_ARCHITECTURE_MODULES.md`
- `docs/adr/ADR-003_REST_SIGNALR_AUTHORITY.md`
- `docs/adr/ADR-004_TRANSACTIONAL_OUTBOX.md`
- `docs/adr/ADR-005_MULTI_TENANCY_RLS.md`
- `docs/adr/ADR-006_POSTGRES_POSTGIS.md`
- `docs/adr/ADR-007_REDIS_NON_AUTHORITATIVE.md`
- `docs/adr/ADR-008_OBJECT_STORAGE_EVIDENCE.md`
- `docs/adr/ADR-009_PROGRESSIVE_SCALING.md`
- `docs/adr/ADR-010_DOTNET_SIGNALR_BACKEND.md`
- `docs/adr/ADR-011_QUOTE_ORDER_SNAPSHOT.md`
- `docs/adr/ADR-012_FORCE_RLS_DATABASE_ROLES.md`
- `docs/adr/ADR-013_ORGANIZATION_MEMBERSHIPS.md`
- `docs/adr/ADR-014_CUSTODY_STATE_SEMANTICS.md`
- `docs/adr/ADR-015_DRIVER_LOCATION_INGESTION.md`
- `docs/adr/ADR-016_APPEND_ONLY_AND_OUTBOX_PRIVILEGES.md`
- `docs/adr/ADR-017_RLS_BOOTSTRAP.md`
- `docs/adr/ADR-018_PHYSICAL_MODULE_SCHEMAS.md`
- `docs/adr/ADR-019_TRANSACTION_LOCAL_TENANT_CONTEXT.md`
- `docs/adr/ADR-020_SIGNED_POD_UPLOAD.md`
- `docs/adr/ADR-021_PRICING_SNAPSHOT_GUARDS.md`
- `docs/adr/ADR-022_LOCATION_TELEMETRY_LANE.md`
- `docs/adr/ADR-023_HTTP_VISIBILITY_POLICY.md`
- `docs/adr/ADR-024_CLAIM_WINDOW_FINALIZATION.md`
- `prototype/index.html`
- `specs/AI-20_REMEDIATION_PLAN_V0.4.yaml`
