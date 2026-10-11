# Guía Sección A: preparar Azure y GitHub para el primer despliegue del piloto (ENV-001)

Para: el owner de Paquetenvia (desarrollador C#, sin experiencia previa en Azure).
Base: rama `origin/development` (2026-10-03). Fuentes principales:

- `docs/operations/env-001-pilot/bootstrap.md` (pasos únicos del owner)
- `docs/operations/env-001-pilot/README.md` (§4 secretos, §6.1 primer despliegue, §6.6 secretos del owner)
- `.github/workflows/deploy-azure-pilot.yml` (el workflow que despliega)
- `tools/azr-001/env001_pilot_guards.py` (validadores que detienen el deploy)
- `deploy/azure/pilot/*.bicep`, `apps.settings.json`, `observability.parameters.json`, `web.parameters.json`,
  `kv-firewall.sh`

Convenciones:
- `<ASI>` = valor que solo tú conoces. Nunca pegues un secreto real en un archivo, issue, PR ni chat.
- **Pregunta abierta** = algo que el repo no deja claro. No lo adivines; decídelo y regístralo.
- Los comandos `az` se corren en **Azure Cloud Shell (bash)** con una cuenta que sea **Owner de la
  suscripción** (bootstrap.md, introducción). Los comandos `gh` se corren en tu máquina con el GitHub CLI
  real (`gh auth login` primero).

> **Con fecha límite, 23 de octubre de 2026:** crea los recursos de correo de Azure Communication Services
> (sección 8). No depende del despliegue, y después de esa fecha Microsoft puede ya no dejar crearlos.

---

## Resumen: qué detiene el deploy y cuándo

| Bloqueo | Quién lo revisa | En qué momento | Paso de esta guía |
|---|---|---|---|
| `apps.settings.json` con `OWNER_DECISION_REQUIRED` | `env001_pilot_guards.py settings-check` (job `provenance`) | Antes de tocar Azure | 0.2 |
| `observability.parameters.json` con `OWNER_DECISION_REQUIRED` o correo inválido | `observability-check` (job `provenance` y Stage 5) | Antes de tocar Azure | 6 |
| `web.parameters.json` con `OWNER_DECISION_REQUIRED` o versión inválida (términos / aviso de privacidad) | `web-check` (job `provenance` y Stage 4) | Antes de tocar Azure | 6.1 |
| SHA no certificado 13/13 por Foundation CI en `main`, o workflow no lanzado desde `main` | `azr001_static_guards.py deploy-gate` | Antes de tocar Azure | 7 |
| Variables del environment faltantes o con formato inválido | Paso "Validate inputs and environment variables" (job `deploy`) | Antes del login en Azure | 3 |
| `PILOT_GATE_007_DECISION` / `PILOT_GATE_012_DECISION` sin una fila válida en `decision-log.md` | `env001_pilot_guards.py gate-decision` | Antes del login en Azure | 0.1 y 3 |
| Grupo de recursos inexistente o fuera de `mexicocentral` | Paso "Verify subscription, resource group and region" | Tras el login | 1 |
| Rol custom `Paquetenvia Pilot Blob Tag Reader` inexistente | Paso "Prepare platform parameters" | Tras Stage 1 | 2.4 |
| Faltan en Key Vault `authcenter-paquetenvia-client-secret`, `google-maps-api-key` o `public-tracking-link-key` | Paso de secretos de la ventana 2 | Tras Stage 2 (la bóveda ya existe) | 5 |

Consecuencia práctica: **el primer run siempre falla a propósito** al llegar a los secretos del owner,
porque la bóveda la crea ese mismo run (README §6.1 paso 6). Es lo esperado.

---

## 0. Requisitos previos que no son de Azure (bloquean igual)

### 0.1 Decisiones GATE-007 y GATE-012 en `decision-log.md`

El workflow exige que `PILOT_GATE_007_DECISION` y `PILOT_GATE_012_DECISION` sean, cada uno, el ID de
**exactamente una** fila de la tabla de `docs/normative/v0.6/decision-log.md` (en el SHA desplegado),
con ID que empiece por `GATE-007-` / `GATE-012-` y columna Type exactamente `Gate resolution` o
`Gate scoping` (README §1; `env001_pilot_guards.py`, `validate_gate_decision`).

- GATE-012: ya existe `GATE-012-PILOT-SCOPE` (Type `Gate scoping`, 2026-09-28). Úsala tal cual.
- GATE-007: **hoy no existe ninguna fila válida**. La única es `GATE-007-PRIVACY-DRAFT`
  (Type `Legal process decision`), que el validador rechaza explícitamente.

> **Pregunta abierta (bloqueante):** ¿cuál será la fila GATE-007 (aviso de privacidad aprobado por tu
> abogado, o un scoping explícito para el piloto) y con qué ID? Sin ella, el job `deploy` se detiene
> antes de iniciar sesión en Azure. Agregarla toca `docs/normative/**`: requiere PR con la decisión
> registrada y mantener `CHECKSUMS_SHA256.txt` y `MANIFEST.json` sincronizados
> (`python3 docs/normative/v0.6/tools/validate_contracts.py` → `VALIDATION_OK`), según `CLAUDE.md`.

### 0.2 Valores de negocio en `deploy/azure/pilot/apps.settings.json`

En `development` (commit `e52158f`) todavía hay dos `OWNER_DECISION_REQUIRED`:
`Dispatch__AssignmentPolicyVersion` y `Drivers__Eligibility__PolicyVersion`. **Esto ya está resuelto
en el PR #174**, que implementa tu decisión del 2 de octubre (versión por empresa, iniciando con
`piloto-2026-10-v1`): elimina ambos valores del archivo, las versiones viven en cada organización y el
guard rechaza esos nombres si reaparecen. No tienes que hacer nada aquí; queda resuelto al fusionar #174
y promoverlo a `main`.

