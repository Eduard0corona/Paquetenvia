# ADR-011 — Contrato cotización a orden

**Estado:** Aprobado.

La cotización persiste `origin_location_id`, `destination_location_id`, servicio, bandera de ruta consolidada y snapshots de solicitud/paquetes. La orden copia esos datos desde una cotización vigente en una transacción. `breakdown` es explicativo y no puede usarse como origen contractual de ubicaciones.
