# GATE-003-PROVIDER-GOOGLE: adaptador de geocodificación Google Maps Platform

Decisiones: `GATE-003-PROVIDER-GOOGLE` (decision-log, 2026-09-27). Google Maps Platform es el
proveedor de geocodificación y ruteo; su API key vive en Key Vault.
`GATE-003-MAPS-PILOT-RULES-2026-10-02` (decision-log, 2026-10-02): reglas del piloto, ver abajo.
GATE-003 sigue **abierto** hasta que el owner registre los pendientes de la sección "Pasos del
owner", así que el piloto mantiene `Locations__GeocodingProvider=Manual`.

## Reglas del piloto (GATE-003-MAPS-PILOT-RULES-2026-10-02)

Literales del owner: "Sí, las 4" y, sobre qué es una coincidencia exacta, "Solo ROOFTOP".

| # | Regla | Dónde se aplica | Prueba |
|---|---|---|---|
| 1 | API key restringida **solo** a la Geocoding API | Paso del owner en Google Cloud Console | — (fuera del software) |
| 2 | Sin rutas ni ETAs en el piloto | Solo se llama `GET maps/api/geocode/json`; no existe `IRoutingProvider` | `MapsPilotScopeArchitectureTests`; `Every_request_targets_only_the_geocoding_path_and_is_restricted_to_Mexico` |
| 3 | El pin del cliente se reemplaza solo con coincidencia exacta: un único resultado, `partial_match` distinto de `true` y `location_type == ROOFTOP` | `GoogleMapsGeocodingProvider.Parse` | `Only_a_non_partial_rooftop_match_moves_the_pin` |
| 4 | Búsquedas restringidas a México: siempre `components=country:MX`; el resultado debe traer un componente `country` con `short_name` `MX` | `BuildRequestUri` (constante `PilotCountry`), `IsInPilotCountry`, validación de opciones | `The_country_restriction_cannot_be_changed_or_emptied`; `A_rooftop_match_outside_Mexico_or_without_a_Mexican_country_component_keeps_the_pin` |

## Alcance

- Adaptador productivo `GoogleMapsGeocodingProvider` detrás del puerto existente
  `IGeocodingProvider` (AI-03 §15), en `Locations.Infrastructure/Geocoding/GoogleMaps/`.
  Usa `HttpClient` (IHttpClientFactory del framework compartido) y `System.Text.Json`, sin SDK ni
  paquetes NuGet nuevos.
- Selección por configuración: `Locations:GeocodingProvider=GoogleMaps`. `Manual`, `Mock` y
  `Disabled` no cambian; el mock determinista sigue siendo el de pruebas y CI.
- **No** se implementa `IRoutingProvider`/ETA: AI-03 §15 lo lista, pero el owner decidió que el
  piloto no tiene rutas ni ETAs (regla 2). Una prueba de arquitectura prohíbe en `src` los endpoints
  de Directions, Routes y Distance Matrix y cualquier puerto de ruteo/ETA de proveedor. El módulo
  interno `Routing` (rutas manuales en PostgreSQL, `IRouteService`) no es un proveedor y no cambia.

## Comportamiento

1. Valida el pin del cliente (obligatorio en AI-05 `CreateLocationRequest`) antes de cualquier
   llamada; un pin inválido sigue siendo 400.
2. Geocodifica `address_text` (`GET /maps/api/geocode/json`, siempre `components=country:MX`
   desde la constante `GoogleMapsGeocodingOptions.PilotCountry`; `region=mx` como sesgo, configurable).
   `Locations:GoogleMaps:ComponentsCountry` solo admite `MX`: cualquier otro valor, vacío incluido,
   falla la validación de opciones al arrancar (falla cerrado en lugar de ignorarse).
3. Las coordenadas del proveedor reemplazan al pin solo con coincidencia **exacta**: **un único**
   resultado, sin `partial_match`, `location_type` exactamente `ROOFTOP` y un componente de
   `address_components` con tipo `country` y `short_name` `MX` (`ProviderMode=GOOGLE_MAPS`).
   `RANGE_INTERPOLATED`, `GEOMETRIC_CENTER` y `APPROXIMATE` conservan el pin.
4. Cualquier otro resultado degrada al comportamiento `work_allowed` de GATE-003: el pin manual,
   igual que `ManualGeocodingProvider` (`ProviderMode=MANUAL`, `UsedManualCoordinates=true`).
   Esto incluye sin coincidencia, ambiguo, impreciso, fuera de México o sin país (`outside_country`),
   timeout, error HTTP/red, `OVER_QUERY_LIMIT`,
   `REQUEST_DENIED`, respuesta malformada o > 256 KiB, circuito abierto y bulkhead saturado.
   La creación de la ubicación nunca falla por el proveedor.
