# REL-000: nota documental sobre la evidencia aprobada

Registro del 2026-09-26. Esta nota no modifica ni reinterpreta la aprobación
`REL-000-OWNER-001` ni los artefactos aprobados de
`docs/releases/evidence/rel-000-owner-001/`, que siguen byte por byte iguales.
Solo deja por escrito lo que esos archivos dicen literalmente, para que nadie
los lea como algo que no dicen.

## 1. Estado literal de los JSON aprobados

Los cuatro JSON se capturaron del artifact de Foundation `8834236041`, generado
**antes** de la decisión del owner. Por eso dicen:

| Archivo | Campo | Valor literal |
| --- | --- | --- |
| `rel000-internal-release-report.json` | `mvp0_p0_items_verified` | `28` |
| `rel000-internal-release-report.json` | `mvp0_p0_items_blocked` | `1` |
| `rel000-internal-release-report.json` | `owner_approval_status` | `PENDING` |
| `rel000-internal-release-report.json` | `release_candidate_status` | `BLOCKED_BY_OWNER_DECISION` |
| `rel000-p0-evidence.json` | ítem `REL-000` `implementation_status` | `BLOCKED` |

El estado `29/29 VERIFIED` y `REL-000 = VERIFIED` no está escrito en esos
archivos. Lo produce el validador de `tools/rel-000/rel000.py` al aplicar el
decision record versionado (`docs/releases/mvp-0-owner-decision.json`) sobre
esa evidencia. `docs/releases/mvp-0-internal-release-report.md` ya lo dice
como "históricamente registra 28 `VERIFIED`".

## 2. Rollback: referencia verificada frente a ejecución verificada

`rel000-rollback-evidence.json` informa `rollback_items_verified = 29`. Ese
conteo corresponde a `rollback_reference_verified = true`: existe una
referencia de rollback documentada para cada ítem. En cambio,
`rollback_execution_verified` es `false` en 28 de los 29 ítems. Solo `REL-000`
tiene `true`, porque sus 14 escenarios de rollback se ejecutaron.

Es decir: salvo REL-000, los procedimientos de rollback están documentados,
pero su ejecución no quedó verificada en esta evidencia.

## 3. Ítems cuya evidencia no cubre todos sus criterios AI-08

La evidencia marca `VERIFIED` los dos ítems siguientes, pero la prueba citada
no cubre todos los criterios de aceptación que les asigna
`docs/normative/v0.6/specs/AI-08_BACKLOG.yaml`:

- **PRC-002 (Guardia para tarifas de volumen).** AI-08 exige, entre otros,
  "GATE-011 tests PLUS_VAT and VAT_INCLUDED" y la salida "financial override
  workflow". GATE-011 sigue abierto y la única prueba citada es
  `PricingPostgreSqlContractTests.Pricing_and_idempotency_RLS_are_forced_fail_closed_and_cross_tenant_safe`.
  El propio ítem declara la limitación "Tax display remains owner-blocked.".
- **TEN-003 (Provisioning transaccional).** AI-08 exige
  "identity_subject equals validated OIDC subject" y la prueba requerida
  "first login provisioning". No hay proveedor OIDC real integrado (la
  autenticación disponible es `Mock`) y la única prueba citada es el contrato
  SQL `RlsAndProvisioningContractTests.First_user_and_organization_provisioning_are_preauthorized_and_atomic`.

## 4. Alcance

- No cambia la aprobación, su alcance (`MVP-0_INTERNAL` con datos sintéticos)
  ni sus exclusiones.
- No reabre REL-000 ni modifica AI-08, AI-10, los fixtures ni el validador.
- Cualquier consecuencia sobre PRC-002, TEN-003 o la verificación de rollback
  requiere una decisión del project owner, que no consta.
