# ADR-013 — Membresías por organización

**Estado:** Aprobado.

`users` representa identidad global. Los roles y estados de autorización viven en `organization_memberships`; un usuario puede pertenecer a varias organizaciones y debe seleccionar un contexto activo autorizado.
