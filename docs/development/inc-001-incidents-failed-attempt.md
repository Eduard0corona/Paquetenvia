# INC-001: incidencias e intento fallido

INC-001 agrega el módulo `Incidents` y el comando autenticado e idempotente
`POST /api/v1/orders/{orderId}/incidents`. La incidencia es la **precondición** del intento
fallido: INC-001 nunca escribe `orders.orders`, de modo que ORD-002 sigue siendo el único autor
del estado de la orden.

Cubre los criterios de aceptación de AI-08: motivo y evidencia obligatorios, siguiente acción
explícita, SLA timestamp y `Idempotency-Key` en la operación de apertura.

No implementa resolución de incidencias, ventana de reclamo, LIF-001, soporte a clientes ni CRM.

## Capas y responsabilidades

- `Incidents.Domain` contiene el vocabulario (estado, severidad, motivo, siguiente acción), la
  política de SLA, la política de estados de orden habilitados y los límites de evidencia. No
  depende de JSON, persistencia ni frameworks.
- `Incidents.Application` define el comando, el resultado, los errores estructurados y la
  validación de forma que rechaza todo criterio obligatorio ausente antes de abrir transacción.
- `Incidents.Infrastructure` ejecuta la transacción tenant, la reserva idempotente, las lecturas
  cross-schema de solo lectura (`orders`, `identity`, `organizations`, `drivers`, `dispatch`,
  `custody`) y las escrituras en `incidents`.
- `Incidents.Endpoints` limita el contrato HTTP a binding, autenticación, tenant activo,
  `Idempotency-Key` y mapeo 201/401/403/404/409/503.

## Modelo y persistencia

La migración `20260922_AdoptCanonicalIncidentsBaseline` adopta la tabla canónica AI-06
`incidents.incidents` y la evoluciona de forma aditiva. Siguiendo el precedente NTF-001, los
objetos que INC-001 introduce viven en la migración del módulo y no en el bundle normativo v0.6
congelado; cada paso está guardado, la migración es idempotente y no elimina ningún objeto ni
fila.

Columnas agregadas a `incidents.incidents`:

| Columna | Tipo | Regla |
| --- | --- | --- |
| `reason_code` | `text NOT NULL` | motivo obligatorio |
| `next_action` | `text NOT NULL` | `CHECK IN ('RESCHEDULED','RETURNING')` |
| `occurred_at` | `timestamptz NOT NULL` | hora del intento reportada por el cliente |
| `sla_due_at` | `timestamptz NOT NULL` | `CHECK (sla_due_at > occurred_at)` |

Tabla nueva `incidents.incident_evidence`, con `UNIQUE (incident_id, proof_id)`, RLS
`ENABLE`/`FORCE`, política `incident_evidence_tenant` y trigger `incident_evidence_append_only`.
La evidencia referencia `custody.proofs` de POD-001; INC-001 no captura archivos ni bytes.

La obligatoriedad de la evidencia no es solo de aplicación: el trigger de restricción
`incidents_require_evidence` es `DEFERRABLE INITIALLY DEFERRED`, de modo que una incidencia sin
evidencia no puede confirmarse ni siquiera saltándose la aplicación.

Una instalación anterior a INC-001 se evoluciona con `ADD COLUMN` nullable, backfill determinista
por severidad y `SET NOT NULL`, preservando el historial existente.

## Contrato HTTP

`POST /api/v1/orders/{orderId}/incidents` exige `Idempotency-Key` (16-128 caracteres) y el cuerpo:

```json
{
  "type": "FAILED_DELIVERY_ATTEMPT",
  "severity": "MEDIUM",
  "description": "El destinatario no se encontraba en el domicilio.",
  "reason_code": "RECIPIENT_ABSENT",
  "next_action": "RESCHEDULED",
  "occurred_at": "2026-09-22T17:55:00Z",
  "evidence_proof_ids": ["..."]
}
```

Motivos admitidos: `RECIPIENT_ABSENT`, `ADDRESS_NOT_FOUND`, `RECIPIENT_REFUSED`,
`ACCESS_RESTRICTED`, `PAYMENT_UNAVAILABLE`, `PACKAGE_DAMAGED`, `SECURITY_RISK`.

La respuesta 201 devuelve `id`, `order_id`, `status`, `severity`, `reason_code`, `next_action`,
`custody_acquired`, `occurred_at`, `sla_due_at` y `evidence_proof_ids`.

Códigos de conflicto publicados: `INVALID_REQUEST`, `IDEMPOTENCY_CONFLICT`,
`ORDER_STATE_NOT_ALLOWED`, `EVIDENCE_NOT_AVAILABLE` y `CONFLICT`.

### Alineación normativa de AI-05

La desviación entre AI-05 y AI-08 quedó cerrada con autorización del propietario, limitada a
`POST /api/v1/orders/{orderId}/incidents`.

`OpenIncidentRequest` publica ahora los siete campos que AI-08 exige —`type`, `severity`,
`description`, `reason_code`, `next_action`, `occurred_at` y `evidence_proof_ids`— todos
obligatorios y con `additionalProperties: false`, que es el esquema cerrado que el endpoint ya
aplicaba al rechazar miembros no declarados. `Incident` publica los diez campos que
`IncidentResponse` devuelve, incluidos `custody_acquired` y `sla_due_at`. La matriz de respuestas
declara 201/401/403/404/409/503 y el 409 se documenta con `IncidentConflictProblem`, cuyo conjunto
cerrado de códigos es el que ya publicaba el endpoint. El `Idempotency-Key` obligatorio se
documenta por referencia al parámetro canónico.

