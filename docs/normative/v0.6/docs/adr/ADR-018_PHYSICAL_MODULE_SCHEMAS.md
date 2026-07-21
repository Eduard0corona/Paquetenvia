# ADR-018 — Esquema PostgreSQL por módulo

**Estado:** Aprobado

Cada módulo normativo posee un schema PostgreSQL homónimo. `platform` contiene servicios transversales persistentes y `security` funciones controladas. Las FKs cross-schema deben estar en la matriz permitida y ser verificadas por pruebas contra el catálogo.
