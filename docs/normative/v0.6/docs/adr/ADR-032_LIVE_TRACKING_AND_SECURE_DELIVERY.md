# ADR-032 — Tracking en vivo, traslado a recolección y verificación segura de entrega

**Estado:** ACEPTADO como referencia de diseño para v0.7 (congelado 2026-07-21 tras revisión cruzada Claude↔Codex). No autoriza implementación por sí solo: los deltas de contrato/SQL, GATE-007/GATE-013 y las pruebas Testcontainers siguen siendo  requisito.
**Versión del candidato:** `0.7-review-4`  
**Fecha:** 2026-07-21  
**Base normativa:** Paquetería Culiacán v0.6 Full Canonical Sync.  
**ADR hermano:** ADR-033 — sello de integridad y cadena de custodia.

**Bloqueos de implementación:** GATE-007; GATE-013 para producción multiinstancia; aprobación conjunta de ADR-032/ADR-033; delta normativo coordinado; migraciones ejecutadas en PostgreSQL/PostGIS real.

**Depende de / modifica:** ADR-003, ADR-004, ADR-005, ADR-014, ADR-015, ADR-020, ADR-022, ADR-023, ADR-028, ADR-031, ADR-033; AI-02, AI-03, AI-04, AI-05, AI-06, AI-07, AI-08, AI-09, AI-12, AI-13, AI-18, AI-24 y `tools/validate_contracts.py`.

---

## 0. Decisiones cerradas de este candidato

1. Se agrega `PICKUP_IN_PROGRESS`; el contrato pasa de 17 a 18 estados internos.
2. `ASSIGNED → AT_PICKUP` deja de ser una transición runtime válida.
3. Las opciones se eligen en la cotización y se copian inmutables a la orden.
4. `STRICT_CODE_ONLY` implica `delivery_code_required=true` y `tamper_seal_required=true`.
5. El código inicial es numérico de seis dígitos, con HMAC, máximo cinco intentos y política versionada.
6. El código no se modela como archivo POD.
7. La autorización del remitente ocasional se representa mediante grants append-only de acceso a orden, no mediante una organización personal.
8. Los tokens de tracking histórico y live son físicamente separados.
9. La ubicación live es una proyección sanitizada; ninguna función pública lee GPS crudo.
10. Una excepción supervisada puede terminar en `DELIVERED`, pero se registra mediante evidencia append-only y `delivery_completion_method`, no mediante una bandera mutable aislada.
11. El guard final de entrega es compuesto con ADR-033. Una excepción de código jamás satisface el requisito de sello.
12. Un fallo de sello confirma un resultado `SECURITY_HOLD`; no se implementa lanzando una excepción que revierta el incidente.

---

## 1. Estado universal `PICKUP_IN_PROGRESS`

Ruta ordinaria obligatoria:

```text
READY_FOR_PICKUP → ASSIGNED → PICKUP_IN_PROGRESS → AT_PICKUP → PICKED_UP
```

Transiciones:

```text
ASSIGNED → PICKUP_IN_PROGRESS
PICKUP_IN_PROGRESS → AT_PICKUP
PICKUP_IN_PROGRESS → READY_FOR_PICKUP
PICKUP_IN_PROGRESS → CANCELLED
```

La transición directa `ASSIGNED → AT_PICKUP` queda prohibida para runtime. Un adaptador temporal de migración puede reconocer órdenes anteriores, pero no producir nuevas omisiones del estado.

### 1.1 Guard de entrada

`ASSIGNED → PICKUP_IN_PROGRESS` exige:

- asignación activa, vigente y no reemplazada;
- actor = repartidor asignado o dispatcher con capacidad explícita;
- parada pickup vigente y no completada;
- `custody_acquired=false`;
- ningún POD definitivo de pickup;
- `expected_order_version` y versión de asignación válidos;
- contexto tenant autorizado;
- si `tamper_seal_required=true`, el repartidor tiene al menos un sello disponible bajo custodia aceptada.

### 1.2 Retorno a READY y cancelación

`PICKUP_IN_PROGRESS → READY_FOR_PICKUP` desactiva atómicamente asignación y parada.

Toda transición a `CANCELLED` antes de pickup exige:

- motivo;
- `custody_acquired=false`;
- ningún POD definitivo de pickup;
- ninguna `custody_leg` activa;
- cancelación atómica de asignación/parada.

Después de adquirir custodia, `CANCELLED` está prohibido; la salida es devolución.

### 1.3 Métricas