No se amplió ninguna funcionalidad: AI-05 se limitó a describir lo ya implementado.

Al cambiar AI-05 se repinnearon los hashes derivados que la gobernanza exige:
`CHECKSUMS_SHA256.txt` y `MANIFEST.json` (regenerados con
`tools/validate_contracts.py --write-integrity`), el pin de `OpenApiBaselineTests` y los dos
hashes congelados de
`SyntheticEnvironmentArchitectureTests.Frozen_governance_files_are_byte_identical_to_SEC003_base`.
`specs/AI-08_BACKLOG.yaml` no se tocó.

`IncidentsOpenApiImplementationTests` impide que la deriva vuelva a abrirse: deriva cada
expectativa del vocabulario de dominio, de los records de transporte y del propio fuente del
endpoint, en vez de repetirla como literal, de modo que ampliar cualquiera de los dos lados sin
republicar el contrato falla.

## SLA

El plazo se deriva únicamente de la severidad y se mide desde el intento, no desde la petición,
de modo que reintentar la misma apertura reproduce el mismo `sla_due_at`:

| Severidad | Ventana |
| --- | --- |
| `CRITICAL` | 2 horas |
| `HIGH` | 8 horas |
| `MEDIUM` | 24 horas |
| `LOW` | 72 horas |

`occurred_at` se acepta con hasta 5 minutos de adelanto (desfase de reloj del dispositivo) y hasta
24 horas de atraso; fuera de ese rango la petición es `INVALID_REQUEST`.

## Integración con ORD-002

INC-001 no agrega, modifica ni elimina aristas ni guards. La incidencia habilita el camino que
ORD-002 ya define:

1. La incidencia se abre solo desde `AT_PICKUP`, `IN_TRANSIT` o `DELIVERING`, que son los estados
   desde los que ORD-002 permite alcanzar `FAILED_ATTEMPT`.
2. `custody_acquired` se deriva del estado en que falló el intento: falso en `AT_PICKUP`,
   verdadero en `IN_TRANSIT` y `DELIVERING`.
3. La transición se solicita después con
   `POST /api/v1/orders/{orderId}/transitions`, `target_status = FAILED_ATTEMPT` y
   `metadata = {"incident_id": "..."}`.
4. Los guards `attempt_stage_recorded` y `custody_acquired_recorded` de ORD-002 leen la incidencia
   a través de `IOrderIncidentGuardReader` y rechazan la transición si no existe, no pertenece al
   tenant o no está `OPEN`/`INVESTIGATING`.
5. `no_unresolved_incident` sigue bloqueando `CLOSED` mientras la incidencia siga abierta.

`next_action` registra de forma explícita si el siguiente paso operativo es `RESCHEDULED` o
`RETURNING`; ambos son sucesores válidos de `FAILED_ATTEMPT` en la matriz de ORD-002.

## Tenancy, auditoría e idempotencia

- Toda la lectura y escritura ocurre dentro de una transacción tenant con
  `set_config(..., true)`; `incidents.incidents` e `incidents.incident_evidence` tienen RLS
  `FORCE` y política por `owner_org_id`/`operator_org_id`.
- Una orden de otro tenant es indistinguible de una ausente: 404 uniforme.
- Una evidencia que no sea prueba POD-001 de esa misma orden dentro del tenant no cuenta, y la
  apertura falla cerrada con `EVIDENCE_NOT_AVAILABLE` sin revelar la causa.
- La apertura escribe exactamente una entrada `incidents.incident.opened` en
  `platform.audit_logs`, con payload redactado y sin descripción en claro.
- La reserva idempotente vive en `platform.idempotency_keys` con scope
  `INC-001:OPEN_INCIDENT`, protegida por `pg_advisory_xact_lock`. El hash canónico ordena los
  identificadores de evidencia, de modo que un reintento con el mismo conjunto en otro orden
  replica en lugar de abrir una segunda incidencia. La misma clave con otro contenido responde
  `IDEMPOTENCY_CONFLICT`; la misma clave en otro tenant abre una incidencia independiente.

## Pruebas

- `tests/Paqueteria.UnitTests/Incidents/IncidentPolicyTests.cs`: vocabulario, motivos, siguiente
  acción, evidencia obligatoria, ventana de SLA y estados de apertura (57 casos).
- `tests/Paqueteria.ContractTests/PostgreSql/IncidentsPostgreSqlContractTests.cs`: apertura y
  persistencia, auditoría, inmovilidad de la orden, rechazo de motivo/siguiente acción/evidencia
  ausentes, evidencia ajena, estado no permitido, `Idempotency-Key` inválida, replay idempotente,
  aislamiento por tenant, evidencia append-only, restricción diferida, y el traspaso completo a la
  transición `FAILED_ATTEMPT` de ORD-002 incluidos sus rechazos.

Las pruebas de contrato requieren Docker (Testcontainers con `postgis/postgis:18-3.6`).

## Rollback

1. Retirar `MapIncidentEndpoints` y `AddIncidentsInfrastructure` de la composición de la API.
2. Revertir los commits INC-001.

La migración no se revierte: la evidencia de incidencia es registro operativo append-only. Las
columnas y la tabla agregadas permanecen y son inertes sin el módulo.
