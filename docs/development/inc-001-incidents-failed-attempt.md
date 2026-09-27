# INC-001: incidencias e intento fallido

INC-001 agrega el módulo `Incidents` y el comando autenticado e idempotente
`POST /api/v1/orders/{orderId}/incidents`. La incidencia es la **precondición** del intento
fallido: INC-001 nunca escribe `orders.orders`, de modo que ORD-002 sigue siendo el único autor
del estado de la orden.

Cubre los criterios de aceptación de AI-08: motivo y evidencia obligatorios, siguiente acción
explícita, SLA timestamp y `Idempotency-Key` en la operación de apertura.

El seguimiento de resolución agrega `POST /api/v1/incidents/{incidentId}/resolution`, que cierra
una incidencia `OPEN` o `INVESTIGATING` en `RESOLVED` o `REJECTED` (ver [Resolución](#resolución)).

No implementa la entrada a `INVESTIGATING`, reapertura, edición, ventana de reclamo, LIF-001,
soporte a clientes ni CRM.

## Capas y responsabilidades

- `Incidents.Domain` contiene el vocabulario (estado, severidad, motivo, siguiente acción), la
  política de SLA, la política de estados de orden habilitados y los límites de evidencia. No
  depende de JSON, persistencia ni frameworks. `IncidentOperationalPolicy` centraliza los
  parámetros operativos MVP-1 y los valida como una unidad.
- `Incidents.Application` define el comando, el resultado, los errores estructurados y la
  validación de forma que rechaza todo criterio obligatorio ausente antes de abrir transacción.
- `Incidents.Infrastructure` ejecuta la transacción tenant, la reserva idempotente, las lecturas
  cross-schema de solo lectura (`orders`, `identity`, `organizations`, `drivers`, `dispatch`,
  `custody`) y las escrituras en `incidents`.
- `Incidents.Endpoints` limita el contrato HTTP a binding, autenticación, tenant activo,
  `Idempotency-Key` y mapeo 201/401/403/404/409/503 en la apertura y 200/401/403/404/409/503 en
  la resolución.

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

### Coherencia de la evidencia en la base de datos

La validación de aplicación no basta para una relación append-only. La base de datos rechaza por
sí misma una evidencia incoherente:

- `incident_evidence_incident_order_owner_fkey` — clave foránea compuesta
  `(incident_id, order_id, owner_org_id)` contra `incidents.incidents`, apoyada en
  `incidents_identity_order_owner_key`;
- `incident_evidence_proof_order_owner_fkey` — clave foránea compuesta
  `(proof_id, order_id, owner_org_id)` contra `custody.proofs`, apoyada en
  `proofs_identity_order_owner_key`.

Ambas claves son implícitas en las llaves primarias existentes, así que no agregan ninguna regla
de unicidad nueva a las tablas canónicas: solo permiten que PostgreSQL exija que la evidencia
nombre una incidencia y una prueba de la misma orden y del mismo tenant. Las comprobaciones de
integridad referencial ignoran RLS por diseño, de modo que valen también contra SQL directo sin
debilitar ninguna política.

`operator_org_id` es nullable en ambas tablas canónicas y `MATCH SIMPLE` omitiría una clave
compuesta que lo incluyera. El complemento mínimo es el trigger de restricción
`incident_evidence_coherent`, que solo decide la coherencia del operador y falla cerrado: una
incidencia o una prueba que no puede ver se rechaza en lugar de suponerse coherente.

### Actualización de una instalación con datos

Una instalación anterior a INC-001 se evoluciona con `ADD COLUMN` nullable, backfill determinista
por severidad y `SET NOT NULL`, preservando el historial existente.

El backfill tiene una dificultad real: `incidents.incidents` mantiene `FORCE ROW LEVEL SECURITY`
y `paqueteria_migrator` es `NOBYPASSRLS`, así que las filas de un tenant instalado son invisibles
para la sesión de migración. El `UPDATE` no actualizaría nada y el `SET NOT NULL` posterior —que sí
ve todas las filas— rechazaría la actualización.

Siguiendo el precedente NTF-001 de un privilegio temporal acotado a la transacción de migración,
el propietario se concede a sí mismo la política `incidents_inc001_backfill`
(`AS PERMISSIVE FOR ALL TO paqueteria_migrator`), hace el backfill y la elimina unas sentencias
después, dentro de la misma transacción. `FORCE RLS` nunca se apaga, ningún rol recibe
`BYPASSRLS`, ningún rol de runtime gana visibilidad y, si algo falla, la transacción revierte la
política junto con el resto. La migración verifica al final que la política no sobrevivió.

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
`SyntheticEnvironmentArchitectureTests.Governance_integrity_files_match_pinned_values`.
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
72 horas de atraso; fuera de ese rango la petición es `INVALID_REQUEST`.

## Parámetros operativos

Las ventanas de SLA, la ventana retrospectiva, la tolerancia de reloj y el máximo de evidencias son
parámetros operativos MVP-1: pueden revisarse sin cambiar la semántica que AI-08 fija. Viven
centralizados en `IncidentOperationalPolicy` y se exponen en la sección de configuración
`Incidents`:

| Ajuste | Valor MVP-1 aprobado |
| --- | --- |
| `Incidents:CriticalSlaHours` | 2 |
| `Incidents:HighSlaHours` | 8 |
| `Incidents:MediumSlaHours` | 24 |
| `Incidents:LowSlaHours` | 72 |
| `Incidents:MaximumOccurrenceAgeHours` | 72 |
| `Incidents:MaximumOccurrenceSkewMinutes` | 5 |
| `Incidents:MaximumEvidenceCount` | 10 |

La validación es de arranque (`ValidateOnStart`) y de unidad: las ventanas deben ser positivas y
acotadas, ordenadas de `CRITICAL` a `LOW` —una incidencia más severa nunca puede tener un plazo más
laxo—, y el techo de evidencias debe quedar entre el mínimo semántico y el máximo que AI-05
publica. Una configuración incoherente impide el arranque en lugar de abrir incidencias bajo una
política que nadie aprobó; el servicio la vuelve a comprobar y responde 503 si aun así llegara.

El mínimo de una evidencia no es un parámetro: AI-08 hace la evidencia obligatoria y ninguna
configuración puede bajarlo. El catálogo de motivos sigue siendo controlado y extensible.

## Descripción y protección de PII

`description` es obligatoria y forma parte del hash de idempotencia, y se persiste únicamente a
través de los campos canónicos AI-06 `description_ciphertext` y `pii_key_version`. El texto en
claro no se persiste nunca, ni llega a la auditoría.

La protección la resuelve `IIncidentPiiProtector`, una abstracción propia del módulo siguiendo el
precedente GEO-001: INC-001 no depende de Locations para reutilizar su implementación concreta. Hay
dos implementaciones, seleccionadas por `Incidents:PiiProtector`:

- `Disabled` (valor por omisión) — no protege nada y por lo tanto no se persiste nada;
- `Mock` — protector sintético determinista, `DEV_SYNTHETIC_ONLY`, rechazado fuera de Development,
  Testing y DevSynthetic autorizado.

La protección se ejecuta **antes** de abrir la transacción. Si no está disponible, la petición
termina en 503 sin haber escrito incidencia, evidencia, reserva idempotente ni auditoría: cero
efectos parciales. Un despliegue que no configure `Incidents:PiiProtector` no abre incidencias, que
es el comportamiento cerrado deseado y el mismo que GEO-001 aplica a sus proveedores.

## Integración con ORD-002

INC-001 no agrega, modifica ni elimina aristas ni guards. La incidencia habilita el camino que
ORD-002 ya define:

1. La incidencia se abre solo desde `AT_PICKUP`, `IN_TRANSIT` o `DELIVERING`, que son los estados
   desde los que ORD-002 permite alcanzar `FAILED_ATTEMPT`.
2. `custody_acquired` es la derivación única que comparten ORD-002 y la vista de paradas: existe
   un `ORDER_STATUS_CHANGED` con `new_status = PICKED_UP` en el historial de la orden. Es falso en
   la primera recolección y verdadero en `IN_TRANSIT`, `DELIVERING` y en una nueva recolección
   posterior a un intento que ya había recogido el paquete.
3. La orden se lee `FOR SHARE` dentro de la transacción de apertura: una transición ORD-002
   concurrente (que bloquea la orden `FOR UPDATE`) termina antes o espera a que la incidencia se
   confirme, y la apertura nunca decide sobre un estado ya reemplazado.
4. La transición se solicita después con
   `POST /api/v1/orders/{orderId}/transitions`, `target_status = FAILED_ATTEMPT` y
   `metadata = {"incident_id": "..."}`.
5. Los guards `attempt_stage_recorded` y `custody_acquired_recorded` de ORD-002 leen la incidencia
   a través de `IOrderIncidentGuardReader` y rechazan la transición si no existe, no pertenece al
   tenant, no está `OPEN`/`INVESTIGATING`, se abrió antes de entrar al estado actual o ya justificó
   otro `FAILED_ATTEMPT`: una incidencia justifica un solo intento fallido.
6. `no_unresolved_incident` sigue bloqueando `CLOSED` mientras la incidencia siga abierta.
7. Al salir de `FAILED_ATTEMPT`, ORD-002 respeta `next_action`: `RETURNING` solo permite
   `RETURNING`; `RESCHEDULED` permite `RESCHEDULED` o el reintento `DELIVERING`.
8. La evidencia de una incidencia nunca completa una recolección ni una entrega en ORD-002.

`next_action` registra de forma explícita si el siguiente paso operativo es `RESCHEDULED` o
`RETURNING`; ambos son sucesores válidos de `FAILED_ATTEMPT` en la matriz de ORD-002.

`RETURNING` exige custodia adquirida. ORD-002 y ADR-014 solo devuelven lo que el operador ya tiene:
un paquete que nunca fue recogido no puede devolverse. Una primera recolección en `AT_PICKUP` admite
`RESCHEDULED` pero nunca `RETURNING`, y la apertura se rechaza con `ORDER_STATE_NOT_ALLOWED` antes
de persistir nada, en lugar de registrar una siguiente acción que la máquina de estados jamás
podría honrar junto a un `custody_acquired = false`. La regla no agrega ni modifica ninguna arista
de ORD-002. La base de datos la sostiene por su cuenta con
`incidents_returning_requires_custody_check`.

## Tenancy, auditoría e idempotencia

- Toda la lectura y escritura ocurre dentro de una transacción tenant con
  `set_config(..., true)`; `incidents.incidents` e `incidents.incident_evidence` tienen RLS
  `FORCE` y política por `owner_org_id`/`operator_org_id`.
- Una orden de otro tenant es indistinguible de una ausente: 404 uniforme.
- Una evidencia que no sea prueba POD-001 de esa misma orden dentro del tenant no cuenta, y la
  apertura falla cerrada con `EVIDENCE_NOT_AVAILABLE` sin revelar la causa.
- La apertura escribe exactamente una entrada `incidents.incident.opened` en
  `platform.audit_logs`, con payload redactado y sin descripción en claro: la auditoría registra el
  conteo de evidencias, no su contenido, y nunca la descripción.
- La reserva idempotente vive en `platform.idempotency_keys` con scope
  `INC-001:OPEN_INCIDENT`, protegida por `pg_advisory_xact_lock`. El hash canónico ordena los
  identificadores de evidencia, de modo que un reintento con el mismo conjunto en otro orden
  replica en lugar de abrir una segunda incidencia. La misma clave con otro contenido responde
  `IDEMPOTENCY_CONFLICT`; la misma clave en otro tenant abre una incidencia independiente.

## Resolución

`POST /api/v1/incidents/{incidentId}/resolution` (`resolveIncident`) recibe exactamente
`{"outcome", "reason"}` y responde 200 con la representación `Incident` existente, que solo cambia
su `status`.

- **Máquina de estados.** `OPEN` e `INVESTIGATING` —el mismo par que ORD-002 considera incidencia
  no resuelta— cierran en `RESOLVED` o `REJECTED`. Un estado terminal no vuelve a cambiar: otro
  cierre con otra clave responde `INCIDENT_STATE_CONFLICT` sin revelar el estado actual. No se
  agregan estados ni columnas; la resolución escribe solo `status` y `resolved_at` (reloj UTC
  normalizado a microsegundos).
- **Motivo.** Texto plano de 1 a 500 caracteres; se rechazan, sin recortarlos, los espacios al
  inicio o al final y los caracteres de control. Vive solo como evidencia de auditoría.
- **Autorización.** Solo un `DISPATCHER` activo del tenant, o un `PLATFORM_ADMIN` activo con MFA
  satisfecho. Un `DRIVER` nunca, ni siquiera quien abrió la incidencia con su asignación activa.
  La capacidad se evalúa antes del lock idempotente, del replay y de la visibilidad de la
  incidencia.
- **Orden de la transacción.** Capacidad → `pg_advisory_xact_lock` de la clave → replay exacto →
  `SELECT … FOR UPDATE` de la incidencia en el tenant → validación de estado → reserva → `UPDATE`
  → auditoría → cierre de la reserva, todo en una transacción. Dos cierres concurrentes con claves
  distintas se serializan en el lock de fila: exactamente uno gana y el otro recibe
  `INCIDENT_STATE_CONFLICT`.
- **Idempotencia.** Scope `INC-001:RESOLVE_INCIDENT`; el hash liga tenant, incidencia, desenlace y
  motivo exacto. El replay devuelve la respuesta original sin otro `UPDATE`, otra auditoría ni otro
  `resolved_at`; la misma clave con otro contenido responde `IDEMPOTENCY_CONFLICT`; una reserva
  corrupta falla cerrada como `CONFLICT`. El replay de una **apertura** sigue devolviendo su
  respuesta `OPEN` original aunque la incidencia ya se haya cerrado.
- **Auditoría.** `incidents.incident.resolved` o `incidents.incident.rejected`, con
  `incident_id`, `order_id`, `previous_status`, `outcome`, `resolution_reason` y `resolved_at`,
  pasando por el redactor de auditoría y sin la descripción protegida.
- **Frontera con ORD-002.** La resolución no escribe `orders.orders`, no cambia la versión de la
  orden, no infiere `RESCHEDULED` ni `RETURNING` y no toca la evidencia. Solo deja de contar como
  incidencia no resuelta para `no_unresolved_incident`.

## Pruebas

- `tests/Paqueteria.UnitTests/Incidents/IncidentPolicyTests.cs`: vocabulario, motivos, siguiente
  acción, evidencia obligatoria, ventana de SLA, estados de apertura, la precondición de custodia
  para `RETURNING` y los parámetros operativos MVP-1 con sus combinaciones inválidas.
- `tests/Paqueteria.UnitTests/Incidents/IncidentsConfigurationTests.cs`: los valores por omisión
  aprobados, la validación que falla cerrado ante una configuración incoherente y el protector de
  descripción, que está deshabilitado mientras un despliegue no diga otra cosa.
- `tests/Paqueteria.ContractTests/PostgreSql/IncidentsPostgreSqlContractTests.cs`: apertura y
  persistencia, auditoría, inmovilidad de la orden, rechazo de motivo/siguiente acción/evidencia
  ausentes, evidencia ajena, estado no permitido, `Idempotency-Key` inválida, replay idempotente,
  aislamiento por tenant, evidencia append-only, restricción diferida, la descripción cifrada con su
  versión de clave, el protector no disponible sin efectos, la coherencia de evidencia exigida por
  la base de datos (otra orden, otro tenant, orden que no corresponde a la incidencia, operador
  incoherente), `RETURNING` según la custodia, y el traspaso completo a la transición
  `FAILED_ATTEMPT` de ORD-002 incluidos sus rechazos.
- `tests/Paqueteria.ContractTests/PostgreSql/IncidentsUpgradePostgreSqlContractTests.cs`: la
  actualización de una instalación **con datos**, desde el baseline canónico previo a INC-001 y con
  el migrador canónico, verificando que ninguna incidencia se pierde, que el backfill queda
  completo, que las restricciones finales existen y que `FORCE RLS`, las políticas de tenant, la
  propiedad de los objetos y el `NOBYPASSRLS` del migrador siguen intactos.

- `tests/Paqueteria.UnitTests/Incidents/IncidentResolutionPolicyTests.cs`: vocabulario de
  desenlaces, matriz de transiciones, política del motivo y matriz de autorización.
- `tests/Paqueteria.ContractTests/PostgreSql/IncidentsResolutionPostgreSqlContractTests.cs`: las
  cuatro transiciones, estados terminales inmutables, `resolved_at` único, auditoría y reserva
  atómicas con rollback ante falla inyectada, autorización por rol/estado/MFA, el conductor que
  abrió la incidencia, `FORCE RLS` y 404 uniforme, replay y conflicto idempotente, la carrera
  `RESOLVED`/`REJECTED` con un solo ganador, la orden y la evidencia intactas y la semántica de
  pendiente que lee ORD-002.
- `tests/Paqueteria.IntegrationTests/Incidents/IncidentResolutionHttpTests.cs`: la matriz HTTP
  completa a través de la API real sobre PostgreSQL real.

Las pruebas de contrato requieren Docker (Testcontainers con `postgis/postgis:18-3.6`).

## Rollback

1. Retirar `MapIncidentEndpoints` y `AddIncidentsInfrastructure` de la composición de la API.
2. Revertir los commits INC-001.

La migración no se revierte: la evidencia de incidencia es registro operativo append-only. Las
columnas y la tabla agregadas permanecen y son inertes sin el módulo.
