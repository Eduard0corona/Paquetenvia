# ADR-015 — Ingesta de ubicación del repartidor

**Estado:** Aprobado.

La PWA envía posiciones por REST con `client_event_id`. La API persiste y deduplica; el outbox publica `DriverLocationUpdated` por SignalR únicamente a operaciones autorizadas.
