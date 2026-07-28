# OPS-001: simulación de 20 entregas

OPS-001 agrega un runner de integración repetible para validar el recorrido
operativo existente. No agrega endpoints de simulación ni proveedores
productivos alternos. PostgreSQL/PostGIS sigue siendo la autoridad; S3,
outbox, SignalR, tracking y dashboard usan sus implementaciones reales.
No es un seed productivo y no contiene PII real.

## Recursos efímeros

La fixture levanta un contenedor PostgreSQL 18 con PostGIS y el baseline
verificado, más un contenedor Ceph RGW usado como S3-compatible. Kestrel aloja
la API y los hubs reales. Un contenedor DI separado usa la credencial y el rol
del Worker para validar/promover POD. Los dispatchers del outbox, conexiones
SignalR y clientes HTTP se crean y eliminan dentro de cada corrida.

## Alcance de una corrida

Cada corrida crea datos sintéticos aislados para:

- una organización propietaria y una organización señuelo;
- cuatro conductores `OWN` activos, cinco órdenes por conductor;
- 20 cotizaciones, 20 órdenes, 20 assignments y 20 tracking tokens;
- 20 pruebas `PICKUP_PHOTO` y 20 pruebas `DELIVERY_PHOTO`;
- la secuencia completa hasta `DELIVERED`, versión agregada 9.

No se crea el estado `CLOSED`. Todas las mutaciones de órdenes, asignaciones,
tokens y POD atraviesan los servicios o endpoints de producto. El seed se
limita a prerrequisitos sintéticos de tenant, ubicación, tarifa, identidad y
elegibilidad. Una segunda corrida usa identificadores e idempotency keys
distintos y reutiliza únicamente la identidad estable del dispatcher.

La creación y las transiciones tienen concurrencia máxima 4. POD usa dos
unidades de trabajo y cada iteración del procesador de validación usa
concurrencia interna 1, para no compartir un `DbContext` scoped entre objetos.
El procesador, el rol `paqueteria_worker`, el scanner, la promoción S3 y las
escrituras de auditoría son los reales.

## Recuperación y orden del outbox

El escenario fuerza tres casos:

1. Una publicación se interrumpe después de entregar a todas las audiencias y
   antes de `settle`. La fila queda `PROCESSING`, expira, se recupera con un
   lease nuevo y termina `PROCESSED` con dos intentos. SignalR observa la
   redelivery y el conteo se deduplica por `event_id`.
2. El lease anterior intenta completar la fila mientras el lease nuevo está
   vigente. `security.settle_outbox` devuelve `false`, no modifica el estado y
   el lease actual sí puede completar.
3. Un mensaje de topic desconocido anterior a un status legítimo termina
   `DEAD` con `UNKNOWN_TOPIC`; el status posterior termina `PROCESSED`.

El runner consume `orders.created` mediante `security.claim_outbox` y
`security.settle_outbox` después de detener el dispatcher realtime. Esto evita
enviar un topic que no pertenece al procesador SignalR y no actualiza estados
directamente.

## Criterios verificados

La aceptación exige, por corrida:

- 20 órdenes `DELIVERED`, 20 assignments y distribución 5/5/5/5;
- 20 pickup proofs y 20 delivery proofs finalizados;
- 180 eventos de dominio, sin huecos de versiones 1 a 9;
- 160 eventos realtime únicos y observación mínima de 98%;
- 340 auditorías requeridas y correlacionadas;
- 360 outbox procesados, un poison `DEAD` esperado y ningún outbox activo;
- tracking público final sin driver, assignment ni payload interno;
- dashboard con las 20 órdenes correctas y sin filas del tenant señuelo;
- stops del conductor vacíos al terminar;
- ausencia de `/api/v1/operations/simulation`;
- ausencia de token claro, URL firmada, object key, hash, payload, dirección o
  connection string en el reporte.

La prueba principal ejecuta dos corridas consecutivas contra los mismos
contenedores para detectar colisiones. La categoría también incluye pruebas
focales de aceptación, deduplicación, huecos de versión, redacción y cleanup
por cancelación.

## Ejecución local

Requisitos: Docker disponible y el SDK fijado por `global.json`.

```powershell
dotnet restore .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj --locked-mode
dotnet build .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj --configuration Release --no-restore
$env:OPS001_REPORT_PATH = Join-Path $PWD "TestResults\delivery-simulation\ops001-delivery-simulation.json"
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj --configuration Release --no-build --filter "Category=OpsDeliverySimulation" --logger "trx;LogFileName=delivery-simulation.trx" --results-directory .\TestResults\delivery-simulation
Remove-Item Env:OPS001_REPORT_PATH
```

No ejecute categorías Testcontainers en paralelo en una estación con recursos
limitados. La prueba tiene cancelación interna de 20 minutos; CI limita el job
completo a 30 minutos.

## Reporte seguro

La segunda corrida escribe `ops001-delivery-simulation.json`. El documento
contiene únicamente contadores, porcentajes, flags de recuperación y duración.
No contiene IDs de órdenes, conductores u organizaciones.

El reporte es evidencia de integración, no telemetría de producción. CI lo
publica junto con el TRX como artifact por 14 días. Los diagnósticos de fallo
muestran versión de Docker y estado de contenedores, sin volcar logs ni
variables de entorno.

## Diagnóstico y cleanup

Si falla una métrica, revise primero el stack del TRX y luego:

```powershell
docker ps --all --filter "label=org.testcontainers.lang=dotnet"
```

Testcontainers elimina PostgreSQL, Ceph y redes al terminar. El job CI ejecuta
además cleanup `always()`. No reutilice el reporte como entrada, no copie
tracking tokens desde el depurador y no publique URLs firmadas.

## Límites

- El escenario no prueba volumen, latencia sostenida ni operación multi-región.
- No reemplaza pruebas de carga ni valida capacidad de producción.
- SignalR es señal de actualización; REST/PostgreSQL conserva autoridad.
- La simulación no resuelve GATE-003, GATE-007, GATE-010, GATE-011, GATE-013,
  GATE-014, GATE-015, RTM-001-CUSTOMER-SUPPORT-ROLE ni Issue #5.
- No inicia OPS-002 ni REL-000.
- No constituye autorización de liberación para REL-000.
- No modifica `docs/normative/v0.6/`, contratos, DDL ni datos productivos.

## Rollback

Retire el job `delivery-simulation`, la guía y los tests OPS-001. No ejecute
DDL, no borre órdenes, proofs, assignments ni outbox, y no modifique el
baseline normativo.
