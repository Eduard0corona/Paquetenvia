# Paquetería Culiacán

Repositorio de arquitectura y contratos normativos para la plataforma local de paquetería.

## Estado actual

- Base normativa: v0.6
- Arquitectura: monolito modular en .NET 10
- Persistencia: PostgreSQL/PostGIS con RLS y FORCE RLS
- Mensajería: outbox transaccional con lease
- ADR-032 y ADR-033: aceptados como referencia de diseño para v0.7, todavía no implementados

Comienza por `AI-00_README_FIRST.md` y `CANONICAL_SOURCE_OF_TRUTH.md`.
