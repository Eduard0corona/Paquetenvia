# EXT-001: decisión del contrato de lectura

## Estado

Estado vigente (registro del 2026-09-26):

```text
EXT-001 normative_gap = RESOLVED (PR #44, fusionado en main el 2026-08-28)
EXT-001 implementation_started = true
EXT-001 implementation_merged = true (PR #45, fusionado en main el 2026-08-28)
EXT-001 merge_authorization = no consta en los cuerpos de #44/#45; pregunta abierta GOV-2026-09-MERGE-AUTHORIZATION
```

Estado histórico al abrir PR #44, conservado sin cambios:

```text
EXT-001 normative_gap = RESOLVED_PENDING_MERGE
EXT-001 implementation_started = false
EXT-001 merge_authorized = false
```

La revisión de readiness identificó que AI-05 sólo definía creación y
aceptación de ofertas. Un external driver no podía reconstruir sus ofertas
elegibles después de una carga inicial, refresh o reconnect sin depender de
eventos SignalR anteriores. Además, `ExternalOffer` no exponía `expires_at`,
aunque la expiración ya existía en creación, AI-06 y
`ExternalOfferChanged`.

## Decisión adoptada

AI-05 incorpora:

```text
GET /driver/me/external-offers
```

El endpoint:

- requiere autenticación y el `OrganizationContext` existente;
- deriva siempre el driver autenticado y no acepta `driver_id`;
- devuelve sólo ofertas del tenant activo que estén `OPEN`, no hayan expirado
  y sigan siendo elegibles al momento de la lectura;
- pagina mediante el cursor existente y responde con `items` y
  `next_cursor`, sin page-size configurable;
- usa `ExternalOffer`, ahora con `expires_at` requerido.

REST/PostgreSQL permanecen como autoridad. La carga inicial, refresh y
reconnect consultan este GET. `ExternalOfferChanged` sólo notifica al cliente
para que vuelva a leer el recurso.

## Dismiss local

“No me interesa” es una decisión local de UI no durable. No genera request al
servidor, no cambia `ExternalOffer.status`, no afecta a otros drivers y puede
reaparecer después de reload o reconnect.

No se agrega `REJECTED`, endpoint reject, tabla ni campo de rechazo. AI-06 no
cambia.

## Límite

Este cambio sólo resuelve el contrato normativo. No incluye implementación
funcional de EXT-001, runtime, UI, migraciones, SignalR, outbox ni
dependencias.
