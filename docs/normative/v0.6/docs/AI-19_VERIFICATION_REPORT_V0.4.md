# AI-19 — Verificación independiente de hallazgos sobre v0.4

**Paquete revisado:** `Paqueteria_Culiacan_AI_Implementation_v0.4_Contract_Hardening`  
**Tipo de revisión:** consistencia entre contratos, arquitectura, OpenAPI, modelo de dominio, SQL, backlog y pruebas  
**Resultado general:** los 20 hallazgos contienen una oportunidad real. Doce son defectos contractuales o de seguridad que deben corregirse antes o durante Foundation/MVP-0; cuatro son correcciones necesarias antes del piloto o crecimiento local; cuatro son medidas de gobierno o evolución a largo plazo.

## Escala de veredicto

- **CONFIRMADO:** el problema está presente en los archivos normativos.
- **CONFIRMADO CON MATIZ:** el núcleo es correcto, pero una parte de la explicación o de la solución propuesta debe ajustarse.
- **OPORTUNIDAD CONFIRMADA:** no rompe el MVP actual, pero debe resolverse antes de la fase indicada.

## Resumen ejecutivo

| # | Veredicto | Prioridad recomendada | Decisión |
|---|---|---:|---|
| 1 | Confirmado con matiz | P0 | Restringir grants de tablas append-only; outbox requiere actualización limitada de columnas operativas. |
| 2 | Confirmado | P0 BLOCKER | Definir bootstrap RLS mediante funciones `SECURITY DEFINER` acotadas. |
| 3 | Confirmado | P0 BLOCKER | Adoptar carga directa a object storage con sesión/URL firmada y finalización JSON. |
| 4 | Confirmado | P0 | Alinear esquemas físicos por módulo y grants/RLS, o declarar un esquema único. |
| 5 | Confirmado | P0 | Una cotización solo puede consumirse una vez: `UNIQUE (quote_id)` y marca de consumo. |
| 6 | Confirmado | P0/P1 | Incorporar ciudad, área de servicio y zona operativa en el primer esquema. |
| 7 | Confirmado | P0 | Añadir guard de custodia a `RESCHEDULED -> DELIVERING`. |
| 8 | Confirmado con matiz | P0 | Completar OpenAPI y definir contexto de organización y política 403/404. |
| 9 | Confirmado | P0 privacidad | El snapshot de cotización debe ser no-PII o cifrado explícitamente. |
| 10 | Confirmado | P0/P1 seguridad | Worker normal sujeto a RLS; elevación solo por funciones/roles acotados. |
| 11 | Confirmado | P0 seguridad | Contexto RLS siempre con `SET LOCAL`/`set_config(..., true)` dentro de transacción. |
| 12 | Confirmado con matiz | P1 | Separar telemetría del outbox de negocio, publicar con throttling y aceptar lotes. |
| 13 | Confirmado | P0 negocio | Guardar la clase/tier de tarifa; no inferir el guard por `net_cents`. |
| 14 | Confirmado | P0 si COD entra al MVP | Crear ledger mínimo de COD y líneas de liquidación, o desactivar formalmente COD en MVP-0. |
| 15 | Oportunidad confirmada | P1 antes de LOCAL_GROWTH | Denormalizar tenant en tablas hijas de alto volumen. |
| 16 | Confirmado | P1 | Índices/limpieza de idempotencia, CHECK/enum y membresía activa parcial. |
| 17 | Oportunidad confirmada | P1/P2 | Definir ventana de reclamación y estado final o separar Claim del estado de Order. |
| 18 | Oportunidad confirmada | P1 antes de datos reales | Retención/partición específica para ubicación de trabajadores. |
| 19 | Oportunidad confirmada | P0 gobierno | ArchitectureTests deben permitir solo los flujos atómicos registrados. |
| 20 | Oportunidad confirmada | P1 arquitectura | Separar desde ahora topic/consumer/retención de TrackingAndLocation. |