La fuente de verdad son `order_events` append-only, ordenados por `aggregate_version`. Reporting deriva intervalos y número de intento. Solo se materializa `reporting.order_state_intervals` si mediciones reales justifican la optimización; nunca reemplaza al log fuente.

### 1.4 Mapa público fail-closed

`PICKUP_IN_PROGRESS → SCHEDULED`.

AI-04 es la fuente normativa del mapa de 18 estados. SQL devuelve `NULL` ante estado no mapeado; C# lanza `PublicStatusMappingException`; CI compara exactamente los conjuntos de AI-02, AI-04, AI-05, AI-06 y C#.

---

## 2. Opciones de servicio e invariantes

Campos de quote y snapshot inmutable en order:

- `live_tracking_enabled boolean`;
- `delivery_code_required boolean`;
- `tamper_seal_required boolean`;
- `delivery_code_failure_policy` = `STRICT_CODE_ONLY | CODE_OR_SUPERVISED_ALTERNATE | NOT_APPLICABLE`;
- versiones de políticas.

Invariantes SQL y de dominio:

```text
delivery_code_required=false
  ⇒ delivery_code_failure_policy=NOT_APPLICABLE

STRICT_CODE_ONLY
  ⇒ delivery_code_required=true
  AND tamper_seal_required=true

usuario-a-usuario
  ⇒ delivery_code_required=true
  AND tamper_seal_required=true
  AND delivery_code_failure_policy=STRICT_CODE_ONLY
```

Las opciones se eligen antes de calcular/aceptar la cotización. Después de `CONFIRMED` solo cambian mediante override administrativo auditado que recotiza, revalida canal/consentimiento y genera nuevo snapshot de política.

Matriz:

| Live | Código | Mapa | Genera/envía código | Proximidad |
|---:|---:|---|---|---|
| 0 | 0 | No | No | No |
| 1 | 0 | Sí | No | Opcional con canal verificado |
| 0 | 1 | No | Sí | Sí, GPS interno o acción driver |
| 1 | 1 | Sí | Sí | Sí |

La notificación del código depende solo de `delivery_code_required`.

---

## 3. Canal del destinatario

Cuando el código es obligatorio debe existir, antes de `IN_TRANSIT`, al menos un canal verificado y entregable permitido por política.

Modelo conceptual:

```text
notifications.recipient_channels
- id
- owner_org_id
- order_id
- channel_type
- destination_ciphertext
- pii_key_version
- verification_status
- verification_method
- verified_at
- verification_expires_at
- provider_validation_reference
```

El `contact_token` del driver no se reutiliza. Notifications resuelve internamente el destino con privilegio mínimo; UI, tracking y driver nunca reciben el valor real.

---

## 4. Challenge de código

`DELIVERY_CODE` se elimina de los tipos de archivo de ADR-020.

### 4.1 `custody.delivery_code_challenges`

Campos mínimos:

- `id`, `order_id`, `owner_org_id`, `operator_org_id`;
- `delivery_attempt_id`, `assignment_id`;
- `code_hmac`, `hmac_key_version`;
- `code_ciphertext`, `pii_key_version`, solo mientras pueda notificarse;
- `status`: `PENDING_NOTIFICATION | ACTIVE | CONSUMED | LOCKED | EXPIRED | REVOKED`;
- `max_attempts`, `failed_attempts`;
- timestamps y `policy_version`.

El HMAC se calcula sobre una forma canónica que liga el secreto a su contexto:

```text
challenge_id | order_id | delivery_attempt_id | code
```

El secreto HMAC no vive en PostgreSQL. El código es string para conservar ceros iniciales. Comparación en tiempo constante. El incremento de intentos y el lockout son atómicos mediante bloqueo de fila o `UPDATE ... WHERE failed_attempts < max_attempts RETURNING`.

### 4.2 `custody.delivery_code_verifications`

Registro append-only de cada resultado:

- challenge, orden, intento, asignación y actor;
- resultado `VALID | INVALID | EXPIRED | LOCKED | REVOKED | VALID_BUT_DELIVERY_BLOCKED`;
- timestamp y metadatos redacted;
- nunca el código digitado.

### 4.3 Ciclo y notificación