5. `address_summary` siempre es el resumen normalizado del cliente; `formatted_address` (dirección
   completa) nunca se usa ni se guarda.
6. La cancelación del llamador se propaga y no cuenta como falla del proveedor.

## Resiliencia (AI-03 §16)

| Mecanismo | Opción `Locations:GoogleMaps:*` | Default | Rango |
|---|---|---|---|
| Timeout por intento | `AttemptTimeoutMilliseconds` | 3000 | 200–10000 |
| Reintentos (solo transitorios: timeout, red, 5xx, 429, `OVER_QUERY_LIMIT`, `UNKNOWN_ERROR`; GET idempotente) | `MaxRetries` | 2 | 0–3 |
| Backoff exponencial + jitter ≤ 50 % | `RetryBaseDelayMilliseconds` | 200 | 0–5000 |
| Circuit breaker (fallas consecutivas; un probe half-open) | `CircuitBreakerFailureThreshold` / `CircuitBreakerBreakSeconds` | 5 / 30 | 1–50 / 1–600 |
| Bulkhead (llamadas concurrentes; excedente degrada al pin) | `MaxConcurrentRequests` | 8 | 1–64 |

Sin coincidencia, ambiguo, impreciso, fuera de México o `INVALID_REQUEST` son respuestas de un proveedor sano y no
abren el circuito. El cliente nombrado no tiene timeout global, redirecciones, cookies ni loggers
por defecto.

## Secretos y privacidad

- `Locations:GoogleMaps:ApiKey` solo llega por `KeyVaultSecrets:Mappings`
  (`google-maps-api-key`, escrita por el owner). En el piloto: mapping 3 de la API en
  `deploy/azure/pilot/apps.bicep`, *Key Vault Secrets User* sobre ese secreto (`apiSecretNames`),
  allowlist `REQUIRED_SECRET_MAPPINGS` del guard P06 y preflight del workflow que se detiene si falta.
  El guard P06 rechaza `*ApiKey*` como variable de entorno en claro.
- La validación de opciones nunca incluye el valor de la key en su mensaje.
- Logs: solo un código de resultado (`EventId` 4301 warning para proveedor no disponible, 4302
  information para pin conservado). Nunca la key, la dirección, la URI, el cuerpo ni
  `error_message`. `RemoveAllLoggers()` evita que IHttpClientFactory registre la URI (que contiene
  dirección y key); una prueba lo demuestra a través de la fábrica real.
- La dirección viaja a Google por HTTPS en el query string (la Geocoding API no acepta la key ni la
  dirección en el cuerpo). Enviar PII a un tercero es materia de GATE-007: ver preguntas abiertas.

## Validación

```bash
dotnet test tests/Paqueteria.UnitTests --filter "FullyQualifiedName~GoogleMaps"
dotnet test tests/Paqueteria.ArchitectureTests --filter "FullyQualifiedName~MapsPilotScope|FullyQualifiedName~LocationsArchitecture"
python3 tools/azr-001/env001_pilot_guards.py check --arm-dir <bicep build output>
```

Las pruebas usan un `HttpMessageHandler` falso; ninguna llama a la API real de Google.

## Pasos del owner (GATE-003 sigue abierto)

- **Restringir la key** `google-maps-api-key` en Google Cloud Console (*APIs & Services → Credentials*)
  a **solo** la Geocoding API (regla 1). El software no puede verificarlo; es un paso del owner.
- Pendientes que cierran GATE-003 (sin ellos el piloto sigue en `Manual`):
  - tope de gasto (budget) del proyecto de Google Cloud;
  - cuotas diarias de la Geocoding API;
  - alertas de facturación;
  - escribir la key restringida en Key Vault como `google-maps-api-key`
    (`docs/operations/env-001-pilot/README.md` §6.6).
- Abierta aparte: GATE-007, aviso de privacidad y transferencia de direcciones de clientes a Google
  como encargado.
- Resueltas por `GATE-003-MAPS-PILOT-RULES-2026-10-02`: APIs de la key (solo Geocoding), ruteo/ETA
  (no en el piloto), precisión mínima para reemplazar el pin (solo `ROOFTOP`) y restricción
  geográfica (`country:MX`, fija).

## Rollback

`Locations__GeocodingProvider=Manual` (valor actual del piloto) desactiva el adaptador sin
redeploy de código. Revertir el PR quita el adaptador, el mapping, el permiso RBAC y el preflight;
no hay migraciones.
