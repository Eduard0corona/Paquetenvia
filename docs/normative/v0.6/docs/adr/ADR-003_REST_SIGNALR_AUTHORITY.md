# ADR-003 — REST como autoridad y SignalR como distribución

**Estado:** Aprobado

## Decisión
Los comandos autoritativos entran por REST/casos de uso y se confirman en PostgreSQL. SignalR publica eventos posteriores al commit. Clientes reconectados resincronizan por REST.

## Consecuencias
Se toleran desconexiones y duplicados sin perder consistencia.