- Al confirmar `PICKED_UP`, la transacción crea challenge y outbox A si aplica.
- `PICKED_UP → IN_TRANSIT` exige challenge activo y notificación A encolada idempotentemente.
- `IN_TRANSIT → DELIVERING` exige notificación `SENT/DELIVERED` o resolución autorizada de soporte.
- Reasignación, reprogramación o nuevo intento revoca el challenge anterior y crea uno nuevo.
- Al llegar a `LOCKED`/`EXPIRED`, si no existe alternate permitido, el comando confirma `FAILED_ATTEMPT` con reason `DELIVERY_CODE_UNAVAILABLE` o inicia devolución según policy; no deja la orden indefinidamente en `DELIVERING`.
- El outbox contiene `challenge_id`; el Worker resuelve el ciphertext dentro de su límite de confianza.
- `code_ciphertext` se elimina o destruye criptográficamente al consumir, revocar o expirar el challenge, conforme a GATE-007.

El sistema nunca revela ni precarga el código al driver. El input es `writeOnly`; respuestas, logs, SignalR, auditoría y outbox genérico no contienen el secreto.

---

## 5. `CompleteDelivery` y guard compuesto

No existen comandos separados “validar código” y “entregar”. El comando idempotente recibe:

- `order_id`;
- `expected_order_version`;
- `delivery_attempt_id`;
- `delivery_code` write-only cuando aplica;
- `observed_seal_token` o identificador manual cuando aplica;
- referencias a Proof ya `READY`/definitivos;
- `Idempotency-Key`.

### 5.1 Guard

```text
delivery_proof_complete =
  required_binary_or_signature_proofs_complete
  AND code_requirement_complete
  AND tamper_seal_requirement_complete
```

```text
code_requirement_complete =
  delivery_code_required=false
  OR valid_code_consumable
  OR supervised_alternate_valid
```

```text
tamper_seal_requirement_complete =
  tamper_seal_required=false
  OR expected_seal_reverified_intact
```

`supervised_alternate_valid` solo sustituye código y está prohibido bajo `STRICT_CODE_ONLY`. Nunca sustituye sello.

### 5.2 Resultados confirmables

El comando devuelve y confirma uno de:

- `DELIVERED`;
- `CODE_REJECTED`;
- `SECURITY_HOLD`;
- `CONFLICT` por versión/idempotencia.

No se usa una excepción para representar `SECURITY_HOLD`, porque revertiría incidente, eventos y transición de sello.

### 5.3 Camino exitoso

Dentro de `proof_custody_to_order_transition`:

1. valida orden, tenant, asignación e intento;
2. valida Proof definitivos;
3. valida código y sello;
4. consume challenge;
5. inserta verificación append-only;
6. transiciona sello a `DELIVERY_VERIFIED` si aplica;
7. registra `delivery_completion_evidence`;
8. transiciona orden a `DELIVERED`;
9. inserta `order_event`, auditoría y outbox.

### 5.4 Fallos

Tabla normativa:

| Código | Sello | Resultado |
|---|---|---|
| válido/no requerido | válido/no requerido | `DELIVERED` |
| inválido | válido/no requerido | `CODE_REJECTED`; no consumir; contabilizar intento |
| válido | inválido/roto | `SECURITY_HOLD`; challenge revocado con resultado `VALID_BUT_DELIVERY_BLOCKED` |
| inválido | inválido/roto | `SECURITY_HOLD`; registrar ambos fallos |

`SECURITY_HOLD` confirma en la misma transacción:

- orden `FAILED_ATTEMPT`, razón `SEAL_SECURITY_HOLD`, `custody_acquired=true`;
- incidente append-only;
- sello esperado y observado bajo investigación cuando correspondan;
- challenge revocado;
- eventos y outbox `DriverSecurityHoldRequested`.

La suspensión de nuevas asignaciones se ejecuta después del commit mediante handler idempotente. No bloquea completar devoluciones activas.

No se agrega una sexta entrada a la allowlist: se amplía explícitamente `proof_custody_to_order_transition` para abarcar Orders, Custody, Incidents y Outbox. La acción posterior sobre Drivers es asíncrona.

---

## 6. Excepción supervisada y reporting

Políticas:

### `STRICT_CODE_ONLY`

- usuario-a-usuario y negocios que la exijan;
- implica sello;
- no admite excepción de código;
- sin código: `FAILED_ATTEMPT` o devolución.

### `CODE_OR_SUPERVISED_ALTERNATE`

Exige:

- supervisor autenticado distinto del driver;
- capability explícita;
- reason code;
- geofence y asignación activa;
- evidencia alternativa según política;
- incidente y auditoría append-only;
- límites por driver;
- exclusión de categorías de alto riesgo.

La orden puede terminar `DELIVERED`, pero se registra:

