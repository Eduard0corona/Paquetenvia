# Contrato operativo del agente de desarrollo

## 1. Mandato

Construir software verificable, seguro y trazable para una plataforma local de última milla. No ampliar cobertura, estados, precios, retenciones ni responsabilidades sin tarea y decisión aprobadas.

## 2. Reglas generales

1. Trabajar una tarea de backlog a la vez y respetar dependencias.
2. Leer contratos normativos antes de editar código.
3. No inventar reglas legales, financieras, de privacidad o aislamiento.
4. Implementar pruebas de contrato y seguridad antes o junto con el código.
5. Mantener separados `owner_org_id` y `operator_org_id`.
6. Aplicar autorización backend + RLS; la UI nunca es barrera suficiente.
7. Registrar cambios sensibles en auditoría append-only.
8. Usar transacciones, versión optimista e idempotencia donde corresponda.
9. No introducir secretos, PII en logs ni datos de tarjeta.
10. No desplegar producción ni habilitar dinero/PII real sin gates.

## 3. Restricción tecnológica

Backend: .NET 10, ASP.NET Core, EF Core/Npgsql, PostgreSQL/PostGIS, Worker Services y SignalR. Frontend: Next.js/React/TypeScript PWA. No reintroducir NestJS, BullMQ, microservicios, sharding, Kubernetes o segundo origen de verdad sin ADR.

## 4. Reglas obligatorias v0.6

1. Toda consulta tenant se ejecuta dentro de transacción explícita.
2. Aplicar `set_config(..., true)` después de `BEGIN`; `current_org_ids` usa parámetro `uuid[]`, `{}` vacío, nunca `NULL`.
3. API y Worker son `NOBYPASSRLS`; roles privilegiados son `NOLOGIN` y solo se acceden por `EXECUTE`.
4. Bootstrap jamás escribe; provisioning genera UUIDs y los preautoriza únicamente en la transacción.
5. `pgcrypto` se referencia como `extensions.*`; PostGIS permanece en `public`, sin `CREATE` runtime.
6. Hash de tracking: SHA-256 de bytes UTF-8 exactos del token Base64URL sin padding.
7. No existe acceso directo `SELECT/UPDATE/DELETE` a outbox desde runtime.
8. Productores de outbox proporcionan todos los valores, usan `ValueGeneratedNever` y no emiten `RETURNING`.
9. Claim/settle/requeue requieren `lease_token`; settle obsoleto debe fallar.
10. Purga solo puede borrar `PROCESSED`/`DEAD` antiguos mediante funciones de maintenance.
11. Tracking público nunca expone estado interno ni payload de evento; solo mapa AI-04 + `public_event_code`.
12. C# falla ruidosamente ante estado público no mapeado; SQL falla cerrado con 404 uniforme.
13. `order_acceptances`, `order_events`, `proofs` y `audit_logs` son append-only.
14. Canonicalización legal usa `OrderAcceptanceCanonicalForm v1`, no serialización automática ni dependencia JCS.
15. Todo dinero usa centavos `long/bigint/int64`; se prohíbe punto flotante.
16. La API no recibe bytes POD multipart; usa carga firmada.
17. GPS usa lote 1–20 y lane independiente con `UNIQUE(driver_position_id)` sin FK.
18. No añadir un sexto flujo cross-module sin ADR y actualización de pruebas.

## 5. Artefactos por tarea

Código, migraciones, pruebas exigidas, evidencia de ejecución, OpenAPI/tipos cuando aplique, riesgos residuales, rollback, comandos reproducibles y actualización de `decision-log.md` ante decisiones.

## 6. Definition of Done

- Criterios de aceptación y pruebas pasan.
- Migración aplica y revierte en BD real de prueba.
- Pruebas autorizadas/denegadas y aislamiento tenant pasan.
- No existen errores de lint, tipos o referencias.
- Dependencias nuevas están justificadas.
- Contratos y manifiesto permanecen sincronizados.

## 7. Ambigüedad

`BLOCKER`: seguridad, dinero, legal, privacidad, aislamiento o cobertura.  
`MAJOR`: cambio reversible de experiencia/operación; requiere ADR.  
`MINOR`: convención documentada.

## 8. Cierre

```text
Task: <ID>
Status: DONE | BLOCKED | PARTIAL
Files changed: ...
Migrations: ...
Tests executed: ...
Acceptance criteria: ...
Security/tenant checks: ...
Contract checks: ...
Open decisions: ...
Rollback: ...
```
