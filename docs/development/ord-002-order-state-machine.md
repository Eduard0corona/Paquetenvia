# ORD-002: máquina de estados de órdenes

ORD-002 agrega el comando autenticado e idempotente `POST /api/v1/orders/{orderId}/transitions`. La implementación conserva el contrato de 17 estados y 30 aristas de AI-02/AI-04/AI-05, aplica autorización y guards con lecturas tenant-scoped, y confirma la orden, el evento, el outbox, la auditoría y la respuesta idempotente en una sola transacción PostgreSQL.

No implementa despacho, captura de pruebas, incidencias, COD, settlements, tracking público, SignalR ni procesamiento del outbox. Solo consume sus filas existentes mediante interfaces de lectura acotadas.

## Capas y responsabilidades

- `Orders.Domain` contiene el vocabulario de 17 estados, la matriz pura, los terminales, la validación de versión y la política de `public_event_code`. No depende de JSON, persistencia ni frameworks.
- `Orders.Application` define el comando, los errores estructurados, la forma canónica, autorización, metadata allowlisted, snapshots de lectura y 23 guards ordenados y con código único.
- `Orders.Infrastructure` ejecuta la transacción, las lecturas cross-schema de solo lectura, el update optimista y las escrituras append-only.
- `Orders.Endpoints` limita el contrato HTTP a binding, autenticación, tenant activo, validación superficial y mapeo 200/409/401/403.

La matriz y las reglas temporales existen en un solo lugar: `OrderTransitionMatrix`. Los endpoints no contienen aristas ni guards. Las lecturas de Pricing, Dispatch, Drivers, Custody, Incidents y Finance no escriben en esos schemas.

## Estados, aristas y terminales

Las únicas aristas permitidas son:

| Origen | Destinos |
| --- | --- |
| `DRAFT` | `CONFIRMED`, `CANCELLED` |
| `CONFIRMED` | `READY_FOR_PICKUP`, `CANCELLED` |
| `READY_FOR_PICKUP` | `ASSIGNED`, `CANCELLED` |
| `ASSIGNED` | `AT_PICKUP`, `READY_FOR_PICKUP`, `CANCELLED` |
| `AT_PICKUP` | `PICKED_UP`, `FAILED_ATTEMPT`, `CANCELLED` |
| `PICKED_UP` | `IN_TRANSIT`, `RETURNING` |
| `IN_TRANSIT` | `DELIVERING`, `FAILED_ATTEMPT`, `RETURNING` |
| `DELIVERING` | `DELIVERED`, `FAILED_ATTEMPT` |
| `FAILED_ATTEMPT` | `RESCHEDULED`, `RETURNING`, `DELIVERING` |
| `RESCHEDULED` | `READY_FOR_PICKUP`, `ASSIGNED`, `DELIVERING` |
| `RETURNING` | `RETURNED` |
| `DELIVERED` | `CLOSED`, `CLAIM_OPEN` |
| `CLOSED` | `CLAIM_OPEN` |
| `CLAIM_OPEN` | `CLAIM_RESOLVED` |

`RETURNED`, `CLAIM_RESOLVED` y `CANCELLED` son terminales inmediatos. `CLOSED` solo permite abrir reclamo cuando `occurred_at <= claim_window_ends_at` y `finalized_at` sigue nulo. `CLAIM_RESOLVED` asigna `finalized_at = occurred_at`. La primera entrada en `DELIVERED` crea una ventana configurable, por defecto de 72 horas. El job que finalizará un `CLOSED` al vencer esa ventana queda diferido a una tarea operativa posterior; ORD-002 no conecta Worker ni asigna `finalized_at` automáticamente.

Las pruebas recorren las 289 combinaciones del producto cartesiano, comparan exactamente las 30 aristas y ejecutan cada arista permitida contra PostgreSQL.

## Contrato HTTP

El request AI-05 contiene exactamente:

```json
{
  "target_status": "CANCELLED",
  "reason": "motivo sintético",
  "expected_version": 1,
  "metadata": {}
}
```

Se exige `Authorization`, tenant seleccionado por `X-Organization-Id` e `Idempotency-Key` de 16 a 128 caracteres. `reason` no puede ser nulo, vacío ni whitespace y admite hasta 500 caracteres. `expected_version` inicia en 1.

Metadata permite como máximo 4096 bytes UTF-8 y profundidad 2:

- `DRAFT -> CONFIRMED`: únicamente `restricted_goods_acknowledged` booleano;
- transición a `FAILED_ATTEMPT`: únicamente `incident_id` UUID `D`;
- cualquier otra transición: objeto vacío o nulo.

