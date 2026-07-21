# ADR-020 — Evidencia mediante URL firmada y cuarentena

**Estado:** Aprobado

La API crea una sesión de carga; el cliente sube directamente a object storage. Un Worker valida hash, tamaño, tipo y seguridad, promueve el objeto y marca READY. La finalización JSON crea el Proof inmutable y la transición de custodia. Se prohíbe multipart en la API.
