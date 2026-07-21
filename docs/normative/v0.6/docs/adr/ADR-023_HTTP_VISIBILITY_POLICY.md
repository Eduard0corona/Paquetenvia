# ADR-023 — Política uniforme 401/403/404

**Estado:** Aprobado

401: autenticación inválida o ausente. 403: actor autenticado sin capacidad sobre un recurso visible de su tenant. 404: recurso ausente, cross-tenant o token público inválido/expirado/revocado. Las respuestas 404 no deben permitir enumeración.