> **Versiones de términos y aviso de privacidad (bloquea el deploy):** `PAQUETERIA_TERMS_VERSION` y
> `PAQUETERIA_PRIVACY_VERSION` del contenedor web ya están cableados en `apps.bicep`. Sus valores van en
> `deploy/azure/pilot/web.parameters.json`, que hoy tiene `OWNER_DECISION_REQUIRED` en ambos. El deploy se
> detiene hasta que los llenes (paso 6.1; README §4, "Accepted terms and privacy versions").

---

## 1. Grupo de recursos

Fuente: `bootstrap.md` §0–§2. El workflow fija la región `mexicocentral` (`env.PILOT_REGION`) y rechaza
cualquier otra.

```bash
# Cloud Shell (bash), como Owner de la suscripción
az account set --subscription "<NOMBRE_O_ID_DE_SUSCRIPCION>"
SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
TENANT_ID="$(az account show --query tenantId -o tsv)"
RG="rg-pv-pilot"
LOCATION="mexicocentral"
GITHUB_REPO="Eduard0corona/Paquetenvia"
GITHUB_ENVIRONMENT="azure-pilot"
RG_ID="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}"

# 1.a Registrar proveedores (una vez por suscripción; tarda unos minutos)
for ns in Microsoft.App Microsoft.ContainerRegistry Microsoft.DBforPostgreSQL Microsoft.KeyVault \
          Microsoft.Network Microsoft.OperationalInsights Microsoft.Storage Microsoft.Security \
          Microsoft.ManagedIdentity Microsoft.Consumption Microsoft.EventGrid Microsoft.Insights; do
  az provider register --namespace "$ns" --wait
done

# 1.b Crear el grupo de recursos
az group create --name "$RG" --location "$LOCATION" \
  --tags project=Paquetenvia environment=PILOT contract=ENV-001 dataClassification=REAL_PEOPLE
```

Guarda las variables de la sesión: los pasos siguientes las reutilizan. Si Cloud Shell se reinicia,
vuelve a ejecutar el bloque de variables.

---

## 2. App registration, service principal, credencial federada OIDC y roles

Fuente: `bootstrap.md` §3–§5. No se crea ningún client secret y nada de credenciales se guarda en
GitHub.

### 2.1 App registration y service principal

```bash
APP_ID="$(az ad app create --display-name "paquetenvia-pilot-deployer" --query appId -o tsv)"
az ad sp create --id "$APP_ID" --output none
SP_OBJECT_ID="$(az ad sp show --id "$APP_ID" --query id -o tsv)"
echo "APP_ID=$APP_ID"   # lo necesitarás en el paso 3 (no es secreto)
```

### 2.2 Credencial federada (OIDC)

El job `deploy` declara `environment: azure-pilot`, así que el token de GitHub lleva el subject de
**environment**, no de rama. El subject exacto es:

```
repo:Eduard0corona/Paquetenvia:environment:azure-pilot
```

No hace falta una credencial con subject de rama (`ref:refs/heads/main`): la restricción a `main` la
pone la regla de ramas del environment (paso 3) y el `deploy-gate` (`CONTROL_PLANE_REF = refs/heads/main`).
El job `provenance` no inicia sesión en Azure.

```bash
cat > /tmp/env001-federated.json <<EOF
{
  "name": "github-${GITHUB_ENVIRONMENT}",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:${GITHUB_REPO}:environment:${GITHUB_ENVIRONMENT}",
  "audiences": ["api://AzureADTokenExchange"],
  "description": "Paquetenvia ENV-001 pilot deployment workflow (GitHub Environment azure-pilot only)"
}
EOF
az ad app federated-credential create --id "$APP_ID" --parameters /tmp/env001-federated.json
rm /tmp/env001-federated.json
```

El subject distingue mayúsculas: debe coincidir con el dueño y el nombre del repo tal como los muestra
GitHub.

### 2.3 Rol custom de lectura de tags de blob (mínimo privilegio)

El Worker lee el veredicto de Defender for Storage (un index tag del blob). Ningún rol integrado da
`tags/read` sin `tags/write`, así que se crea uno, asignable solo dentro del grupo.

```bash
cat > /tmp/env001-tag-reader.json <<EOF
{
  "Name": "Paquetenvia Pilot Blob Tag Reader",
  "Description": "Read blob index tags (Defender for Storage malware verdict). ENV-001 / ADP-001.",
  "Actions": [],
  "NotActions": [],
  "DataActions": ["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read"],
  "NotDataActions": [],
  "AssignableScopes": ["${RG_ID}"]
}
EOF
az role definition create --role-definition /tmp/env001-tag-reader.json --output none
rm /tmp/env001-tag-reader.json
TAG_READER_ID="$(az role definition list --custom-role-only true \
  --name "Paquetenvia Pilot Blob Tag Reader" --scope "$RG_ID" --query "[0].name" -o tsv)"
echo "$TAG_READER_ID"   # debe imprimir un GUID; si sale vacío, espera 1-2 minutos y repite
```

