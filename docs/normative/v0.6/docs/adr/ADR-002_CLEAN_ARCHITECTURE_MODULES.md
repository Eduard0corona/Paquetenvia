# ADR-002 — Clean Architecture por módulo

**Estado:** Aprobado

## Decisión
Cada módulo separa Domain, Application, Infrastructure y Endpoints. Domain no depende de frameworks. Controllers, hubs y EF no contienen reglas de negocio.

## Consecuencias
Más proyectos y contratos explícitos, a cambio de pruebas y evolución controlada.