El response 200 usa el mismo `OrderResponse` de ORD-001, con versión incrementada. No expone reason, metadata, actor, payload interno ni PII. Request inválido, orden ausente o extranjera, estado, versión, guard, concurrencia o dependencia producen el mismo 409 público. Falta de identidad produce 401; tenant/capacidad/MFA producen 403.

## Autorización

La autorización usa membresía `ACTIVE` dentro del tenant seleccionado:

- `PLATFORM_ADMIN`: todas las aristas, con MFA obligatorio;
- `DISPATCHER`: todas las aristas;
- `DRIVER`: solo las 15 aristas operativas allowlisted y únicamente cuando existe una asignación `ACCEPTED` o `ACTIVE` de esa orden, actor y organización;
- cualquier otro rol: denegado.

La asignación DRIVER se verifica por identidad, organización, estado del perfil y owner/operator de la asignación. Una asignación extranjera o perteneciente a otro actor no amplía permisos.

## Guards

El registro evalúa en orden determinista y falla en el primer código no satisfecho:

- confirmación: quote consumida coherente, aceptación válida y revisión de mercancía restringida;
- asignación/reintento: una asignación activa, driver elegible, capacidad atestiguada y costo presente;
- pickup/delivery: prueba con hash y, si corresponde, COD `RECORDED` o `RECONCILED` por el importe exacto;
- failed attempt: `incident_id` allowlisted, incidencia exacta y custodia registrada;
- returning/retry: custodia adquirida;
- cancelación desde `AT_PICKUP`: custodia todavía no adquirida;
- cierre: ventana presente, ninguna incidencia `OPEN`/`INVESTIGATING`, integridad monetaria y conciliación; no-COD exige ausencia de transacción COD y COD exige `RECONCILED`;
- reclamo y resolución: ventana inclusiva, `finalized_at` nulo y reason no vacío.

Los readers ejecutan solo `SELECT`; no crean aceptación, asignación, prueba, incidencia ni transacción COD.

`valid_active_quote` se interpreta junto con ORD-001: la quote estaba `ACTIVE` cuando ORD-001 la consumió atómicamente, y ORD-002 exige encontrarla `USED`, con `consumed_at` y snapshots coherentes; nunca intenta devolverla a `ACTIVE`. `restricted_goods_acknowledged` es un booleano sintético y reversible para cerrar el guard de MVP-0, no una validación legal ni una integración de mercancías restringidas. Del mismo modo, la capacidad de una asignación se limita a la atestación conservadora disponible; no implementa un capacity engine.

## Lock order, concurrencia e idempotencia

Cada intento usa una conexión, una transacción, `SET LOCAL ROLE paqueteria_app` y contexto tenant reaplicado por retry:

1. advisory lock por tenant, scope e idempotency key;
2. replay/conflicto de `platform.idempotency_keys` y reserva;
3. `SELECT ... FOR UPDATE` de la orden visible;
4. versión esperada, matriz y autorización;
5. lecturas de guards en orden fijo;
6. `UPDATE orders.orders` condicionado por tenant, estado origen y versión;
7. un `ORDER_STATUS_CHANGED`, un outbox `orders.status-changed` y una auditoría AUD-001;
8. respuesta idempotente 200 y commit.

El scope exacto es `ORD-002:TRANSITION_ORDER`. El SHA-256 canónico incluye tenant, order ID, target exacto normalizado, reason exacto, versión esperada y metadata normalizada. Excluye actor, request ID, headers y tiempo de servidor.

Misma organización, key y hash puede reproducir el 200 almacenado sin volver a escribir; la autorización no forma parte del hash y siempre se vuelve a comprobar con el actor actual. La misma key con otro hash devuelve 409 antes de cualquier lectura de autorización. Keys distintas con la misma versión compiten bajo el lock de orden: solo una actualiza. El predicado optimista y la unique constraint `(order_id, aggregate_version)` son backstops.

### Replay completado y autorización vigente

`ORD-002-DEF-001` queda corregido mediante esta reautorización obligatoria.

Un replay completado no usa el estado actual de `orders.orders`, porque la orden puede haber avanzado legítimamente después de la transición original. Antes de autorizar, valida de forma fail-closed que existe exactamente un evento append-only con `order_id`, `owner_org_id`, `event_type = ORDER_STATUS_CHANGED` y `aggregate_version = expected_version + 1`; su `previous_status -> new_status` debe ser una arista canónica y `new_status` debe coincidir con el target solicitado. La respuesta guardada debe corresponder al mismo recurso, tenant, estado y versión.