El nombre debe ser exactamente `Paquetenvia Pilot Blob Tag Reader`: el workflow lo busca así
(`env.BLOB_TAG_READER_ROLE`).

### 2.4 Roles del deployer (solo a nivel del grupo de recursos)

- **Contributor** sobre `rg-pv-pilot`.
- **Role Based Access Control Administrator** sobre `rg-pv-pilot`, con una condición ABAC que solo le
  deja asignar/quitar 7 roles: AcrPull, Storage Blob Data Contributor, Storage Blob Delegator,
  Key Vault Secrets User, Key Vault Secrets Officer, Key Vault Crypto Service Encryption User y el
  rol custom. Así nunca puede conceder Owner, Contributor, User Access Administrator ni
  Storage Blob Data Owner.

```bash
az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Contributor" --scope "$RG_ID" --output none

ALLOWED="7f951dda-4ed3-4680-a7ca-43fe172d538d, ba92f5b4-2d11-453d-a403-e96b0029c9fe, db58b8e5-c6ad-4a2a-8342-4190687cbf4a, 4633458b-17de-408a-b874-0445c86b69e6, b86a8fe4-44ce-4948-aee5-eccb2c155cd7, e147488a-f6f5-4113-8e2d-b22465e65bf6, ${TAG_READER_ID}"
CONDITION="((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ALLOWED}})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ALLOWED}}))"
az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope "$RG_ID" \
  --condition "$CONDITION" --condition-version "2.0" --output none
```

No asignes nada a nivel de suscripción. *Key Vault Secrets Officer* sobre la bóveda se lo dan las
propias plantillas (`security.bicep`, vía `deployer()`); el deployer nunca recibe un rol cripto.

### 2.5 Verificación del bootstrap (bootstrap.md §7)

```bash
az group show -n "$RG" --query location -o tsv                      # mexicocentral
az ad app federated-credential list --id "$APP_ID" --query "[].subject" -o tsv
# repo:Eduard0corona/Paquetenvia:environment:azure-pilot
az role assignment list --assignee "$SP_OBJECT_ID" --all \
  --query "[].{role:roleDefinitionName,scope:scope,condition:condition!=null}" -o table
# Dos filas, ambas con scope .../resourceGroups/rg-pv-pilot; la de RBAC Administrator con condition=True
```

---

## 3. GitHub Environment `azure-pilot`

Fuente: `bootstrap.md` §6 y el bloque `env:` del job `deploy` en `deploy-azure-pilot.yml`.

**No hay ningún GitHub secret.** El workflow solo lee **variables del environment** (`vars.*`), y
ninguna es sensible.

### 3.1 Crear el environment y sus reglas (interfaz web, recomendado)

GitHub → repo `Eduard0corona/Paquetenvia` → *Settings → Environments → New environment* →
nombre exacto `azure-pilot`. Luego:

- **Required reviewers:** tú (el owner). Cada run se pausa hasta que lo apruebes.
- **Deployment branches and tags:** *Selected branches and tags* → agregar regla de rama `main`.

> **Pregunta abierta:** el repo no dice si marcar *Prevent self-review*. Si eres el único revisor y lo
> marcas, no podrás aprobar tus propios runs. Lo seguro con un solo owner es dejarlo desmarcado.

### 3.2 Variables del environment (no "repository variables")

En la misma pantalla del environment → *Environment variables → Add variable*:

| Variable | Obligatoria | Valor | Validación en el workflow |
|---|---|---|---|
| `AZURE_CLIENT_ID` | Sí | `$APP_ID` del paso 2.1 | usada por `azure/login` |
| `AZURE_TENANT_ID` | Sí | `$TENANT_ID` del paso 1 | usada por `azure/login` |
| `AZURE_SUBSCRIPTION_ID` | Sí | `$SUBSCRIPTION_ID` del paso 1 | `^[0-9a-f-]{36}$` |
| `PILOT_RESOURCE_GROUP` | Sí | `rg-pv-pilot` | `^[A-Za-z0-9._-]{1,90}$` y región `mexicocentral` |
| `PILOT_BUDGET_START_DATE` | Sí | Primer día del mes actual o siguiente, p. ej. `2026-10-01`. **No lo cambies después.** | `^20YY-MM-01$` |
| `PILOT_BUDGET_EMAILS` | No | Correos extra separados por coma, sin espacios (`<CORREO1>,<CORREO2>`). Los Owners del grupo siempre reciben el aviso. | `^[A-Za-z0-9._%+@,-]+$` |
| `PILOT_TRACKING_SUPPORT_URL` | No | URL `https://` de soporte en el tracking público (por defecto `https://paquetenvia.com`) | `^https://[A-Za-z0-9./_-]+$` |
| `PILOT_GATE_012_DECISION` | Sí | `GATE-012-PILOT-SCOPE` | fila válida en `decision-log.md` |
| `PILOT_GATE_007_DECISION` | Sí | `<ID_FILA_GATE-007>` (ver 0.1; hoy no existe) | fila válida en `decision-log.md`; `GATE-007-PRIVACY-DRAFT` se rechaza |

Nota: `PILOT_TRACKING_SUPPORT_URL` se compila dentro de la imagen web como
`NEXT_PUBLIC_TRACKING_SUPPORT_URL` (build-arg del workflow). Cambiarla requiere un nuevo run.

