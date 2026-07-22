# ADR-033 — Inventario de sellos de integridad y cadena de custodia física

**Estado:** ACEPTADO como referencia de diseño para v0.7 (congelado 2026-07-21 tras revisión cruzada Claude↔Codex). No autoriza implementación por sí solo: los deltas de contrato/SQL, GATE-007/GATE-013 y las pruebas Testcontainers siguen siendo  requisito.
**Versión del candidato:** `0.7-review-2`  
**Fecha:** 2026-07-21  
**Base normativa:** Paquetería Culiacán v0.6 Full Canonical Sync.  
**ADR hermano:** ADR-032 — tracking live y verificación segura de entrega.

**Bloqueos:** aprobación conjunta con ADR-032; especificación del proveedor/material físico; GATE-007 para fotografías/datos del driver; delta normativo coordinado; pruebas PostgreSQL reales.

---

## 0. Decisiones cerradas

1. El inventario pertenece al módulo/esquema `custody`, no a Drivers.
2. El tenant del inventario es `inventory_org_id`: la organización que opera/custodia físicamente los sellos.
3. El vínculo sello→orden no es una columna mutable: es un enlace append-only por `custody_leg_id`.
4. Un sello físico se usa una sola vez en toda su vida; una orden puede tener un nuevo sello únicamente en un nuevo tramo de custodia.
5. El identificador se canonicaliza y el QR contiene autenticidad criptográfica; unicidad no se confunde con genuinidad.
6. La logística mínima de handoff físico se modela.
7. El máximo de lotes activos es política versionada, valor inicial candidato 2, con enforcement atómico mediante slots.
8. La operación cotidiana usa la capacidad `SEAL_INVENTORY_MANAGE` del rol aplicativo `INVENTORY_CUSTODIAN`, no `PLATFORM_ADMIN`.
9. La entrada manual es evidencia degradada, se bloquea automáticamente por abuso y en pickup requiere supervisión.
10. Un fallo de sello produce `SECURITY_HOLD`, no culpabilidad automática ni rollback del incidente.

---

## 1. Alcance y postura

El sello aporta evidencia de integridad y continuidad de custodia. No demuestra por sí solo desconocimiento legal del contenido ni elimina responsabilidades regulatorias. La redacción contractual será “aporta evidencia relevante”, no “defensa absoluta”.

La resistencia antifraude combina:

- payload auténtico;
- material físico destructivo o evidenciable;
- serial visual;
- fotografía;
- enlace append-only;
- custodio actual;
- re-verificación en entrega.

---

## 2. Opción de servicio

`tamper_seal_required` se elige en quote y se copia inmutable a order.

Invariantes con ADR-032:

```text
STRICT_CODE_ONLY ⇒ tamper_seal_required=true
usuario-a-usuario ⇒ tamper_seal_required=true
```

No se activa después de `CONFIRMED` sin override auditado y recotización.

---

## 3. Tenant, capacidades y roles

### 3.1 Tenant

```text
inventory_org_id = organización operadora/custodio del inventario
```

No se asume que sea `order.owner_org_id`. El enlace guarda:

- `inventory_org_id`;
- `order_owner_org_id`;
- `operator_org_id`.

El sello solo puede usarse si su `inventory_org_id` coincide con el operador efectivo de la orden o con una relación de inventario autorizada explícitamente. Este ADR no convierte `driver_profiles` a multi-org: el driver opera bajo su `org_id` actual y la organización operadora efectiva. Un rediseño de perfil multi-org requiere ADR separado.

### 3.2 Rol aplicativo

Nuevo rol/capacidad:

```text
INVENTORY_CUSTODIAN
SEAL_INVENTORY_MANAGE
```

Puede importar lotes, ejecutar handoff, recibir devoluciones, conciliar y anular sellos no usados. `PLATFORM_ADMIN` conserva emergencia auditada, no operación cotidiana.

### 3.3 Rol de base

El delta AI-18 puede introducir `paqueteria_inventory_executor NOLOGIN BYPASSRLS` como propietario limitado de funciones `SECURITY DEFINER`. Ningún login runtime obtiene membresía ni BYPASSRLS. Direct table mutation se revoca para operaciones de lifecycle; app/worker ejecutan funciones explícitas.

---

## 4. Identidad y autenticidad del sello

### 4.1 Forma canónica

`seal_identifier_canonical`:

- Base32 Crockford o alfabeto equivalente aprobado;
- mayúsculas;
- longitud fija definida por proveedor;
- sin espacios ni Unicode libre;
- `CHECK` SQL y canonicalizador compartido;
- `UNIQUE` global sobre valor canónico.

