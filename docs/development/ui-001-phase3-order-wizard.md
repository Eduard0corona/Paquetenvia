# UI-001 fase 3: asistente de nueva orden en 4 pasos

Decisión: `UI-PHASE3-ORDER-WIZARD-2026-10-10`. Literales del owner: fase 3 aprobada el 2026-10-09 ("avanza con la
fase 3") y, a la pregunta del 2026-10-10 sobre cómo debe quedar la orden al terminar los 4 pasos, "Confirmada
(Recommended)" ("El asistente la crea y la confirma con las casillas del paso 4 (términos y artículos prohibidos).
Queda lista para preparar."). Es un cambio MAJOR de operación (AI-01 §7): la fila de `decision-log.md` es su registro
ADR. Contrato: AI-07 ruta `/ops/orders/new` y `screen_contracts.create_order.wizard`.

## Qué cambia

- `/ops/orders/new` deja de ser un formulario largo y pasa a un asistente de cuatro pasos con stepper (completado,
  actual, pendiente), "Atrás"/"Siguiente" y validación por paso. Los mensajes salen de las mismas reglas de
  `create-order.ts` (ahora con el campo al que pertenecen: `FieldError`) y se muestran junto a cada campo y en la lista
  de arriba; al rechazar un paso, el foco va al primer campo con mensaje y, al cambiar de paso, al título del paso.
- Antes la pantalla decía "Orden creada y confirmada." aunque la orden quedaba en `DRAFT` (`Order.Create`). Ahora la
  crea y la confirma de verdad, y solo dice "confirmada" cuando `transitionOrder` respondió `CONFIRMED`.
- La importación CSV y la API siguen creando órdenes en borrador. Sin cambios de API, backend, roles ni migraciones.

## Pasos

| Paso | Contenido | Se valida al pulsar "Siguiente" |
| --- | --- | --- |
| 1. Dónde | Origen y destino: dirección, contacto, teléfono, latitud/longitud (solo DISPATCHER y PLATFORM_ADMIN), referencias | Dirección ≥ 8, contacto, teléfono de 10 dígitos (+52 opcional), coordenadas, referencias ≤ 500 |
| 2. Qué se envía | 1 a 20 paquetes: descripción, peso (g), valor declarado (MXN), medidas opcionales (mm) | Límites de AI-05; un mensaje de medidas por paquete |
| 3. Servicio y precio | Tipo de servicio, ruta consolidada, ventana de entrega opcional, cobro contra entrega, ID de cuenta cliente, "Autorizar envío de bajo monto" y "Calcular precio" | Servicio, cuenta, motivo de la autorización, COD (centavos enteros, tope $20,000.00), ventana (hora de Mazatlán, ≤ 12 h, vigente) y un precio activo, vigente y que pase la guarda de 52 MXN |
| 4. Confirmar | Solo quién paga y las dos casillas (términos y aviso vigentes; sin artículos prohibidos) | Quién paga, ambas casillas y las versiones configuradas |

- El precio (`createQuote`) se calcula en el paso 3 y se descarta en cuanto cambia algo de lo que depende (direcciones,
  paquetes, servicio, ruta consolidada, cuenta, autorización); el COD, la ventana y quién paga no lo invalidan. Un
  precio vencido o usado se descarta al pulsar "Siguiente" o al confirmar, y se pide calcularlo de nuevo. Si llega una
  respuesta para datos que ya cambiaron, se descarta.
- "Autorizar envío de bajo monto" sigue en el paso 3 para DISPATCHER y PLATFORM_ADMIN, con motivo obligatorio y la
  misma regla de antes. Se deja visible siempre para esos roles (como hoy) porque una tarifa empresarial 200–499 o 500+
  sin ruta consolidada se rechaza antes de mostrar un precio y solo se cotiza autorizándola; cuando el total es de
  52 MXN o menos sin ruta consolidada ni autorización, el paso 3 lo señala y no deja avanzar.
- La versión de términos y aviso sale de la configuración (`PAQUETERIA_TERMS_VERSION`, `PAQUETERIA_PRIVACY_VERSION`)
  y no se muestra como código; sin ellas, "Crear y confirmar orden" queda deshabilitado con el aviso de siempre.

## Resumen fijo

A la derecha en pantallas anchas (≥ 1100 px, `position: sticky`) y apilado en teléfonos: después de los campos en los
pasos 1 a 3 y antes de ellos en el paso 4, para que el desglose se lea antes de confirmar (AI-07 "show price breakdown
before confirmation"). Repite lo capturado (sin identificadores) y, ya calculado, el desglose: neto sin IVA, IVA,
total "IVA incluido", la regla en palabras (tarifa en español y mínimo de referencia), los conceptos de la tarifa y la
vigencia del precio. Ya no se muestran el código del tier, la versión de política ni el conteo de reglas.

## Crear y confirmar

1. `createOrder` con el cuerpo de siempre (`restricted_goods_acknowledged: true`, COD en centavos, ventana opcional,
   aceptación `ASSISTED` con las versiones configuradas). Idempotency-Key por contenido; un reintento tras un fallo de
   red o servidor reenvía el mismo cuerpo (incluido `accepted_at`) con la misma llave: nunca hay una segunda orden.
2. `transitionOrder` DRAFT → CONFIRMED con el cliente de "Siguiente paso" (`createOrderActionsApi`): motivo fijo
   "Confirmada al crear la orden", `metadata.restricted_goods_acknowledged: true` (la guarda `restricted_goods_check`
   de AI-04, de la casilla del paso 4) y `expected_version` de la orden creada. Llave propia, reutilizada solo para
   reintentar la confirmación; reintentarla nunca vuelve a llamar a `createOrder`.

| Resultado | Pantalla |
| --- | --- |
| 200 `CONFIRMED` | "Orden creada y confirmada": número de guía, estado devuelto, servicio, ventana, montos y COD; "Abrir orden" y "Capturar otra orden" |
| 409 con código de ORD-002-GUARD-CODES | "Orden creada en borrador" con el mensaje del código y enlace al detalle ("Siguiente paso" la confirma) |
| 403 `MFA_REQUIRED` (PLATFORM_ADMIN sin MFA, avisado en el paso 4) | Igual, con "Verificar identidad" que regresa al detalle de la orden |
| Red o servidor | "Orden creada sin confirmación": no se sabe si quedó confirmada; "Reintentar confirmación" (misma llave) y enlace al detalle |
| Respuesta inválida | Igual, sin reintento |
| `createOrder` 409 | No hay orden: vuelve a "Servicio y precio" para calcular el precio de nuevo |

## Pendiente (no se simula)

- Libreta de direcciones del diseño aprobado: `createQuote` no acepta una ubicación guardada y `listLocations` no
  devuelve la dirección en texto; AI-08 la ubica en BUS-001 (MVP-2).
- Selector de cuenta cliente: no hay operación que liste cuentas; el ID se sigue capturando como antes.

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm run test && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
dotnet test tests/Paqueteria.ArchitectureTests --filter "FullyQualifiedName!~ModuleTemplateTests"
```

## Rollback

Revertir el commit. No hay datos, esquema ni API que deshacer; las órdenes ya confirmadas por el asistente siguen su
flujo normal y la pantalla vuelve al formulario anterior, que las deja en borrador.