---

## Hallazgos verificados

### 1. Grants contra append-only — CONFIRMADO CON MATIZ

**Evidencia:** `AI-18_DATABASE_ROLE_MODEL.sql:12-16` concede CRUD sobre todas las tablas. `AI-04_DOMAIN_MODEL.yaml:154-171,190-207` marca `OrderEvent`, `Proof`, `AuditLog` y `OutboxEvent` como inmutables; `AI-03_ARCHITECTURE.md:248,410,531` exige append-only.

**Evaluación:** el riesgo es real para `order_events`, `proofs` y `audit_logs`: un usuario dentro del tenant puede actualizar o borrar si la aplicación expone accidentalmente esa operación. Sin embargo, revocar toda actualización sobre `outbox_events` rompería el procesamiento porque el Worker debe modificar `status`, `attempts`, locks y `processed_at`.

**Corrección recomendada:**

- API: `SELECT, INSERT` sobre eventos/POD/auditoría; sin `UPDATE` ni `DELETE`.
- Worker: `SELECT` y `UPDATE` únicamente sobre columnas operativas del outbox; sin modificación de `topic`, `aggregate_id`, `tenant_context` o `payload`.
- Ningún rol runtime puede borrar estas tablas.
- Trigger defensivo para impedir cambios a payloads inmutables.
- Corregir el modelo: `OutboxEvent` tiene contenido inmutable y estado de entrega mutable; no debe declararse fila completamente inmutable.

### 2. Bootstrap de RLS — CONFIRMADO, BLOCKER

**Evidencia:** `AI-06_SCHEMA.sql:191-192` requiere `app_current_user()` o membresía autorizada para leer usuario/membresías, pero no existe función de resolución por `identity_subject`. El tracking anónimo (`AI-05_OPENAPI.yaml`, `/tracking/{token}`) tampoco tiene mecanismo de bootstrap bajo FORCE RLS.

**Riesgo:** un implementador podría usar el rol BYPASSRLS del Worker para resolver identidad o tracking público, ampliando privilegios de forma insegura.

**Corrección recomendada:** ADR y funciones acotadas:

- `security.resolve_identity_context(identity_subject)` como `SECURITY DEFINER`, con `search_path` fijo, que devuelve solo `user_id`, membresías activas y organización predeterminada.
- `tracking.get_public_projection(token_hash)` como `SECURITY DEFINER`, que valida expiración/rotación y devuelve únicamente el DTO público.
- Revocar `PUBLIC EXECUTE`; conceder solo al rol de aplicación.
- No fijar `app.current_org_ids` a partir de un token público arbitrario.
- Pruebas de enumeración, token inválido y ausencia de acceso a tablas generales.

### 3. POD multipart vs URL firmada — CONFIRMADO, BLOCKER

**Evidencia:** `AI-03_ARCHITECTURE.md:357-367` y `ADR-008` ordenan URL firmada, cuarentena y promoción. `AI-05_OPENAPI.yaml:216-229,756-782` define carga multipart directa a la API.

**Decisión recomendada:** conservar la arquitectura de object storage:

1. `POST /orders/{id}/proof-upload-sessions` crea una sesión y URL firmada.
2. La PWA carga el binario directamente a cuarentena.
3. `POST /orders/{id}/proofs` recibe JSON con `upload_id`, hash, metadata y `captured_at`.
4. El Worker valida/promueve el archivo.
5. La transacción crea POD/evento cuando el objeto fue validado.

Para operación offline, el archivo queda temporalmente en IndexedDB y solicita una sesión al recuperar conectividad.

### 4. Esquemas por módulo vs `public` — CONFIRMADO

**Evidencia:** `AI-13_DOTNET_SOLUTION_BLUEPRINT.md:61-69` exige esquema y DbContext propios por módulo. `AI-06_SCHEMA.sql` crea todo sin schema qualification y `AI-18` concede privilegios solo sobre `public`.