### 4.2 Payload auténtico

El QR contiene:

```text
schema_version
seal_identifier
random_nonce >= 128 bits
key_version
authenticity_tag
```

`authenticity_tag` es HMAC o firma verificable según el modelo del proveedor. La clave no se almacena en claro en DB. Un código de barras sin tag autenticado solo puede usarse como serial visual, no como prueba criptográfica.

Entrada manual del serial tiene `assurance_level=MANUAL_VISUAL`, nunca `CRYPTOGRAPHIC_SCAN`.

### 4.3 Material físico

Antes de aprobar implementación se exige especificación del proveedor:

- material destructivo/evidencia de apertura;
- serial legible y resistente;
- holograma/patrón o controles equivalentes;
- procedimiento de recepción de lotes;
- manejo de defectos y duplicados de fábrica.

---

## 5. Modelo conceptual

Todas las tablas de evidencia/lifecycle indicadas como append-only rechazan UPDATE/DELETE runtime y se incorporan a AI-18/triggers.

### 5.1 `custody.seal_batches`

- `id`, `inventory_org_id`;
- proveedor/lote externo;
- `expected_seal_count`;
- estado `CREATED | READY_FOR_HANDOFF | PENDING_ACCEPTANCE | ACTIVE | DEPLETED | RETURN_PENDING | RETURNED | VOIDED`;
- policy/version y timestamps.

Los conteos usados/disponibles/anulados se derivan. `expected_seal_count` se verifica al cerrar importación.

### 5.2 `custody.seals`

- `id`, `inventory_org_id`, `batch_id`;
- `seal_identifier_canonical UNIQUE`;
- nonce/tag/key version;
- estado `GENERATED | ASSIGNED | LINKED_TO_ORDER | DELIVERY_VERIFIED | VOIDED | UNDER_INVESTIGATION | INVESTIGATION_CLOSED`;
- versión optimista;
- proyección de custodio actual opcional.

No contiene `linked_order_id` mutable.

### 5.3 `custody.seal_batch_handoffs` — append-only

- batch;
- `inventory_org_id`;
- origen y destino;
- driver;
- handed-over-by / accepted-by;
- conteo esperado/aceptado;
- estado `PENDING | ACCEPTED | REJECTED | RETURNED`;
- timestamps y discrepancias.

Un lote no se vuelve `ACTIVE` hasta aceptación del driver.

### 5.4 `custody.driver_seal_batch_slots`

- `inventory_org_id`, `driver_id`, `slot_number`;
- `batch_id UNIQUE`, `assigned_at`;
- PK `(inventory_org_id,driver_id,slot_number)`.

La política define `max_active_batch_slots`; valor inicial candidato 2. `assign_seal_batch` bloquea la fila de política/version, selecciona slots `1..N` y hace `INSERT ... ON CONFLICT`. La PK/UNIQUE cierra carreras aun entre nodos. Direct INSERT/UPDATE/DELETE está revocado.

Liberación de slot solo al confirmar `DEPLETED`, devolución aceptada o retiro conciliado.

### 5.5 `custody.custody_legs`

- `id`, `order_id`;
- owner/operator/inventory org;
- assignment y driver;
- estado `ACTIVE | COMPLETED | SECURITY_HOLD | RETURNED`;
- inicio/fin;
- índice único parcial: máximo un leg activo por orden.

Un nuevo sello se permite solo en un nuevo `custody_leg_id`, no por cada `delivery_attempt_id`.

### 5.6 `custody.order_seal_links` — append-only

- `id`, `seal_id`, `order_id`, `custody_leg_id`;
- owner/operator/inventory org;
- driver/assignment;
- método y assurance level;
- linked_at.

Restricciones:

```text
UNIQUE(seal_id)
UNIQUE(order_id,custody_leg_id)
```

FKs compuestas garantizan coherencia de tenant entre sello, lote, leg y orden.

### 5.7 `custody.seal_events` — append-only

Registra transición, actor, razón, expected version, timestamp y referencias; no expone payload QR crudo en outbox/logs.

### 5.8 Evidencia de escaneo

`custody.seal_observations` append-only:

- expected seal;
- observed seal si existe;
- order/leg/attempt;
- `SCAN | MANUAL_VISUAL`;
- `MATCH | MISMATCH | BROKEN | INVALID_AUTHENTICITY`;
- proof photo ID;
- actor/geofence/timestamp.

---

## 6. Ciclos de vida

### 6.1 Sello

```text
GENERATED → ASSIGNED | VOIDED
ASSIGNED → LINKED_TO_ORDER | VOIDED
LINKED_TO_ORDER → DELIVERY_VERIFIED | UNDER_INVESTIGATION
DELIVERY_VERIFIED → terminal
VOIDED → terminal
UNDER_INVESTIGATION → INVESTIGATION_CLOSED
```

