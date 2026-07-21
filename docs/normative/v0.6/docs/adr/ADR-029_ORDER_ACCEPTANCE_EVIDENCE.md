# ADR-029 — Evidencia canónica de aceptación legal

**Estado:** Aprobado

`orders.order_acceptances` es RLS y append-only. Su evidencia usa `OrderAcceptanceCanonicalForm v1`: JSON compacto, claves fijas, UTF-8 sin BOM, UUID minúscula, timestamp UTC estricto y SHA-256.

Se inserta dentro del flujo transaccional existente `quote_snapshot_to_order`; no crea una sexta coordinación cross-module.