```text
delivery_completion_method = STANDARD | DELIVERY_CODE | SUPERVISED_ALTERNATE | NO_CODE_REQUIRED
```

Fuente append-only `custody.delivery_completion_evidence`:

- order/attempt/assignment;
- método;
- supervisor;
- razón, policy version;
- proof IDs;
- geofence result;
- incident ID;
- timestamps.

Una proyección en Orders puede facilitar reporting, pero no reemplaza la evidencia. Un incidente abierto puede permitir `DELIVERED`, pero bloquea `CLOSED` hasta su resolución.

---

## 7. Autorización del remitente ocasional

Se conserva `orders.created_by_user_id` como provenance inmutable. La autorización actual se materializa en una tabla controlada y toda modificación genera eventos append-only.

### 7.1 Proyección actual

```text
orders.customer_order_access
- order_id
- owner_org_id
- user_id
- access_role = SENDER
- status = ACTIVE | REVOKED | EXPIRED
- granted_at
- expires_at
- version
PRIMARY KEY(order_id,user_id,access_role)
```

No hay INSERT/UPDATE/DELETE directo para roles runtime. Las funciones `grant_customer_order_access` y `revoke_customer_order_access` son `SECURITY DEFINER`, validan versión y escriben simultáneamente `orders.customer_order_access_events` append-only. Esto permite revocar y re-conceder sin crear grants efectivos duplicados.

### 7.2 Creación segura

No existe endpoint genérico para auto-concederse acceso en MVP. Para orden ocasional:

- `created_by_user_id = security.app_current_user()`;
- el acceso se crea dentro de `quote_snapshot_to_order`;
- el coordinador verifica que quote, subject autenticado y usuario interno corresponden;
- la función comprueba `orders.created_by_user_id=user_id`, `status` de la orden y contexto tenant;
- una llamada aislada sobre una orden ajena falla de forma uniforme.

RLS de lectura:

```text
(user_id = app_current_user() AND status=ACTIVE AND not expired)
OR app_allowed_org(owner_org_id)
```

La proyección de cliente consulta exclusivamente acceso efectivo y no amplía RLS general de Orders a todos los usuarios de la organización plataforma.

---

## 8. Tokens live e histórico

Se mantienen físicamente separados.

### 8.1 Histórico

`orders.public_tracking_tokens` conserva estado/timeline sin posición live.

### 8.2 Live

```text
orders.public_live_tracking_tokens
- id
- order_id
- owner_org_id
- token_hash bytea(32)
- delivery_attempt_id
- not_before
- expires_at
- revoked_at
- policy_version
```

Contrato hash idéntico al histórico: SHA-256 sobre UTF-8 exacto del Base64URL token, almacenado como `bytea` de 32 bytes.

Función separada:

```text
security.get_public_live_tracking_projection(token)
```

- `SECURITY DEFINER`, `search_path` fijo;
- fail-closed y 404 uniforme;
- no lee `driver_positions`;
- solo lee proyección sanitizada vigente;
- verifica token, intento, bandera y allowlist de estado.

Revocación al salir de `IN_TRANSIT/DELIVERING`, al cambiar intento o al expirar. El hub desconecta/revalida al vencimiento; reconexión exige token vigente.

---

## 9. SignalR y ubicación

Audiencias:

```text
OperationsHub (OIDC/org)
  OperationsDriverLocationUpdated exacto

CustomerHub (OIDC + grant efectivo)
  sender-order:{order_id}
  SenderPickupApproximateLocationChanged

TrackingHub (token live)
  public-live:{public_order_id}:{delivery_attempt_id}
  PublicDeliveryApproximateLocationChanged
```

Allowlist:

- sender privado solo en `PICKUP_IN_PROGRESS`;
- público solo en `IN_TRANSIT` o `DELIVERING`;
- cualquier otro estado: no publicar.

El consumer vuelve a verificar estado, bandera, intento, asignación, token/grant y edad inmediatamente antes del broadcast. Un punto encolado antes de un cambio de estado se descarta si ya no es elegible.

La proyección aproximada:

- se deriva en servidor;
- no incluye driver ID, contacto, speed, heading o breadcrumb;
- expone `captured_at`, `accuracy_radius_m` y punto/celda/segmento aproximado;
- umbral público candidato 200–300 m, no normativo hasta GATE-007;
- umbral sender también requiere aprobación cuantitativa;
- expira al salir del tramo.

Bootstrap nunca recibe acceso a GPS crudo.

---

