# ADR-OBS-001: read model operativo aditivo

- Estado: aceptado para OBS-001
- Fecha: 2026-07-27
- Alcance: dashboard autenticado de despacho

## Contexto

`listOrders` pertenece al contrato congelado AI-05 y no contiene owner/operator
canónicos, cliente, zona de destino, assignment activo, última posición
persistida ni las alertas operativas requeridas por AI-07. Ampliarlo cambiaría
un contrato existente y mezclaría una consulta transaccional con un read model
de operaciones.

PostgreSQL ya conserva las fuentes autoritativas en Orders, Organizations,
Clients, Locations, Dispatch y Drivers. SignalR sólo comunica que puede existir
un cambio; no debe convertirse en almacenamiento ni autoridad.

## Decisión

Se crea el módulo `Reporting` con las cuatro capas canónicas y el endpoint
aditivo `GET /api/v1/operations/dashboard`
(`operationId: getOperationsDashboard`). El baseline AI-05 queda intacto.

El provider `PostgreSql` ejecuta una transacción con contexto RLS local y como
máximo dos comandos Npgsql:

1. establece `app.current_user_id`, `app.current_org_ids`, cambia a
   `paqueteria_app` y autoriza membership activa;
2. obtiene la página mediante una sola consulta CTE, assignment activo y
   posición persistida más reciente.

Sólo `DISPATCHER` y `PLATFORM_ADMIN` con MFA pueden leer. No se infiere ni se
agrega `CUSTOMER_SUPPORT`. Owner/operator usan nombres canónicos visibles bajo
RLS; una ausencia inconsistente falla cerrado. Cliente y zona pueden ser
`null`. Las ventanas permanecen `null` porque no existen como dato persistido.
La referencia de conductor es estable, acotada y no PII (`DRV-` más ocho
hexadecimales derivados del UUID); nunca sustituye un nombre personal.

El cursor opaco ordena por `updated_at DESC, order_id DESC`. El tamaño es fijo
en 50 y el servidor admite como máximo 100. No existe `page_size` controlado por
el cliente.

La web reemplaza su snapshot únicamente mediante REST. Los eventos validados de
OperationsHub se deduplican y disparan refresh con debounce de 250 ms; ubicación
usa 500 ms. Reconexión, polling y cambio de organización también vuelven a
leer REST. Todo estado vive en memoria de la pestaña.

Dashboard y detalle usan un coordinador single-flight común. Un refresh normal
puede agrupar señales y ejecutar como máximo una lectura adicional. El refresh
obligatorio de reconnect tiene prioridad y aplica la estrategia de serializar y
forzar una segunda lectura: espera la request previa, pero siempre inicia otra
lectura REST asociada a la sesión y organización vigentes.

El snapshot de versiones del dashboard se deriva de los items de esa respuesta
obligatoria exacta. El detalle completa conjuntamente orden y proyección
filtrada, exige un item y deriva las versiones de esa misma proyección. Sólo
después de validar y aplicar se reemplaza el guard y se informa `Conectada`.
Errores REST, red, timeout, contrato o cambio de sesión rechazan la
resincronización, informan `Sin conexión` y detienen el Hub.

La vista `Posiciones` normaliza puntos en un panel interno sin proveedor
cartográfico, tiles, geolocalización del navegador o requests externas.
GATE-003 permanece abierto.

## Consecuencias

- El endpoint es aditivo y todavía no forma parte de AI-05 normativo.
- No se agrega DDL, migración, tabla, vista, índice, producer ni paquete.
- La lectura combina módulos sin transferir autoridad de escritura a Reporting.
- La carrera reconnect/request previa y el fallo 503 se verifican sobre el
  pipeline real; las variantes del coordinador se cubren con deferreds.
- `Disabled` devuelve 503 genérico y degrada readiness.
- Las posiciones exactas sólo aparecen a operaciones autorizadas.
- La UI no asigna conductores ni implementa ofertas, rutas o incidencias.
- El despliegue sigue single-instance para realtime/rate limiting; Redis no
  participa y GATE-013 permanece abierto.

## Alternativas descartadas

- Ampliar `listOrders`: modificaría AI-05 y acoplaría consumidores existentes.
- Materialized view o tabla de reporting: requiere DDL y sincronización fuera de
  OBS-001.
- Construir estado desde SignalR: pierde eventos, viola autoridad REST y complica
  reconexión.
- Elegir un proveedor de mapas: anticipa GATE-003 y añade terceros no aprobados.

## Rollback

Configurar `OperationsDashboard:Provider=Disabled`, verificar 503 genérico,
retirar navegación a `/ops/dashboard`, desplegar la web previa e incrementar el
cache name del rollback. Después se pueden revertir los commits y retirar
Reporting de la solución. Se conservan Orders, Dispatch, Drivers, Locations,
Realtime, outbox, auditoría, órdenes, assignments y posiciones. No se ejecuta
DDL ni se borran datos o caches ajenos.
