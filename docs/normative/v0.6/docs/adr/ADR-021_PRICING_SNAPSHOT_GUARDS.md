# ADR-021 — Tier y piso comercial congelados

**Estado:** Aprobado

`Pricing.Domain` determina `pricing_tier` y `minimum_total_cents_snapshot`. Cotización y orden los persisten. Los CHECK de base validan coherencia declarada; no sustituyen al motor. La regla es independiente de IVA y usa `total_cents`/snapshot.