Evidencia ausente, duplicada o incoherente devuelve el mismo 409 de conflicto idempotente. Con evidencia consistente, un reader estrecho obtiene el rol activo y la asignación DRIVER exacta vigentes, y `IOrderTransitionAuthorizer` evalúa la arista histórica original. `VIEWER`, `PLATFORM_ADMIN` sin MFA y `DRIVER` sin asignación activa exacta reciben 403; otro `DISPATCHER`, un admin con MFA o el DRIVER exacto pueden recibir el cuerpo 200 almacenado byte por byte.

Esta rama de replay no bloquea ni actualiza la orden, no reejecuta guards, no incrementa versión y no inserta evento, outbox o auditoría; tampoco modifica la fila idempotente completada.

## Evento, outbox, auditoría y privacidad

Una transición confirmada crea exactamente:

- evento `ORDER_STATUS_CHANGED` con `aggregate_version = new_version`;
- outbox topic `orders.status-changed`, aggregate `Order`, `PENDING`, attempts 0;
- auditoría append-only `ORDER_STATUS_CHANGED`;
- idempotencia completada con response 200.

Los IDs y timestamps se generan en aplicación y los INSERT no usan `RETURNING`. La política de evento público asigna únicamente `PICKUP_SCHEDULED`, `PICKED_UP`, `IN_TRANSIT`, `OUT_FOR_DELIVERY`, `DELIVERY_ATTEMPTED`, `RESCHEDULED`, `DELIVERED`, `RETURNING`, `RETURNED` y `CANCELLED`; el resto permanece privado.

`reason` participa sin cambios en el hash, pero solo se persiste después de la redacción central AUD-001. Correo, teléfono, dirección, tokens, secretos, private keys, connection strings y ciphertext se sustituyen. El request original no se guarda en idempotencia.

## Atomicidad y RLS

Cualquier error o cancelación revierte orden, evento, outbox, auditoría e idempotencia. Las pruebas inyectan fallos después de cada etapa material y verifican cero filas parciales.

La transacción usa `paqueteria_app`, RLS forzada y el owner tenant seleccionado; tanto el lock como el update optimista exigen `owner_org_id = organization_id`. Una orden inexistente y una extranjera son indistinguibles públicamente. Los guards ignoran filas de otro tenant, aun cuando referencien el mismo order ID. No se modifican AI-06, AI-18 ni el catálogo de migraciones; ORD-002 no necesita migración.

## Configuración

```json
{
  "Orders": {
    "Provider": "Disabled",
    "CommandTimeoutSeconds": 30,
    "PageSize": 50,
    "IdempotencyLifetimeMinutes": 1440,
    "PublicIdCollisionRetryCount": 3,
    "ClaimWindowHours": 72,
    "TransitionMetadataMaximumBytes": 4096
  }
}
```

`Disabled` continúa como default fail-closed. `PostgreSql` exige `ClaimWindowHours` entre 1 y 720 y metadata entre 256 y 16384 bytes. No se agregan paquetes ni dependencias.

## Validación

```powershell
python .\docs\normative\v0.6\tools\validate_contracts.py
dotnet tool restore
dotnet restore --locked-mode
dotnet format Paqueteria.sln --verify-no-changes --no-restore
dotnet build Paqueteria.sln --configuration Debug --no-restore
dotnet test Paqueteria.sln --configuration Debug --no-build
$env:CI = "true"
dotnet build Paqueteria.sln --configuration Release --no-restore
dotnet test Paqueteria.sln --configuration Release --no-build
Remove-Item Env:CI
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category=PostgreSqlContract"
dotnet ef migrations list --project .\src\Modules\Orders\Orders.Infrastructure\Orders.Infrastructure.csproj --startup-project .\src\Paqueteria.Api\Paqueteria.Api.csproj --context OrdersDbContext
.\tools\database-baseline.ps1 Verify
```

La matriz completa también ejecuta cada suite aislada, web, Compose, auditoría de paquetes, hashes normativos, `git diff --check` y diff vacío de `docs/normative/v0.6`. Las pruebas PostgreSQL usan Testcontainers efímero y no FND-002.

## Riesgos y rollback

Los gates normativos existentes siguen abiertos. Los guards dependen de datos sintéticos o de capacidades que pertenecen a tickets futuros; ORD-002 no convierte esos datos en integraciones productivas. El outbox queda pendiente hasta un Worker futuro. La ventana de reclamo es una convención reversible de MVP-0.

Rollback:

1. cambiar `Orders:Provider` a `Disabled`;
2. revertir los commits de ORD-002;
3. no ejecutar DDL inverso porque no existe migración nueva;
4. conservar órdenes, eventos, outbox, auditoría e idempotencia ya confirmados según retención.

No se deben eliminar eventos append-only, alterar AI-06/AI-18 ni usar rollback como borrado de datos.