**Recomendación:** como el desarrollo aún no inicia, mantener la decisión modular y crear esquemas físicos por módulo, por ejemplo `identity`, `organizations`, `locations`, `pricing`, `orders`, `dispatch`, `drivers`, `routes`, `custody`, `finance`, `platform`. Los grants/default privileges deben aplicarse a cada schema mediante script parametrizado. Las funciones RLS deben vivir en `security` con `search_path` fijo.

Una alternativa válida es un único schema físico, pero tendría que aprobarse mediante ADR y corregir AI-13. No deben coexistir ambas instrucciones.

### 5. Reutilización de cotización — CONFIRMADO

**Evidencia:** `orders.quote_id` en `AI-06_SCHEMA.sql:76` no es único. La idempotencia solo deduplica la misma clave y scope, no dos requests diferentes.

**Corrección:**

- `UNIQUE (quote_id)` en orders.
- `quotes.consumed_at` y opcionalmente `consumed_by_order_id` para auditoría.
- La creación bloquea/consume la quote dentro de la misma transacción.
- Respuesta uniforme `409 QUOTE_ALREADY_CONSUMED`.

### 6. Multi-ciudad ausente en SQL — CONFIRMADO

**Evidencia:** `AI-03_ARCHITECTURE.md:258` y `AI-15_SCALABILITY_CONTRACT.yaml:187-192` exigen `city_id`, `service_area_id` y `operating_zone_id`; el esquema solo contiene `zones.owner_org_id`.

**Corrección:** introducir desde la primera migración:

- `cities` con zona horaria y códigos geográficos.
- `service_areas` como cobertura comercial/operativa.
- `operating_zones` como subdivisión tarifaria/operativa; sustituye o redefine `zones`.
- Relaciones en locations, tariff rules, quotes/orders y autorizaciones de drivers/allies.
- Snapshot de IDs geográficos en quote/order para reproducibilidad histórica.

### 7. Guard de custodia en reprogramación — CONFIRMADO

**Evidencia:** `AI-04_DOMAIN_MODEL.yaml:268-275,311-312` permite `FAILED_ATTEMPT -> RESCHEDULED -> DELIVERING`, pero el guard solo reconoce la transición directa desde `FAILED_ATTEMPT`.

**Corrección:** `RESCHEDULED -> DELIVERING` exige custodia activa y assignment válido. Si la custodia se devolvió, la ruta correcta es `RESCHEDULED -> READY_FOR_PICKUP/ASSIGNED -> AT_PICKUP/PICKED_UP` según el nuevo intento.

### 8. OpenAPI incompleto — CONFIRMADO CON MATIZ

**Confirmado:**

- `GEO-001` exige endpoints de locations/zones y no existen.
- No existe `GET /quotes/{quoteId}`.
- No está definido cómo seleccionar organización activa.
- No hay endpoints administrativos para documentos del repartidor.
- La mayoría de operaciones no declaran 401/403.
- La prueba habla de política uniforme 403/404, pero no existe ADR/política normativa.
- `RouteChanged.route_version` no tiene columna `routes.version`.

**Matiz:** `eligible_constraints` en SQL es JSON genérico y puede contener `eligible_vehicle_types`; no es por sí solo una contradicción, pero falta un contrato de serialización/versionado.

**Corrección:** completar OpenAPI antes de generar clientes:

- Contexto activo mediante header obligatorio `X-Organization-Id` para endpoints tenant autenticados, validado contra membresías; el token no debe aceptar roles del cliente.
- Endpoints de contexto/membresías (`GET /me/organizations`, `PUT /me/active-organization` solo si se decide persistir preferencia).
- Política: recursos tenant ajenos devuelven 404; falta de permiso sobre un recurso visible/contexto devuelve 403; falta de autenticación 401.
- Añadir schemas/respuestas globales y version a Route.
- Definir `ExternalOfferEligibility` como schema estable en lugar de JSON sin contrato.

