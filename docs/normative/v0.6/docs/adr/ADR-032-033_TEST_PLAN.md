# Plan SQL/Testcontainers obligatorio para ADR-032/ADR-033

## 1. Requisitos del entorno

- PostgreSQL/PostGIS de la versión objetivo.
- Migraciones v0.6 + delta v0.7.
- Roles reales: migrator, app, worker, bootstrap, inventory executor y roles de mantenimiento.
- Dos o más conexiones físicas; no simular concurrencia con una sola transacción.
- `FORCE ROW LEVEL SECURITY` activo.

## 2. Inventario inviolable

1. Dos importaciones concurrentes del mismo identificador canónico: una confirma.
2. Variantes case/espacios/Unicode inválidas no crean duplicados semánticos.
3. Tag HMAC/firma inválido no resuelve sello genuino.
4. Dos `CompletePickup` enlazan el mismo sello a órdenes distintas: exactamente uno confirma.
5. Dos sellos para el mismo `order_id+custody_leg_id`: uno rechazado.
6. Nueva delivery attempt no permite otro sello; nuevo custody leg sí.
7. Lifecycle terminal no puede volver a ASSIGNED.
8. UPDATE/DELETE directos sobre links/events/observations/handoffs fallan.
9. FKs compuestas rechazan batch/seal/leg/order de orgs incompatibles.
10. Tenant A no enumera ni usa inventario de B.
11. Owner sin operator capability no administra inventario de la operadora.
12. Tres asignaciones concurrentes con policy N=2 ocupan como máximo dos slots.
13. Cambio concurrente de policy no permite exceder versión bloqueada.
14. Rollback de handoff no deja slot ni lote ACTIVE.
15. Devolución libera slot solo tras aceptación.
16. Pickup atómico revierte link/leg/event/outbox si Proof no es definitivo.
17. Pickup STRICT con QR ilegible no acepta entrada manual.
18. Delivery manual exacta registra assurance degradado; serial distinto falla.
19. Abuso de manual entry activa bloqueo atómico bajo concurrencia.
20. Catálogo confirma owner, grants, NOBYPASS runtime y search_path fijo.

## 3. Guard compuesto

1. Código válido+sello válido: una sola entrega.
2. Código inválido+sello válido: intento++; no consume; no delivery.
3. Quinto intento concurrente: contador máximo respetado y challenge LOCKED una vez.
4. Challenge LOCKED/EXPIRED produce salida operacional definida.
5. Código válido+sello mismatch: `VALID_BUT_DELIVERY_BLOCKED`, revocado y security hold.
6. Código inválido+sello mismatch: ambos resultados persistidos; hold domina.
7. Excepción supervisada+sello válido: delivery solo policy alternate.
8. Excepción supervisada+sello inválido: siempre hold.
9. Dos `CompleteDelivery` concurrentes: una consumación y una versión ganadora.
10. Repetición misma Idempotency-Key devuelve resultado previo.
11. Versión obsoleta no consume código ni verifica sello.
12. Security hold confirma incidente, expected/observed y FAILED_ATTEMPT sin rollback.
13. Outbox `DriverSecurityHoldRequested` aparece una vez.
14. Handler post-commit bloquea nuevas asignaciones pero permite retornos activos.
15. `CLOSED` falla mientras incidente abierto.
16. Código/serial/tag crudos ausentes de logs, audit payload, outbox y Problem Details.

## 4. Customer access y live tracking

1. Usuario A no puede auto-concederse acceso a orden B.
2. Grant se crea únicamente con orden ocasional propia en `quote_snapshot_to_order`.
3. Revocación y regrant incrementan versión y generan eventos.
4. Usuario A/B bajo mismo owner_org quedan aislados.
5. Token histórico no obtiene ubicación live.
6. Token live expirado/revocado devuelve 404 uniforme y deja de recibir SignalR.
7. Punto encolado en DELIVERING se descarta si al consumir la orden está RETURNING/DELIVERED.
8. Proyección pública no contiene punto crudo, driver ID, speed, heading ni historial.
9. Bootstrap no tiene SELECT sobre driver_positions.

## 5. Ejecución y evidencia

Cada prueba debe capturar:

- SQLSTATE esperado;
- filas afectadas;
- estado final de todas las tablas participantes;
- grants/owners desde catálogos PostgreSQL;
- trazas redacted;
- repetición al menos 100 veces para pruebas de carrera críticas.