`INVESTIGATION_CLOSED` es terminal. Un sello investigado nunca vuelve a disponible ni enlazable, aunque la investigación concluya que no hubo fraude.

### 6.2 Lote

```text
CREATED → READY_FOR_HANDOFF → PENDING_ACCEPTANCE → ACTIVE
ACTIVE → DEPLETED | RETURN_PENDING | VOIDED
RETURN_PENDING → RETURNED
```

Estados que ocupan slot: `PENDING_ACCEPTANCE` y `ACTIVE`, configurable por política.

---

## 7. Pickup y vínculo atómico

Secuencia:

```text
ASSIGNED → PICKUP_IN_PROGRESS → AT_PICKUP
→ CompletePickup
→ PICKED_UP
```

Si `tamper_seal_required=true`, `CompletePickup` dentro de `proof_custody_to_order_transition`:

1. valida tenant, asignación, driver y versión;
2. valida payload auténtico o entrada manual supervisada;
3. comprueba sello `ASSIGNED` bajo custodia del driver;
4. valida `PICKUP_SEAL_PHOTO` definitivo/READY;
5. crea `custody_leg` activa;
6. inserta `order_seal_link` append-only;
7. transiciona sello `LINKED_TO_ORDER`;
8. transiciona orden `PICKED_UP`;
9. inserta order/seal events, auditoría y outbox.

Todo confirma o revierte junto.

La transición `ASSIGNED → PICKUP_IN_PROGRESS` ya exige que el driver disponga de un sello asignado cuando la orden lo requiere, evitando llegada sin inventario.

---

## 8. Entrega y guard compuesto

La entrega usa `CompleteDelivery` de ADR-032.

La comparación se ancla en el enlace append-only del `custody_leg_id` actual:

```text
observed token → seal_id
expected seal_id = order_seal_link.seal_id
```

No se compara contra una columna mutable de Orders.

Para entregar:

- escaneo/tag auténtico o manual permitido;
- sello observado = sello esperado;
- estado físico íntegro;
- `DELIVERY_SEAL_PHOTO` definitivo;
- transición sello `DELIVERY_VERIFIED` en la misma transacción que Order `DELIVERED`.

### 8.1 Mismatch o sello roto

Resultado `SECURITY_HOLD`:

- no `DELIVERED`;
- orden `FAILED_ATTEMPT`, razón `SEAL_SECURITY_HOLD`;
- custody leg `SECURITY_HOLD`;
- expected seal `UNDER_INVESTIGATION`;
- observed seal también `UNDER_INVESTIGATION` si existe;
- incidente con expected/observed IDs;
- código revocado según ADR-032;
- eventos/outbox.

El payload observado desconocido se almacena redacted/hasheado, no en logs.

Después del commit, `DriverSecurityHoldRequested` bloquea nuevas asignaciones. El driver conserva acceso a retornos activos. No se presume culpabilidad automática.

---

## 9. Proofs fotográficos

ADR-020 continúa como flujo binario. Se agregan propósitos/tipos inequívocos:

```text
PICKUP_SEAL_PHOTO
DELIVERY_SEAL_PHOTO
```

El comando de pickup/delivery solo acepta Proof definitivo que pasó cuarentena/validación. No crea archivos ficticios para escaneos o seriales.

---

## 10. Entrada manual

La entrada manual es fallback degradado, no equivalente al QR auténtico.

**Pickup:** en flujos `STRICT_CODE_ONLY` o cualquier policy que exija autenticidad criptográfica, la entrada manual no satisface el guard. El sello ilegible se marca `VOIDED` y se usa otro sello escaneable. En una policy no estricta, un supervisor puede autorizar fallback manual solo si la característica física secundaria aprobada y la foto permiten verificar el sello.

**Delivery:** puede usarse fallback manual porque el sello esperado ya fue autenticado y ligado en pickup; aun así exige coincidencia exacta, integridad visual y controles.

Controles obligatorios:

1. coincidencia exacta contra expected seal;
2. foto camera-only y legible;
3. geofence y asignación activa;
4. mínimo de intentos de escaneo + reason code;
5. idempotency key;
6. evento append-only y `assurance_level=MANUAL_VISUAL`;
7. contador por driver/ventana;
8. bloqueo automático al superar umbral;
9. supervisor según policy;
10. jamás validar un tag inválido o un serial no legible.

Si no puede verificarse el identificador esperado, no se completa pickup/entrega.

---

## 11. RLS y privilegios

