# ADR-026 — Extensiones y hash simétrico de tracking

**Estado:** Aprobado

`pgcrypto` se instala en `extensions`; PostGIS permanece en `public`. Runtime tiene `USAGE` pero no `CREATE` sobre `public`.

El token se compone de 32 bytes aleatorios codificados Base64URL sin padding. C# y SQL calculan SHA-256 sobre los bytes UTF-8 exactos. SQL usa `extensions.digest(pg_catalog.convert_to(token,'UTF8'),'sha256')`. Un contract test compara vectores entre ambos runtimes.