### 3.3 Alternativa con GitHub CLI (bootstrap.md §6)

```bash
GITHUB_REPO="Eduard0corona/Paquetenvia"; GITHUB_ENVIRONMENT="azure-pilot"
MY_ID="$(gh api users/<TU_USUARIO_GITHUB> --jq .id)"
# Crear el environment con revisor y política de ramas en UNA sola llamada
gh api --method PUT "repos/${GITHUB_REPO}/environments/${GITHUB_ENVIRONMENT}" --input - <<EOF
{"reviewers":[{"type":"User","id":${MY_ID}}],
 "deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}
EOF
gh api --method POST "repos/${GITHUB_REPO}/environments/${GITHUB_ENVIRONMENT}/deployment-branch-policies" \
  -f name=main -f type=branch
for pair in "AZURE_CLIENT_ID=<APP_ID>" "AZURE_TENANT_ID=<TENANT_ID>" "AZURE_SUBSCRIPTION_ID=<SUBSCRIPTION_ID>" \
            "PILOT_RESOURCE_GROUP=rg-pv-pilot" "PILOT_BUDGET_START_DATE=2026-10-01" \
            "PILOT_GATE_012_DECISION=GATE-012-PILOT-SCOPE"; do
  gh variable set "${pair%%=*}" --env "$GITHUB_ENVIRONMENT" --repo "$GITHUB_REPO" --body "${pair#*=}"
done
# Cuando exista la fila GATE-007:
# gh variable set PILOT_GATE_007_DECISION --env azure-pilot --repo "$GITHUB_REPO" --body "<ID_FILA_GATE-007>"
```

Nota: `bootstrap.md` §6 hace dos `PUT` separados (política de ramas y, aparte, revisores). Un `PUT`
sobre el environment puede reemplazar la configuración que no envías, así que aquí van juntos en uno.
Después revisa en la interfaz web que queden **ambas** reglas.

---

## 4. DNS de `paquetenvia.com`

Fuente: README §2 y §6.1 pasos 7–8; paso "Summary and DNS records for the owner" del workflow.

**Cuándo:** después del primer run que complete Stage 2 (`platform.bicep`). El resumen del run
(pestaña *Summary*) muestra la tabla con los valores reales. Ese resumen se escribe con `if: always()`,
así que aparece incluso en el run que falla por los secretos del paso 5. **No crees nada antes:** los
valores no existen hasta entonces.

Registros, en tu proveedor DNS de `paquetenvia.com`:

| Tipo | Host | Valor | Origen |
|---|---|---|---|
| `A` | `@` (apex) | `<IP_ESTATICA_DEL_ENTORNO>` | output `containerEnvironmentStaticIp` de `platform.bicep` |
| `TXT` | `asuid` (es decir, `asuid.paquetenvia.com`) | `<CUSTOM_DOMAIN_VERIFICATION_ID>` | output `customDomainVerificationId` de `platform.bicep` |

- Elimina cualquier otro `A`, `AAAA` o `CNAME` en el apex (README §6.1 paso 7).
- Comprueba la propagación:
  ```bash
  dig +short paquetenvia.com
  dig +short TXT asuid.paquetenvia.com
  ```
- Solo entonces lanza el workflow con `custom_domain_phase=bind` (paso 7.4). El certificado administrado
  se emite por validación HTTP (README §3).

> **Pregunta abierta:** el repo no menciona registros `CAA`. Si `paquetenvia.com` ya tiene `CAA`, el
> certificado administrado de Azure podría fallar si la CA emisora no está permitida. Revísalo en tu DNS
> antes del `bind`.

> No van en esta sección: los registros de correo de Azure Communication Services (TXT de dominio, SPF,
> DKIM x2). README §4 los deja fuera de las plantillas del piloto.

---

## 5. Secretos de Key Vault que escribe el owner

Fuente: README §4 (tabla de secretos), §6.6 y `bootstrap.md` §5 ("Owner access to the vault").

El workflow genera solo `pg-admin-password`, `pg-*-connection`, `pg-*-login-verifier` y
`paquetenvia-email-lookup-key-1`. **Los tres siguientes los escribes tú**, y el workflow se detiene
(`::error::Key Vault <kv> lacks ...`) mientras falte **cualquiera** de ellos, en este orden:

| Secreto | Valor | Lo lee |
|---|---|---|
| `authcenter-paquetenvia-client-secret` | El client secret que AuthCenter te entrega una sola vez para el cliente `paquetenvia-web-prod` | API (`AuthCenter:ClientSecret`) |
| `google-maps-api-key` | API key de Google Maps Platform, restringida (5.2) | API (`Locations:GoogleMaps:ApiKey`) |
| `public-tracking-link-key` | 32 bytes aleatorios en base64, generados en el momento | API (`PublicTracking:LinkKeys:1`) |

Los tres deben existir **antes del segundo run**. La API lee `google-maps-api-key` al arrancar aunque
`Locations__GeocodingProvider=Manual`, y un secreto mapeado que falte detiene el host (README §4 y §6.6).

> Nota: el workflow exige los tres (README §6.1 paso 6) y solo reporta el primero que falta. Escríbelos
> todos de una vez para no gastar un run por cada uno.

### 5.1 Obtener los valores (fuera de Azure)