## 10. Lane GPS, proximidad y SLO

Toda posición usa `platform.location_outbox_events`. No se crea un evento de negocio por punto.

El consumer coalesce y conserva el último punto bajo presión. El cruce de geofence genera un único comando Notifications por `order_id + delivery_attempt_id`, con hysteresis, cooldown e idempotencia.

Objetivos candidatos sujetos a capacidad/GATE-013:

- máximo una publicación cada 3–5 s en viaje foreground;
- p95 persistencia→cliente <5 s;
- no publicar punto >30 s de antigüedad;
- degradación a ETA/estado;
- lane de negocio dentro de su presupuesto de lag.

---

## 11. Privacidad

GATE-007 debe aprobar antes de implementación:

- consentimiento/base legal de destinatario y driver;
- proveedores/subprocesadores y transferencias;
- precisión, frecuencia y retraso;
- retención de GPS crudo, proyección y verificaciones;
- acceso de soporte a PII/secretos;
- opt-out/canal alterno;
- respuesta ante URL filtrada;
- ARCO y borrado criptográfico;
- límites de uso laboral/analítico.

---

## 12. Delta normativo indivisible

Actualizar juntos: AI-02/03/04/05/06/07/08/09/12/13/18/24, ADR-014/020/022/028, validador y ArchitectureTests.

En particular:

- 18 estados en todos los enums/checks/mapas/tests;
- retirar `DELIVERY_CODE` del upload binario;
- nuevas tablas append-only y RLS;
- funciones/grants de tokens live y customer access;
- AI-18 sin acceso directo indebido;
- mapa SQL/C# de 18 estados;
- guard compuesto 032/033.

---

## 13. Pruebas de aprobación

1. Conjuntos de 18 estados idénticos.
2. Ruta pickup obligatoria y cancelación pre-custodia.
3. Invariantes de flags y snapshot quote→order.
4. Cuatro combinaciones live/código.
5. HMAC, contador concurrente, expiración, rotación y consumo único.
6. Código ausente de superficies prohibidas.
7. `CompleteDelivery` concurrente/idempotente.
8. Tabla completa código×sello.
9. `SECURITY_HOLD` persiste incidente y no entrega.
10. Excepción supervisada no sustituye sello.
11. Grants de customer access sin auto-concesión/cross-user.
12. Tokens live e histórico aislados.
13. Puntos encolados obsoletos descartados.
14. No GPS crudo en proyecciones públicas.
15. Proximidad idempotente.
16. Carga y multiinstancia después de GATE-013.
17. Testcontainers con roles reales, FORCE RLS y funciones `SECURITY DEFINER`.

---

## 14. Veredicto del candidato

Listo para revisión arquitectónica conjunta con ADR-033. No autoriza implementación.


---

## Nota de cierre (consenso de revisión cruzada)

Este ADR es el resultado de revisión cruzada iterativa Claude↔Codex sobre la base normativa v0.6 canónica (SHA-256 de AI-06_SCHEMA.sql: `4b5fe5397ff088b63e0c288770903512665c5fe8a8dc7401d7e4d3af64643505`). Se congela como **referencia de diseño estable** para no perder el trabajo acumulado, con estas condiciones explícitas registradas por Claude en la verificación final:

1. **Es diseño, no implementación.** Ningún delta de AI-06/AI-18 (SQL), AI-05 (OpenAPI) ni migración ha sido escrito ni ejecutado. Las afirmaciones de este ADR sobre comportamiento en base de datos son estáticas: no han corrido contra PostgreSQL/PostGIS real.
2. **Gates pendientes y bloqueantes:** GATE-007 (privacidad: ubicación del repartidor, teléfono del destinatario, evidencia fotográfica) y, para producción multiinstancia, GATE-013. Las decisiones abiertas (umbral de precisión, SLO, proveedor físico del sello, valor final de lotes activos, política de entrega manual bajo STRICT, dictamen legal de cadena de custodia) dependen de evidencia externa y no las cierra este documento.
3. **Verificación real pendiente:** la validez de los invariantes de inventario, del guard compuesto y de la atomicidad debe demostrarse con las pruebas Testcontainers concurrentes con roles reales descritas en el plan de pruebas asociado, antes de cualquier aprobación de implementación.

Congelar este ADR permite pasar a la fase de implementación (empezando por la fundación verificada: aplicar AI-06/AI-18 v0.6 en PostgreSQL real y correr los escenarios de aceptación existentes) sin perder el hilo de estas decisiones.