### 9. PII en snapshots de quote — CONFIRMADO

**Evidencia:** `AddressInput` requiere nombre/teléfono y `quotes.request_snapshot` es `jsonb` sin cifrado. `locations` sí cifra dichos datos.

**Corrección:** el snapshot para reproducir precio debe contener solo datos no personales: IDs de location, zonas/áreas, distancia, dimensiones, servicio y reglas. No debe duplicar nombre/teléfono/referencias. Si existe obligación legal de congelar una dirección completa, usar snapshot cifrado con key version y política de retención explícita.

### 10. Worker con BYPASSRLS global — CONFIRMADO

**Evidencia:** `AI-18_DATABASE_ROLE_MODEL.sql:6` crea `paqueteria_worker BYPASSRLS` para todos los consumers.

**Corrección recomendada:**

- Worker normal `NOBYPASSRLS`.
- Claim cross-tenant mediante función `SECURITY DEFINER` mínima que bloquea y devuelve eventos con tenant context.
- Procesamiento de cada evento en una transacción con contexto tenant local.
- Funciones/roles elevados distintos y auditados solo para mantenimiento o conciliación global que lo requiera.

### 11. Contexto RLS y PgBouncer — CONFIRMADO

**Evidencia:** AI-15 planea PgBouncer, pero ningún contrato exige contexto transaction-scoped. Las funciones leen `current_setting`.

**Corrección obligatoria desde MVP-0:** iniciar transacción por unidad de trabajo y ejecutar `set_config('app.current_user_id', ..., true)` y `set_config('app.current_org_ids', ..., true)`. El tercer argumento `true` equivale a ámbito local de transacción. Nunca usar `SET` de sesión. Añadir prueba con reutilización de conexión y PgBouncer en transaction pooling.

### 12. GPS en outbox general — CONFIRMADO CON MATIZ

**Evidencia:** ADR-015 y backlog exigen un outbox event por posición. SignalR sí ordena `throttle server publication`, lo cual mitiga visualización, pero no reduce filas del outbox ni costo de claim.

**Corrección:**

- Endpoint batch: recibe 1-20 puntos ordenados y responde resultados por `client_event_id`.
- Persistir posiciones aceptadas, preferentemente mediante inserción por lote.
- Outbox separado o topic/índice/consumer de prioridad baja para telemetría.
- Publicar solo la última posición por ventana temporal/movimiento significativo.
- Retención y partición independientes del outbox de negocio.
- Los eventos de dinero, custodia y orden siempre tienen prioridad superior.

### 13. Guard de precio acoplado a IVA — CONFIRMADO

**Evidencia:** el CHECK usa `net_cents > 5200`, mientras GATE-011 aún decide si la tarifa pública incluye IVA.

**Mejor corrección:** no inferir la regla por importe. Guardar `pricing_tier_code` o `tariff_rule_class` en quote/order y exigir ruta consolidada/override para tiers de volumen. Esto también evita falsos positivos por descuentos, redondeos o cambios futuros de tarifa.

### 14. COD y liquidaciones sin detalle — CONFIRMADO

**Evidencia:** la máquina de estados exige COD registrado/conciliado en DELIVERED, pero no hay entidad o tabla de COD/pagos. `settlements` solo tiene totales y FIN-001 llega en MVP-1.

**Opciones:**

- Si COD no forma parte de MVP-0: declararlo explícitamente deshabilitado y hacer el guard aplicable solo cuando `cod_required=true`, campo que debe existir.
- Si COD entra al piloto: adelantar un ledger mínimo (`cash_collections`, expected/received/handed_over/reconciled) y `settlement_lines` por orden/ajuste.

La segunda opción es coherente con la propuesta comercial, pero requiere aprobación financiera y operativa.

### 15. RLS por subquery en tablas de volumen — OPORTUNIDAD CONFIRMADA

