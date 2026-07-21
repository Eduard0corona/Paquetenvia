# ADR-031 — Provisioning transaccional bajo RLS

**Estado:** Aprobado

Bootstrap es exclusivamente lectura. La aplicación genera UUIDs de usuario/organización y los incorpora al contexto RLS solo durante la transacción de provisioning.

El `identity_subject` coincide con el principal OIDC. Organización, membresía inicial y auditoría son atómicas; un rollback no deja registros parciales.