- **AuthCenter:** pide el client secret del cliente `paquetenvia-web-prod` (auth-001 §10.1,
  `docs/development/auth-001-authcenter-bff.md`). Las URIs del cliente se registran **exactas, sin `/`
  final**:
  - Redirect URI: `https://paquetenvia.com/signin-authcenter`
  - Post-logout redirect URI: `https://paquetenvia.com/login`
  - Back-channel logout URI: `https://paquetenvia.com/auth/backchannel-logout`

  > **Pregunta abierta:** README §6.1 pone el registro de URIs (paso 10) *después* de la prueba manual
  > de login (paso 9), y el login no puede funcionar sin ellas. Lo razonable es registrarlas antes del
  > paso 9, o al pedir el secreto. Confírmalo con AuthCenter.

- **Google Maps (5.2).**
- **Tracking:** se genera dentro del propio comando de 5.4; no hace falta obtenerlo antes.

### 5.2 Google Cloud: key restringida, cuotas y presupuesto

Fuente: README §6.6 y `docs/development/gate-003-google-maps-geocoding.md` ("Preguntas abiertas para el
owner"). GATE-003 sigue **abierto** hasta que registres el tope de gasto en `decision-log.md`. El piloto
despliega con `Locations__GeocodingProvider=Manual`, así que la key se lee al arrancar pero no se usa
todavía.

Consola (https://console.cloud.google.com), con el proyecto `<PROYECTO_GCP>` vinculado a tu cuenta de
facturación:

1. *APIs & Services → Library* → habilita **Geocoding API** (solo esa).
2. *APIs & Services → Credentials → Create credentials → API key*.
3. Edita la key:
   - **API restrictions → Restrict key → solo "Geocoding API"**.
   - **Application restrictions:** ver la pregunta abierta de abajo.
4. *Google Maps Platform → Quotas* (o *IAM & Admin → Quotas*) → Geocoding API → limita
   "Requests per day" a `<CUOTA_DIARIA>`.
5. *Billing → Budgets & alerts → Create budget* → alcance: el proyecto; monto `<TOPE_MENSUAL_USD>`;
   umbrales, p. ej. 50 %, 90 % y 100 %; avisos por correo. Un presupuesto **avisa, no corta** el
   gasto: el límite real es la cuota diaria del punto 4.

Equivalente con `gcloud` (opcional):

```bash
gcloud config set project <PROYECTO_GCP>
gcloud services enable geocoding-backend.googleapis.com
gcloud services api-keys create --display-name="paquetenvia-pilot-geocoding" \
  --api-target=service=geocoding-backend.googleapis.com
# El presupuesto y la cuota se configuran mejor en la consola (pasos 4 y 5).
```

> **Pregunta abierta (GATE-003):** los valores de cuota diaria, tope mensual y umbrales de alerta. El repo
> los deja como decisión tuya. Cuando los fijes, regístralos en `decision-log.md` para cerrar GATE-003.

> **Pregunta abierta:** restricción de aplicación de la key. El repo solo pide restringirla a la
> Geocoding API. Las llamadas salen de Container Apps (Consumption), y el repo no documenta una IP de
> salida fija (la IP estática del paso 4 es de **entrada**). Una restricción por IP podría romper el
> geocoding. Decide si dejas solo la restricción por API más la cuota.

> **Pregunta abierta (GATE-007):** enviar direcciones de clientes a Google es una transferencia de PII a
> un encargado, y depende del aviso de privacidad (gate-003 doc).

### 5.3 Acceso temporal del owner a la bóveda

La bóveda (`kv-pvp-<sufijo>`) usa RBAC y su firewall niega el tráfico público (`defaultAction: Deny`,
`bypass: None`). Para escribir necesitas dos cosas temporales: un rol de datos y una regla de firewall
para tu IP (bootstrap.md §5 "Owner access to the vault"). Hazlo después del primer run, porque la bóveda
la crea Stage 1.

```bash
# Cloud Shell, mismas variables del paso 1
KV="$(az deployment group show -g "$RG" -n env001-security --query properties.outputs.vaultName.value -o tsv)"
echo "$KV"
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Key Vault Secrets Officer" --scope "$(az keyvault show -n "$KV" --query id -o tsv)"
# La propagación de RBAC puede tardar unos minutos.
```

El script `deploy/azure/pilot/kv-firewall.sh` tiene que estar en Cloud Shell. Si puedes clonar el repo:

```bash
git clone https://github.com/Eduard0corona/Paquetenvia.git && cd Paquetenvia
```

Si no (repo privado sin credenciales en Cloud Shell), el equivalente manual de `open`/`close` es:

```bash
MYIP="$(curl -fsS https://api.ipify.org)"
az keyvault network-rule add    --name "$KV" --ip-address "${MYIP}/32" --output none   # open
# ... escribir secretos (5.4) ...
az keyvault network-rule remove --name "$KV" --ip-address "${MYIP}/32" --output none   # close
```

### 5.4 Escribir los tres secretos (README §6.6, literal)

Cada valor se pega cuando el comando lo pide: no queda en el historial, se escribe a un archivo
temporal y se borra con `shred`.

```bash
bash deploy/azure/pilot/kv-firewall.sh open "$KV"

# 1) AuthCenter client secret (pega el valor y pulsa Enter; no se muestra)
read -rs AUTHCENTER_SECRET && printf '%s' "$AUTHCENTER_SECRET" > /tmp/ac && \
az keyvault secret set --vault-name "$KV" --name authcenter-paquetenvia-client-secret --file /tmp/ac --encoding utf-8 --output none; \
shred -u /tmp/ac; unset AUTHCENTER_SECRET

# 2) Google Maps API key
read -rs GOOGLE_MAPS_KEY && printf '%s' "$GOOGLE_MAPS_KEY" > /tmp/gm && \
az keyvault secret set --vault-name "$KV" --name google-maps-api-key --file /tmp/gm --encoding utf-8 --output none; \
shred -u /tmp/gm; unset GOOGLE_MAPS_KEY

# 3) public-tracking-link-key: 32 bytes aleatorios, base64, generados aquí y nunca mostrados
umask 077 && openssl rand -base64 32 | tr -d '\n' > /tmp/tl && \
az keyvault secret set --vault-name "$KV" --name public-tracking-link-key --file /tmp/tl --encoding utf-8 --output none; \
shred -u /tmp/tl

# Comprobar que existen (solo nombres, nunca valores)
az keyvault secret list --vault-name "$KV" --query "[].name" -o tsv | sort

bash deploy/azure/pilot/kv-firewall.sh close "$KV"
```

Avisos importantes:
- `public-tracking-link-key` **se escribe una vez y no se reemplaza**. Cambiar su valor bajo la misma
  versión hace que la API se niegue a mostrar las ligas existentes (falla cerrado). Para rotarla se usa
  un secreto nuevo (`LinkKeys:2`) mediante un cambio de plantilla revisado (README §6.6).
- **Nunca borres** secretos de la bóveda: tiene purge protection y el nombre queda bloqueado 90 días.
- Si no quieres acceso permanente, quita tu rol al terminar:
  ```bash
  az role assignment delete --assignee "$(az ad signed-in-user show --query id -o tsv)" \
    --role "Key Vault Secrets Officer" --scope "$(az keyvault show -n "$KV" --query id -o tsv)"
  ```
- Cada redeploy de `security.bicep` vacía `ipRules`. Una regla olvidada no sobrevive al siguiente run,
  pero ciérrala igual.

---

## 6. Correo de alertas en `deploy/azure/pilot/observability.parameters.json`

Fuente: README §6.1 paso 3 y §10; `env001_pilot_guards.py` (`validate_observability_parameters`).

Hoy el archivo tiene `"alertEmailAddress": { "value": "OWNER_DECISION_REQUIRED" }`. Cámbialo por **una
sola** dirección:

```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "alertEmailAddress": {
      "value": "<CORREO_DE_ALERTAS>"
    }
  }
}
```

Reglas del validador:
- Un solo correo (regex `^[A-Za-z0-9._%+-]{1,64}@dominio.tld$`); no se admiten listas.
- Solo se permiten estos parámetros: `alertEmailAddress` y, opcionalmente, los umbrales
  `outboxLagThresholdSeconds`, `http5xxMinimumCount`, `http5xxPercentThreshold` y
  `readinessEventThreshold` (enteros, con la forma `{"value": <int>}`). Cualquier otra clave falla.

Cómo: rama de tarea → PR **en borrador** a `development` → PR de promoción `development` → `main`
(`CLAUDE.md`, `docs/development/branching-and-ci.md`). Compruébalo
localmente:

```bash
python3 tools/azr-001/env001_pilot_guards.py observability-check --file deploy/azure/pilot/observability.parameters.json
```

> Nota: el correo queda en un archivo versionado del repo. Usa un buzón operativo, no uno personal,
> si eso te preocupa.

### 6.1 Versiones de términos y aviso de privacidad en `deploy/azure/pilot/web.parameters.json`

Fuente: README §4 ("Accepted terms and privacy versions") y §6.1 paso 3; `env001_pilot_guards.py`
(`validate_web_parameters`, guard P22).

La pantalla "Nuevo pedido" (`/ops/orders/new`) registra, en cada orden, qué versión de los términos y del
aviso de privacidad aceptó el cliente. Lee dos variables del contenedor web: `PAQUETERIA_TERMS_VERSION` y
`PAQUETERIA_PRIVACY_VERSION`. Si alguna falta o es inválida, el botón de confirmar queda deshabilitado.

> **Importante:** estos valores **salen del aviso de privacidad aprobado** (GATE-007, paso 0.1) y de los
> términos vigentes. No los inventes ni pongas una fecha provisional: cada orden guarda la versión como
> evidencia de lo que el cliente aceptó. Defínelos cuando tengas el aviso aprobado por tu abogado.

Hoy el archivo tiene `OWNER_DECISION_REQUIRED` en ambos parámetros. Cámbialos por las versiones reales:

```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "webTermsVersion": {
      "value": "<VERSION_TERMINOS>"
    },
    "webPrivacyVersion": {
      "value": "<VERSION_AVISO_PRIVACIDAD>"
    }
  }
}
```

Reglas del validador:
- Formato AI-05 `^[A-Za-z0-9._-]{1,64}$`: solo letras sin acento, dígitos, `.`, `_` y `-`; de 1 a 64
  caracteres. Ejemplo: `2026-10-01`. Sin espacios, `/`, `:` ni acentos.
- Nunca vacío. No hay valor por defecto.
- Solo se permiten esos dos parámetros; cualquier otra clave falla.
- Mientras alguno siga en `OWNER_DECISION_REQUIRED`, el job `provenance` se detiene antes de tocar Azure con
  `STOP_FOR_OWNER_DECISION: webTermsVersion (PAQUETERIA_TERMS_VERSION) ...` (o `webPrivacyVersion`).

Cómo: igual que el paso 6 (rama de tarea → PR en borrador a `development` → promoción a `main`).
Compruébalo localmente:

```bash
python3 tools/azr-001/env001_pilot_guards.py web-check --file deploy/azure/pilot/web.parameters.json
```

Para cambiar una versión más adelante (nuevo aviso o nuevos términos): otro PR a este archivo y un redeploy.
Las órdenes nuevas guardan la versión nueva; las anteriores conservan la que aceptaron.

---

## 7. Primer despliegue y verificación

Fuente: README §6.1 pasos 4–10; `deploy-azure-pilot.yml`.

### 7.1 Certificar el SHA en `main`

1. Fusiona la promoción `development` → `main` (tú decides y fusionas).
2. Espera el run **Foundation CI** con `push` en `main`: los 13 jobs en verde.
3. Anota `<FOUNDATION_RUN_ID>` (número en la URL del run) y `<HEAD_SHA>` (40 caracteres hex en
   minúscula).
   ```bash
   gh run list --workflow "Foundation CI" --branch main --event push --limit 1 \
     --json databaseId,headSha,conclusion
   ```

### 7.2 Run 1: crea la infraestructura y se detiene en los secretos

Actions → **Deploy Azure PILOT** → *Run workflow* → **Use workflow from: `main`** (desde otra rama lo
rechaza el `deploy-gate`):
- `tested_git_sha` = `<HEAD_SHA>`
- `foundation_run_id` = `<FOUNDATION_RUN_ID>`
- `custom_domain_phase` = `none`
- `rotate_runtime_logins` = desmarcado

```bash
gh workflow run deploy-azure-pilot.yml --ref main \
  -f tested_git_sha=<HEAD_SHA> -f foundation_run_id=<FOUNDATION_RUN_ID> -f custom_domain_phase=none
```

Aprueba el run cuando el environment `azure-pilot` lo pida (*Review deployments*). Resultado esperado:
Stage 1 (identidades, VNet, Key Vault, llaves) y Stage 2 (logs, ACR, storage, PostgreSQL, entorno,
presupuesto) se completan, y el run falla con
`Key Vault kv-pvp-... lacks authcenter-paquetenvia-client-secret`. PostgreSQL puede tardar varios
minutos.

Del *Summary* del run copia la IP estática y el `asuid` (paso 4).

### 7.3 Entre runs

1. Escribe los tres secretos (paso 5.3–5.4).
2. Crea los registros DNS (paso 4) y espera a que propaguen.

### 7.4 Run 2 (`none`) y Run 3 (`bind`)

- **Run 2:** mismos inputs, `custom_domain_phase=none`. Debe terminar en verde: imágenes por digest,
  jobs `job-pv-pilot-migrate`, `-logins` y `-verify`, apps, Stage 5 (alertas OBS-002) y el smoke test
  sobre el FQDN de la ruta (`/` → 200, `/auth/session` → JSON, `/auth/backchannel-logout` →
  `invalid_request`).
- **Run 3:** cuando el DNS ya resuelva, mismos inputs con `custom_domain_phase=bind`. Desde aquí, **usa
  siempre `bind`**: con `none` se desvincula el apex (README §6.2).

  Si el DNS ya propagó antes del Run 2, puedes hacer el Run 2 directamente con `bind`. El repo describe
  la secuencia none → bind.

### 7.5 Verificación

```bash
# Certificado administrado emitido
ENV_NAME="$(az deployment group show -g rg-pv-pilot -n env001-platform \
  --query properties.outputs.containerEnvironmentName.value -o tsv)"
az containerapp env certificate list -g rg-pv-pilot -n "$ENV_NAME" --managed-certificates-only -o table   # Succeeded
curl -I https://paquetenvia.com/
# Recursos y presupuesto
az resource list -g rg-pv-pilot -o table
az consumption budget list --resource-group rg-pv-pilot -o table      # budget-pv-pilot, 100 USD
```

Pruebas manuales en el navegador sobre `https://paquetenvia.com` (README §6.1 pasos 9 y 10):
- Login por AuthCenter (requiere las URIs del paso 5.1 registradas).
- `/ops/orders/new` permite confirmar una orden (versiones de términos y aviso del paso 6.1 cargadas).
- DevTools → Network → WS: `/hubs/...` responde `101 Switching Protocols`.
- Subir una foto de prueba y comprobar que el blob tiene el index tag del escaneo de Defender.
- `http://paquetenvia.com` redirige a HTTPS o se rechaza (no verificado aún, README §9).

Después (fuera de la Sección A): restore drill (README §6.4) y guardar su línea `EVIDENCE`.

Si algo falla: README §6.7 (logs en Log Analytics:
`ContainerAppConsoleLogs_CL | where ContainerJobName_s startswith "job-pv-pilot"`).

> **Riesgo conocido (README §9):** varias piezas nunca se han probado contra Azure real: propiedades ARM
> de Defender `2025-01-01`, `customDomains` con `bindingType: Auto` en la ruta, PostgreSQL 18 con acceso
> privado en `mexicocentral`, `SSL Mode=VerifyFull` y la aceptación ARM de las alertas OBS-002. Un fallo
> en el primer run puede deberse a eso y no a tu configuración. Los adaptadores ADP-001
> (`AzureKeyVaultPiiKeyWrapClient`, `AzureBlobProofObjectStorage`, `DefenderForStorageThreatScanner`) ya
> están fusionados, pero nunca se han ejecutado contra Azure real (README §9).

---

## 8. Correo: crear los recursos de Azure Communication Services antes del 23 de octubre de 2026

Fuente: decisión `NTF-EMAIL-ACS-PILOT-2026-10-11` en `decision-log.md` y la guía de retiro de ACS de Microsoft
(<https://learn.microsoft.com/en-us/azure/communication-services/acs-retirement-and-breaking-changes-guide>,
actualizada el 2026-10-08).

Microsoft retira Azure Communication Services (ACS) el 30 de septiembre de 2028. Desde el **23 de octubre de 2026**
empieza a limitar las altas de clientes que no tengan un recurso de ACS creado antes de esa fecha, y avisa que las altas
nuevas de correo pueden cambiar "en cualquier momento". Los recursos que ya existan siguen funcionando hasta el retiro.
Por eso este paso **no espera al primer despliegue**: hazlo ya, aunque el resto de la guía siga pendiente.

Qué se crea y qué no:

- Dos recursos vacíos en `rg-pv-pilot`: **Communication Services** y **Email Communication Services**. No cuestan nada
  mientras no se envíen correos (README §4: unos 0.00025 USD por correo).
- No se agrega dominio ni remitente, no se generan claves y no se tocan el workflow ni las plantillas Bicep. El
  `Worker` usará su identidad administrada; conectarlo (endpoint, remitente y rol de Azure) es un cambio posterior. El
  despliegue corre en modo incremental, así que no borra estos recursos.

**Ubicación de datos (Data location):** ACS no ofrece México, y la guía de correo de Microsoft indica
`United States`. Es donde ACS procesa el contenido de los correos y guarda el remitente, así que entra en el análisis
de transferencia internacional de GATE-007. Si tu abogado pide otra, dímelo antes de crear los recursos. Usa la misma
en los dos.

```bash
# Cloud Shell (bash), como Owner de la suscripción, con las variables de la sección 1 (RG).
# Si rg-pv-pilot aún no existe, haz antes 1.b.
az provider register --namespace Microsoft.Communication --wait
az extension add --name communication --upgrade

SUFFIX="$(openssl rand -hex 3)"        # 6 caracteres; anótalo
ACS_NAME="acs-pv-pilot-${SUFFIX}"
ECS_NAME="ecs-pv-pilot-${SUFFIX}"
DATA_LOCATION="United States"

az communication create --name "$ACS_NAME" --location "Global" \
  --data-location "$DATA_LOCATION" --resource-group "$RG"
az communication email create --name "$ECS_NAME" --location "Global" \
  --data-location "$DATA_LOCATION" --resource-group "$RG"

# Verificación: debe aparecer un recurso de cada tipo
az resource list --resource-group "$RG" --resource-type Microsoft.Communication/communicationServices -o table
az resource list --resource-group "$RG" --resource-type Microsoft.Communication/emailServices -o table
```

Después mándame los dos nombres (no son secretos) para el cambio que conecte el `Worker`.

> Antes de producción el correo pasa a otro proveedor (AI-10 `release_gates.PRODUCTION`). Cuál, lo decides después.

---

## Preguntas abiertas (consolidado)

1. **GATE-007 (bloqueante):** no hay ninguna fila `GATE-007-*` con Type `Gate resolution`/`Gate scoping`
   en `decision-log.md`. Sin ella el deploy no inicia sesión en Azure. ¿Qué se registra y con qué ID?
   Implica tocar `docs/normative/**` con checksums y manifest sincronizados.
2. ~~apps.settings.json~~: resuelto por el PR #174 (versión por empresa, `piloto-2026-10-v1`).
3. **Términos y privacidad web** (`PAQUETERIA_TERMS_VERSION`/`PAQUETERIA_PRIVACY_VERSION`): ya están
   cableados (`web.parameters.json`, paso 6.1). **Bloquean el deploy** hasta que definas ambas versiones,
   que dependen del aviso de privacidad aprobado (GATE-007).
4. **GATE-003 Google:** cuota diaria, tope mensual y umbrales del presupuesto; registrar la decisión.
5. **Restricción de aplicación de la Google key:** sin IP de salida fija documentada, ¿solo restricción
   por API más cuota?
6. **Transferencia de direcciones a Google** depende de GATE-007.
7. ~~Orden de AuthCenter~~: corregido; README §6.1 ahora registra las URIs de `paquetenvia-web-prod`
   (paso 9) antes de la prueba de login (paso 10).
8. ~~README §6.1 paso 6~~: corregido; ahora nombra los tres secretos del owner
   (`authcenter-paquetenvia-client-secret`, `google-maps-api-key`, `public-tracking-link-key`).
9. **Prevent self-review** en el environment, con un único revisor.
10. **CAA en el DNS** de `paquetenvia.com`: el repo no lo menciona; revisar antes del `bind`.
11. **Clonar el repo en Cloud Shell** para usar `kv-firewall.sh` (repo privado). Hay alternativa manual
    en 5.3.
12. ~~README §9 desactualizado~~: corregido; los adaptadores ADP-001 constan como fusionados y pendientes
    solo de verificación contra Azure real.
13. **Ubicación de datos de ACS** (sección 8): ACS no ofrece México. ¿`United States` u otra que pida tu abogado
    por GATE-007? Crea los recursos antes del 23 de octubre de 2026.
