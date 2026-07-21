# ADR-012 — FORCE RLS y separación de roles

**Estado:** Aprobado; refinado por ADR-016, ADR-017, ADR-019 y ADR-025.

API y Worker son roles `NOBYPASSRLS` sin propiedad de tablas. Todas las tablas tenant usan `ENABLE` y `FORCE ROW LEVEL SECURITY`. Las excepciones cross-tenant solo existen mediante funciones propiedad de roles `NOLOGIN BYPASSRLS` de privilegio mínimo.
