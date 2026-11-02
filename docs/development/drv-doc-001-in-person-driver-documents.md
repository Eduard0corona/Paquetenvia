# DRV-DOC-001: documentos del repartidor registrados en persona

Decisión: `DRV-DOC-001-IN-PERSON-2026-11-02` (ronda Y, Y1 a Y12). ADR:
[`docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md`](../adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md). Ítem
de backlog: DRV-DOC-001 (AI-08), puerta GATE-007. **Apagado por defecto y apagado en el piloto** hasta que el owner
registre GATE-007.

## Qué hace

El repartidor muestra el original a despacho de su empresa. En "Repartidores", despacho registra el tipo, la fecha
impresa (o, para una licencia permanente de Sinaloa, la fecha de revisión) y lo aprueba o lo rechaza con un motivo de
una lista fija. No se toma ni se guarda ninguna foto, copia ni número. El repartidor ve sus documentos en "Cuenta". Al
vencer, el repartidor deja de ser elegible solo (DSP-001); nada se marca EXPIRED. Despacho puede revocar un documento
aprobado: el repartidor ya no recibe pedidos nuevos, lo que lleva sigue y despacho reasigna si quiere.

Secuencia de PR: PR-1 (este) ADR, contratos, migración y API; PR-2 el panel (columna "Documentos" y diálogos); PR-3 la
lista de solo lectura en la app del repartidor.

## Superficie

| Operación (AI-05) | Ruta | Quién |
| --- | --- | --- |
| `listDriverDocuments` | `GET /api/v1/drivers/documents` | DISPATCHER; PLATFORM_ADMIN con MFA |
| `recordDriverDocument` | `POST /api/v1/drivers/{driverId}/documents` | DISPATCHER; PLATFORM_ADMIN con MFA; `Idempotency-Key` |
| `revokeDriverDocument` | `POST /api/v1/drivers/{driverId}/documents/{documentId}/revoke` | DISPATCHER; PLATFORM_ADMIN con MFA |
| `listMyDriverDocuments` | `GET /api/v1/driver/me/documents` | DRIVER (los suyos) |
| `registerMyDriverDocument` | `POST /api/v1/driver/me/documents` | Reservada, no implementada |

FINANCE, VIEWER y cualquier otro rol reciben el 403 uniforme antes de leer nada. El orden es forma (409
`INVALID_REQUEST`), capacidad (403, `MFA_REQUIRED` si solo falta el segundo factor) y estado. Un repartidor o documento
desconocido, mal formado, de otra organización, retirado (`INACTIVE`) o que no es `OWN` es el 404 uniforme. Todas las
respuestas llevan `Cache-Control: no-store`.

Cuerpo de `recordDriverDocument` (exactamente uno de estos):

```json
{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PRINTED_EXPIRY","expires_on":"2027-03-01"}
{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PERMANENT_LICENSE_REVIEW"}
{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"ILLEGIBLE"}
```

Códigos 409: `INVALID_REQUEST`, `IDEMPOTENCY_CONFLICT`, `DOCUMENT_TYPE_NOT_REQUIRED` (el vehículo no lo exige),
`EXPIRY_DATE_OUT_OF_RANGE` (último día válido antes de hoy o a más de diez años) y `DOCUMENT_NOT_REVOCABLE`. 503 mientras
`Drivers:Documents:Enabled` sea falso.

## Datos

`drivers.driver_documents` (paso del carril Drivers `20261102000100_AddInPersonDriverDocuments`, mismo DDL en AI-06):

- `evidence_kind`: `FILE` (archivo con SHA-256, la forma de DSP-001) o `IN_PERSON` (sin `object_key` ni `sha256`).
- `expiry_basis`: `PRINTED_EXPIRY` o `PERMANENT_LICENSE_REVIEW` (solo licencia). `expires_at` es las 00:00 de
  `America/Mazatlan` del día siguiente al último día válido.
- `rejection_code`: `ILLEGIBLE`, `EXPIRED`, `WRONG_DOCUMENT`, `HOLDER_MISMATCH`, `INCOMPLETE`, `NOT_ACCEPTED`.
- `reviewed_by`, `reviewed_at`, `revoked_by`, `revoked_at`.

DSP-001 sigue tomando la fila más reciente por tipo (`created_at DESC, id DESC`) y verifica la evidencia según
`evidence_kind`. Auditoría: `DRIVER_DOCUMENT_RECORDED` y `DRIVER_DOCUMENT_REVOKED`, solo tipos y códigos. Las escrituras
tocan solo `drivers.driver_documents`, `platform.audit_logs` y `platform.idempotency_keys`.

## Configuración

| Setting | Default | Notas |
| --- | --- | --- |
| `Drivers:Documents:Enabled` | `false` | Apagado: las listas responden `documents_enabled=false` sin leer nada; registrar y revocar, 503 |
| `Drivers:Documents:Gate007DecisionId` | — | `GATE-007-…`; obligatorio con `Enabled` fuera de Development, Testing y DEV_SYNTHETIC, y siempre con `PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE` |

Con `Enabled`, el API tampoco arranca si `Drivers:Provider` no es `PostgreSql`, si un tipo exigido no es `IDENTITY`,
`DRIVER_LICENSE`, `VEHICLE_CARD` o `INSURANCE`, o si `NonExpiringDocumentTypes` tiene algo. En el piloto
`Drivers__Documents__` es configuración administrada por `apps.bicep`.

## Pasos del owner antes de encenderlo

1. Resolver GATE-007 con el aviso de privacidad que describa este registro (los borradores de `docs/legal/` ya lo
   anotan como pendiente) y el plazo de retención (propuesto: vigencia + 1 año); registrar la fila `GATE-007-…`.
2. Avisar a despacho que no tome fotos ni copias: solo anota tipo y fecha.
3. Encender con un PR que agregue a `apps.bicep` `Drivers__Documents__Enabled=true` y
   `Drivers__Documents__Gate007DecisionId`.

## Pruebas

- Unitarias: `DriverEligibilityPolicyTests` (evidencia por tipo), `DriverDocumentPolicyTests` (calendario, forma del
  cuerpo, hash de la solicitud) y `DriverDocumentsOptionsTests` (puerta de GATE-007 y validación al iniciar).
- PostgreSQL: `DriverDocumentsPostgreSqlContractTests` (catálogo del carril, Up/Down/Up, rollback protegido, RLS y
  aislamiento entre organizaciones, registrar, idempotencia, revocar, listas, 404 uniforme, auditoría en la misma
  transacción y elegibilidad DSP-001 de punta a punta por el camino real, incluido `listAssignableDrivers`).
- HTTP: `DriverDocumentHttpTests` (matriz de autorización, forma antes que capacidad, 404, 503, `no-store`).
- Contrato: `DriverDocumentsContractTests` (AI-05, capacidades y rutas); arquitectura: `CrossSchemaWriteCatalogTests`
  sin cambios.

## Pendientes (fuera de DRV-DOC-001)

- La purga de documentos con la baja del repartidor (Y9: tarea aparte).
- Una carga desde la app (requiere decisión del owner, sesión de carga ADR-020 y GATE-007).
- Perfiles reales: esperan GATE-007 y el job del cargador MDM-001 en el piloto.

## Reversión

`Drivers:Documents:Enabled=false`. El Down del carril se niega mientras haya datos en persona
(`DRVDOC001_DOWNGRADE_BLOCKED_DATA_PRESENT`).