Todas las tablas tienen tenant directo. Políticas:

- inventario: `app_allowed_org(inventory_org_id)`;
- enlaces/legs: owner u operator autorizado; inventario solo para la org custodio;
- driver: acceso limitado a handoffs/lotes bajo su identidad mediante helper `SECURITY DEFINER` no recursivo que resuelve `app_current_driver_id()`;
- ningún runtime BYPASSRLS;
- lifecycle solo mediante funciones propiedad de `paqueteria_inventory_executor`;
- links/events/observations/handoffs append-only;
- outbox contiene IDs internos, no serial/tag/payload escaneado.

FKs compuestas y tests de catálogo son obligatorios.

---

## 12. Logística física mínima

Se modela:

- recepción/importación de lote;
- handoff y aceptación por driver;
- conteo esperado/aceptado;
- devolución;
- discrepancias/pérdidas;
- custodio actual;
- liberación de slots.

Quedan operativos fuera del MVP: compras, pronóstico de reposición y optimización de stock.

---

## 13. Política de lotes

`max_active_batch_slots` es policy versionada por organización. Valor inicial candidato: 2. No es un invariante universal fijo.

Debe validarse en piloto con tamaño de lote, consumo por ruta y frecuencia de reposición. El enforcement sigue siendo atómico cualquiera que sea N dentro del rango soportado.

---

## 14. Delta normativo

Actualizar AI-02/03/04/05/06/07/08/09/13/18/24 y ADR-014/020/032.

Incluye:

- flag quote/order e invariantes;
- schemas/tables/constraints/FORCE RLS;
- capability `SEAL_INVENTORY_MANAGE`;
- funciones inventory lifecycle;
- proof purposes;
- guard compuesto;
- append-only registry;
- tests concurrentes/cross-tenant.

---

## 15. Pruebas de aprobación

1. Duplicado canónico rechazado.
2. Tag auténtico inválido rechazado.
3. Mismo sello no puede enlazarse a dos órdenes.
4. Dos sellos no pueden enlazarse al mismo order+leg.
5. Nuevo leg permite nuevo sello; nuevo delivery attempt no lo exige.
6. Links/events/handoffs no actualizables ni borrables runtime.
7. Tenant/FKs compuestas impiden mezclar orgs.
8. Tres asignaciones concurrentes respetan slots.
9. Rollback no deja slot/handoff parcial.
10. Pickup atómico link+photo+leg+order+outbox.
11. Delivery atómico seal+order+evidence+outbox.
12. Mismatch persiste security hold e incidente.
13. Expected y observed quedan bajo investigación.
14. Entrada manual exige controles y se bloquea por abuso.
15. Driver hold no impide retornos activos.
16. Outbox/logs no contienen serial/tag crudo.
17. FORCE RLS y propietarios/grants correctos.

---

## 16. Veredicto del candidato

Listo para revisión arquitectónica conjunta con ADR-032. No autoriza implementación ni afirma cumplimiento legal o resistencia física sin validar proveedor.


---

## Nota de cierre (consenso de revisión cruzada)

Este ADR es el resultado de revisión cruzada iterativa Claude↔Codex sobre la base normativa v0.6 canónica (SHA-256 de AI-06_SCHEMA.sql: `c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96`). Se congela como **referencia de diseño estable** para no perder el trabajo acumulado, con estas condiciones explícitas registradas por Claude en la verificación final:

1. **Es diseño, no implementación.** Ningún delta de AI-06/AI-18 (SQL), AI-05 (OpenAPI) ni migración ha sido escrito ni ejecutado. Las afirmaciones de este ADR sobre comportamiento en base de datos son estáticas: no han corrido contra PostgreSQL/PostGIS real.
2. **Gates pendientes y bloqueantes:** GATE-007 (privacidad: ubicación del repartidor, teléfono del destinatario, evidencia fotográfica) y, para producción multiinstancia, GATE-013. Las decisiones abiertas (umbral de precisión, SLO, proveedor físico del sello, valor final de lotes activos, política de entrega manual bajo STRICT, dictamen legal de cadena de custodia) dependen de evidencia externa y no las cierra este documento.
3. **Verificación real pendiente:** la validez de los invariantes de inventario, del guard compuesto y de la atomicidad debe demostrarse con las pruebas Testcontainers concurrentes con roles reales descritas en el plan de pruebas asociado, antes de cualquier aprobación de implementación.

Congelar este ADR permite pasar a la fase de implementación (empezando por la fundación verificada: aplicar AI-06/AI-18 v0.6 en PostgreSQL real y correr los escenarios de aceptación existentes) sin perder el hilo de estas decisiones.