No es un defecto funcional inmediato. En `order_events`, `proofs` e `incidents` la política consulta al padre. Antes de particionar o crecer, denormalizar `owner_org_id` y `operator_org_id` con integridad garantizada por trigger/aplicación, y crear índices tenant/fecha. La función de contexto debería operar sobre `uuid[]`, no CSV.

### 16. Higiene de esquema — CONFIRMADO

- Falta índice y tarea de limpieza en `idempotency_keys.expires_at`.
- Varios campos que son enum en OpenAPI permanecen como text libre sin CHECK.
- La restricción absoluta de membresía impide reotorgar históricamente el mismo rol.

**Corrección:** índice parcial/cron de expiración; tipos enum o CHECK compartidos; índice único parcial de membresía activa y conservación de historial.

### 17. Ausencia de finalización definitiva — OPORTUNIDAD CONFIRMADA

La ruta feliz termina en CLOSED pero puede abrir CLAIM indefinidamente. Recomendación preferida: Claim como aggregate separado y Order permanece CLOSED; el token/ventana de reclamo se rige por `claim_deadline`. Alternativa: añadir FINALIZED después de la ventana y tras resolver reclamaciones. La duración requiere gate legal/comercial.

### 18. Retención de ubicaciones — OPORTUNIDAD CONFIRMADA

GATE-007 es genérico y DriverPosition menciona retención, pero debe nombrar expresamente telemetría laboral: frecuencia, finalidad, acceso, retención, anonimización/agrupación y derechos. El particionamiento de `driver_positions` debe ejecutarse antes de LOCAL_GROWTH.

### 19. Erosión del coordinador transaccional — OPORTUNIDAD CONFIRMADA

`AI-13` enumera cinco flujos permitidos, pero ArchitectureTests solo exige límites de referencias. Debe existir una allowlist ejecutable de coordinadores/casos de uso autorizados. Cualquier sexto flujo requiere ADR y actualización de pruebas.

### 20. Extracción futura de TrackingAndLocation — OPORTUNIDAD CONFIRMADA

La extracción ya está prevista, pero telemetría comparte outbox y políticas con negocio. Separar topic, consumer, índices, retención y contratos desde ahora reduce significativamente la futura cirugía. No requiere microservicio inmediato.

---

## Secuencia recomendada

### Gate A — antes de FND-001/TEN-001

1. ADR bootstrap RLS y tracking público.
2. ADR/schema físico por módulo.
3. Grants append-only y modelo de worker privilegiado.
4. Mandato `SET LOCAL` transaction-scoped.
5. Política uniforme 401/403/404 y header de organización activa.

### Gate B — antes de PRC-001/ORD-001

6. Multi-ciudad y zonas desde primera migración.
7. Quote de consumo único.
8. Snapshot no-PII.
9. Guard por tier tarifario, no por monto neto.
10. Guard completo de custodia.

### Gate C — antes de POD-001/DRV-002

11. Flujo de evidencias con URL firmada y finalización.
12. API batch y canal separado de telemetría.
13. Definición de COD mínimo o exclusión del MVP-0.

### Antes del piloto real

14. Checks/enums, limpieza de idempotencia, membresía histórica.
15. Retención de GPS y PII aprobada.
16. Ledger/settlement lines si existe efectivo.

### Antes de LOCAL_GROWTH

17. Denormalización tenant en eventos/telemetría.
18. Particionamiento y retención.
19. Allowlist de transacciones cross-module.
20. Separación operativa TrackingAndLocation.

## Conclusión

Los hallazgos no deben copiarse literalmente como parches. El análisis externo fue técnicamente fuerte y detectó áreas reales. La acción correcta es consolidarlas en una v0.5 con decisiones arquitectónicas explícitas, no añadir soluciones locales inconexas. En especial, los puntos 2, 3, 4, 10 y 11 deben resolverse juntos porque forman un único diseño de seguridad/persistencia/despliegue.
