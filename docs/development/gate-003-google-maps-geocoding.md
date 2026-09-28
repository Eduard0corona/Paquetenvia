# GATE-003-PROVIDER-GOOGLE: adaptador de geocodificación Google Maps Platform

Decisión: `GATE-003-PROVIDER-GOOGLE` (decision-log, 2026-09-27). Google Maps Platform es el
proveedor de geocodificación y ruteo; su API key vive en Key Vault. GATE-003 sigue **abierto**
hasta que el owner registre el tope de gasto, así que el piloto mantiene `Locations__GeocodingProvider=Manual`.

## Alcance

- Adaptador productivo `GoogleMapsGeocodingProvider` detrás del puerto existente
  `IGeocodingProvider` (AI-03 §15), en `Locations.Infrastructure/Geocoding/GoogleMaps/`.
  Usa `HttpClient` (IHttpClientFactory del framework compartido) y `System.Text.Json`, sin SDK ni
  paquetes NuGet nuevos.
- Selección por configuración: `Locations:GeocodingProvider=GoogleMaps`. `Manual`, `Mock` y
  `Disabled` no cambian; el mock determinista sigue siendo el de pruebas y CI.
- **No** se implementa `IRoutingProvider`/ETA: AI-03 §15 lo lista, pero no existe puerto en el
  código ni consumidor (Pricing es por zonas). Queda como pregunta al owner.

## Comportamiento

1. Valida el pin del cliente (obligatorio en AI-05 `CreateLocationRequest`) antes de cualquier
   llamada; un pin inválido sigue siendo 400.
2. Geocodifica `address_text` (`GET /maps/api/geocode/json`, `components=country:MX`,
   `region=mx`, ambos configurables).
3. Las coordenadas del proveedor reemplazan al pin solo con **un único** resultado, sin
   `partial_match`, de tipo `ROOFTOP` o `RANGE_INTERPOLATED` (`ProviderMode=GOOGLE_MAPS`).
4. Cualquier otro resultado degrada al comportamiento `work_allowed` de GATE-003: el pin manual,
   igual que `ManualGeocodingProvider` (`ProviderMode=MANUAL`, `UsedManualCoordinates=true`).
   Esto incluye sin coincidencia, ambiguo, impreciso, timeout, error HTTP/red, `OVER_QUERY_LIMIT`,
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

Sin coincidencia, ambiguo, impreciso o `INVALID_REQUEST` son respuestas de un proveedor sano y no
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
dotnet test tests/Paqueteria.ArchitectureTests
python3 tools/azr-001/env001_pilot_guards.py check --arm-dir <bicep build output>
```

Las pruebas usan un `HttpMessageHandler` falso; ninguna llama a la API real de Google.

## Preguntas abiertas para el owner

- Tope de gasto, cuotas diarias y alertas de facturación en Google Cloud (cierra GATE-003).
- Qué APIs habilitar y restringir en la key (este cambio solo usa la Geocoding API).
- Si el ruteo/ETA (`IRoutingProvider`) entra en el piloto, con qué consumidor y qué API
  (Routes API o Distance Matrix).
- Si las coordenadas de Google deben reemplazar el pin del cliente y con qué precisión mínima
  (hoy: un único resultado `ROOFTOP`/`RANGE_INTERPOLATED` sin `partial_match`), o si hace falta un
  umbral de distancia entre pin y resultado.
- Restricción geográfica: `country:MX` y `region=mx` por defecto.
- GATE-007: aviso de privacidad y transferencia de direcciones de clientes a Google como encargado.

## Rollback

`Locations__GeocodingProvider=Manual` (valor actual del piloto) desactiva el adaptador sin
redeploy de código. Revertir el PR quita el adaptador, el mapping, el permiso RBAC y el preflight;
no hay migraciones.
