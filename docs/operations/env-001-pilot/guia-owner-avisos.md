# Guía del owner: conectar los avisos al destinatario en el piloto (AI-08 NTF-008)

Para: el owner de Paquetenvia. Base: rama `feat/ntf-008-pilot-notice-wiring` (2026-10-31).
Decisiones: `NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31` (rondas V y W) y `NTF-RECIPIENT-NOTICES-2026-10-20`
(E4-P7 y E4-P12), en `docs/normative/v0.6/decision-log.md`.

NTF-008 conecta los avisos al piloto **con todo apagado**: los secretos y la llave de avisos llegan a la API y al
Worker, el Worker gana el permiso de mandar correo por ACS, y la suscripción de Event Grid queda preparada detrás de
`emailEventsPhase=none`. Ningún aviso sale, ningún webhook responde (404 uniforme) y la API no guarda copias para
avisos (W4) hasta el cambio que cierre GATE-004 y GATE-007, que es otro PR (W11, punto 10).

**El PR de NTF-008 queda en borrador y no se fusiona** hasta que confirmes que el primer despliegue del piloto salió
bien y que existe todo lo de Azure, Meta y DNS de esta guía (V1 y W3). Si se fusionara antes, el primer despliegue
siguiente se detendría: la API y el Worker no arrancan si les falta un secreto mapeado.

Reglas de esta guía:

- Aquí solo hay **nombres**, nunca valores. No pegues un valor (token, secreto, id de recurso, id de suscripción,
  nombre real de un recurso, número de teléfono) en el repositorio, en el PR, en un comentario ni en el chat.
- Cada paso termina con **"Dile al orquestador"**: solo sí/no, nombres de roles o plantillas, o conteos.
- Los comandos `az` se corren en **Azure Cloud Shell (bash)** como Owner de la suscripción, con las variables de
  `guia-owner-seccion-a.md` §1 (`RG`, `RG_ID`, `SUBSCRIPTION_ID`).

## Resumen y lista de comprobación

| # | Paso | Cuándo | Lo que detiene el despliegue si falta |
|---|---|---|---|
| 1 | Primer despliegue del piloto (W1) | Primero; crea la bóveda | Todo lo de Key Vault de esta guía |
| 2 | Dos roles propios y la condición del deployer (W5, W6) | Cuando quieras (el grupo de recursos ya existe) | Paso "Look up and check the recipient-notice custom roles" |
| 3 | Variable `PILOT_ACS_NAME` (W2) | Cuando quieras | Paso "Validate inputs and environment variables" y "Check the Communication Services resource" |
| 4 | App de Meta, número y los 3 valores de Meta en Key Vault (W7) | Key Vault: después del paso 1 | Ventana 2: "lacks whatsapp-..." |
| 5 | Verificación del negocio en Meta y nombre visible = razón social (E4-P7) | En paralelo; puede tardar | Nada en el despliegue; sí el cambio de compuertas |
| 6 | Remitente de ACS `avisos@paquetenvia.com`, nombre visible «Seguimiento de envío» (E4-P12) | Después del paso 7 | Nada en el despliegue; sí el cambio de compuertas |
| 7 | DNS: SPF, DKIM y DMARC en cuarentena (W9) | Antes del paso 6 | La verificación del dominio en ACS |
| 8 | What-if en Cloud Shell y conteos en el PR (W8) | Al final, antes de fusionar | La fusión |
| 9 | Plantillas de WhatsApp a Meta, después del abogado (W10) | Tras la respuesta de GATE-007 | El cambio de compuertas |
| 10 | Qué decirle al orquestador | En cada paso | — |
| 11 | Después del cambio de compuertas (no es parte de NTF-008) | Más tarde | — |
| 12 | Riesgos que quedan | Léelo antes de fusionar | — |

---

## 1. Requisito previo: el primer despliegue del piloto (W1)

Respondiste (W1) que el piloto no se ha desplegado. El primer despliegue (Run 1 a Run 3 de
`guia-owner-seccion-a.md` §7, con `custom_domain_phase=bind` al final) crea la bóveda `kv-pvp-<sufijo>`. Todo lo
de Key Vault de esta guía (paso 4.5) va **después** de ese despliegue, y NTF-008 se fusiona después de él.

Comprobación de solo lectura (no escribe nada):

```bash
az keyvault list --resource-group "$RG" --query "length(@)" -o tsv   # 1 = la bóveda existe
```

**Dile al orquestador:** "Primer despliegue: hecho, fecha y run id" (el run id no es secreto).

## 2. Roles propios y la condición del deployer (W5, W6)

Azure no trae un rol que solo abra (desenvuelva) con una llave, ni uno que solo mande correo por ACS. Por eso creas
dos roles propios, asignables solo dentro de `rg-pv-pilot`, como el rol de etiquetas de blob
(`guia-owner-seccion-a.md` §2.3). La API usa el rol integrado *Key Vault Reader* sobre la llave de avisos: le basta
leer la parte pública de la llave para cerrar las copias en su propio proceso.

El workflow busca cada rol **por su nombre exacto** y comprueba, antes de desplegar, que tenga exactamente estas
acciones y ninguna otra (`env001_pilot_guards.py custom-role-check`). Copia los JSON tal cual.

Fuentes (consultadas el 2026-10-31):

- Acciones de Key Vault y el rol *Key Vault Reader*: <https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/security>
  (actualizada 2026-07-01). `Microsoft.KeyVault/vaults/keys/read` "read properties and public material of a key";
  *Key Vault Reader* tiene `Microsoft.KeyVault/vaults/*/read` y nunca lee valores de secretos ni la parte privada.
- Acciones para mandar correo con un rol propio: <https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/email/send-email-smtp/smtp-authentication>
  (pestaña *Custom role*, actualizada 2026-09-23): `Microsoft.Communication/CommunicationServices/Read`,
  `Microsoft.Communication/CommunicationServices/Write` y `Microsoft.Communication/EmailServices/write`. Ninguna es
  `ListKeys` ni `RegenerateKey`, así que el Worker nunca puede leer ni cambiar las claves de ACS. Ojo: estas acciones
  vienen de la página de SMTP, y `CommunicationServices/Write` también permite cambiar la configuración del recurso, más
  que "solo mandar" (riesgo 2 del paso 12).

### 2.1 Rol "solo abrir" para el Worker sobre la llave de avisos

```bash
cat > /tmp/ntf008-unwrap.json <<EOF
{
  "Name": "Paquetenvia Pilot Recipient Notice Unwrap",
  "Description": "Unwrap with recipient-notice-kek and read its versions only. NTF-008 / W5.",
  "Actions": [],
  "NotActions": [],
  "DataActions": [
    "Microsoft.KeyVault/vaults/keys/read",
    "Microsoft.KeyVault/vaults/keys/unwrap/action"
  ],
  "NotDataActions": [],
  "AssignableScopes": ["${RG_ID}"]
}
EOF
az role definition create --role-definition /tmp/ntf008-unwrap.json --output none
rm /tmp/ntf008-unwrap.json
```

### 2.2 Rol "solo mandar correo" para el Worker sobre ACS

```bash
cat > /tmp/ntf008-email-sender.json <<EOF
{
  "Name": "Paquetenvia Pilot Email Sender",
  "Description": "Send e-mail through the pilot Communication Services resource only. NTF-008 / W6.",
  "Actions": [
    "Microsoft.Communication/CommunicationServices/Read",
    "Microsoft.Communication/CommunicationServices/Write",
    "Microsoft.Communication/EmailServices/write"
  ],
  "NotActions": [],
  "DataActions": [],
  "NotDataActions": [],
  "AssignableScopes": ["${RG_ID}"]
}
EOF
az role definition create --role-definition /tmp/ntf008-email-sender.json --output none
rm /tmp/ntf008-email-sender.json
```

### 2.3 Los ids de los tres roles propios

```bash
TAG_READER_ID="$(az role definition list --custom-role-only true \
  --name "Paquetenvia Pilot Blob Tag Reader" --scope "$RG_ID" --query "[0].name" -o tsv)"
UNWRAP_ID="$(az role definition list --custom-role-only true \
  --name "Paquetenvia Pilot Recipient Notice Unwrap" --scope "$RG_ID" --query "[0].name" -o tsv)"
SENDER_ID="$(az role definition list --custom-role-only true \
  --name "Paquetenvia Pilot Email Sender" --scope "$RG_ID" --query "[0].name" -o tsv)"
# Se detiene (exit 1 dentro del paréntesis, sin cerrar Cloud Shell) si falta un id o no es un GUID.
( for id in "$TAG_READER_ID" "$UNWRAP_ID" "$SENDER_ID"; do
    [[ "$id" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || {
      echo "ALTO: falta un rol propio; espera 1-2 minutos y repite el paso 2.3. No sigas al 2.4."; exit 1; }
  done
  echo "Los tres ids existen: sigue al paso 2.4." )
```

Si ves **ALTO**, no pases al paso 2.4 (de todos modos el 2.4 se niega a borrar nada si falta un id). No copies estos
ids a ningún lado: se usan solo en el paso siguiente, en la misma sesión de Cloud Shell.

### 2.4 Volver a crear la condición del deployer

La asignación *Role Based Access Control Administrator* del deployer (`bootstrap.md` §5) admite hoy 7 roles. Las
plantillas de NTF-008 asignan tres roles más: *Key Vault Reader* y los dos roles propios. Una condición de Azure no se
edita por partes, así que borras esa asignación y la creas otra vez con la lista ampliada (10 roles). El rol
*Contributor* del deployer no se toca. El bloque revisa primero que existan el grupo de recursos, el deployer y los tres
ids del paso 2.3; si falta uno, escribe **ALTO** y no borra nada.

```bash
APP_ID="$(az ad app list --display-name "paquetenvia-pilot-deployer" --query "[0].appId" -o tsv)"
SP_OBJECT_ID="$(az ad sp show --id "$APP_ID" --query id -o tsv)"

# AcrPull, Storage Blob Data Contributor, Storage Blob Delegator, Key Vault Secrets User, Key Vault Secrets Officer,
# Key Vault Crypto Service Encryption User, Key Vault Reader (NTF-008) y los tres roles propios.
ALLOWED="7f951dda-4ed3-4680-a7ca-43fe172d538d, ba92f5b4-2d11-453d-a403-e96b0029c9fe, db58b8e5-c6ad-4a2a-8342-4190687cbf4a, 4633458b-17de-408a-b874-0445c86b69e6, b86a8fe4-44ce-4948-aee5-eccb2c155cd7, e147488a-f6f5-4113-8e2d-b22465e65bf6, 21090545-7ca7-4776-b22c-e363652d74d2, ${TAG_READER_ID}, ${UNWRAP_ID}, ${SENDER_ID}"
CONDITION="((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ALLOWED}})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ALLOWED}}))"

# Se niega a borrar nada si falta el grupo de recursos, el deployer o uno de los tres ids (un id vacío dejaría al
# deployer sin poder asignar ese rol).
GUID='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
if [[ "${RG_ID:-}" == */resourceGroups/rg-pv-pilot && "${SP_OBJECT_ID:-}" =~ $GUID && "${TAG_READER_ID:-}" =~ $GUID \
      && "${UNWRAP_ID:-}" =~ $GUID && "${SENDER_ID:-}" =~ $GUID ]]; then
  az role assignment delete --assignee "$SP_OBJECT_ID" \
    --role "Role Based Access Control Administrator" --scope "$RG_ID"
  az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
    --role "Role Based Access Control Administrator" --scope "$RG_ID" \
    --condition "$CONDITION" --condition-version "2.0" --output none
else
  echo "ALTO: falta RG_ID, el deployer o un id del paso 2.3. No se borró nada; repite los pasos 2.3 y 2.4 en la misma sesión."
fi
```

Si la creación falla después del borrado (por ejemplo, por un error al pegar), el deployer queda sin *Role Based
Access Control Administrator* hasta que la vuelvas a crear: repite solo el bloque `az role assignment create` con las
mismas variables. Mientras tanto un despliegue fallaría al asignar roles, sin dar permisos de más.

### 2.5 Comprobar

```bash
az role assignment list --assignee "$SP_OBJECT_ID" --all \
  --query "[].{role:roleDefinitionName,scope:scope,condition:condition!=null}" -o table
# Tres filas: Contributor y Role Based Access Control Administrator (condition = True) sobre rg-pv-pilot, y
# Key Vault Secrets Officer sobre la bóveda kv-pvp-<sufijo> (la crea el primer despliegue para escribir los secretos).
```

El deployer sigue sin poder conceder Owner, Contributor, User Access Administrator ni Storage Blob Data Owner.

**Dile al orquestador:** "Roles creados: Paquetenvia Pilot Recipient Notice Unwrap y Paquetenvia Pilot Email Sender;
condición del deployer recreada con 10 roles: sí".

## 3. La variable `PILOT_ACS_NAME` (W2, W11 punto 4)

Respondiste (W2) que ya creaste los dos recursos de correo (`guia-owner-seccion-a.md` §8). El nombre del recurso
**Communication Services** (no el de *Email Communication Services*) va solo en una variable del environment
`azure-pilot` de GitHub, nunca en el repositorio, como `PILOT_POSTGRES_SERVER_NAME`. El workflow valida su formato
antes de entrar a Azure, comprueba después del login que el recurso existe en `rg-pv-pilot` y guarda una sola vez su
id de recurso en Key Vault (`acs-communication-resource-id`, paso 4.5).

```bash
# Cloud Shell: el nombre (no lo copies a ningún archivo ni chat)
az resource list --resource-group "$RG" --resource-type Microsoft.Communication/communicationServices --query "[].name" -o tsv
```

```bash
# Tu máquina, con el GitHub CLI (gh auth login primero); pega el nombre en lugar de <nombre>
gh variable set PILOT_ACS_NAME --env azure-pilot --repo Eduard0corona/Paquetenvia --body "<nombre>"
```

No cambies esta variable después del primer despliegue con NTF-008: el id del recurso se guarda una sola vez y la API
solo acepta los eventos de correo de ese recurso.

**Dile al orquestador:** "`PILOT_ACS_NAME` creada: sí" (sin el nombre).

## 4. App de Meta, número de WhatsApp y los 3 valores de Meta (W7, W11 punto 9)

El workflow genera solo, una vez y sin mostrarlos, los siete secretos que nadie escribe (W7):
`paquetenvia-contact-lookup-key-1`, `whatsapp-webhook-verify-token`, `whatsapp-webhook-verify-token-previous`,
`whatsapp-app-secret-previous`, `acs-email-events-secret`, `acs-email-events-secret-previous` y
`acs-communication-resource-id`. **Tú escribes solo tres**, que solo Meta te puede dar:

| Secreto de Key Vault | Qué es | Lo lee |
|---|---|---|
| `whatsapp-cloud-api-token` | token de un usuario del sistema de Meta | solo el Worker (manda los mensajes) |
| `whatsapp-phone-number-id` | id del número de WhatsApp Business (solo dígitos) | el Worker y la API |
| `whatsapp-app-secret` | *App secret* de la app de Meta | solo la API (firma de los webhooks) |

### 4.1 Portafolio de negocio y app

1. En Meta Business Suite, el portafolio de negocio a nombre de la **razón social** (paso 5).
2. En <https://developers.facebook.com/apps>, una app de tipo **Business** en ese portafolio; agrega el producto
   **WhatsApp**.

### 4.2 Cuenta de WhatsApp Business y número

1. Una cuenta de WhatsApp Business (WABA) en el portafolio.
2. Un número que **nunca** se haya usado en la app de WhatsApp; verifícalo por SMS o llamada.
3. Nombre visible del número = la razón social (E4-P7, paso 5), nunca "Paquetenvia".
4. Regístralo para la Cloud API con un PIN de dos pasos; guarda el PIN en tu gestor de contraseñas.
5. Agrega un método de pago a la WABA.

### 4.3 Token del usuario del sistema (W11 punto 9)

En *Configuración del negocio → Usuarios del sistema*: agrega un usuario del sistema, asígnale la app y la WABA, y
pulsa **Generar token**. Elige:

- la app de Paquetenvia;
- caducidad: **Nunca** (W11 punto 9, "sin vencimiento");
- permisos: **solo `whatsapp_business_messaging`** ("solo permiso de envío").

Fuentes (consultadas el 2026-10-31): <https://developers.facebook.com/docs/whatsapp/access-tokens> ("choose a token
expiration preference") y <https://developers.facebook.com/docs/permissions/> (`whatsapp_business_messaging` "allows an
app to send WhatsApp messages ... to a specific phone number"). La guía general de Meta pide además
`business_management` y `whatsapp_business_management` para administrar activos; el Worker solo manda mensajes, y las
plantillas las creas tú en WhatsApp Manager (paso 9). Si Meta no ofrece la caducidad "Nunca", **no generes el token**:
avísale al orquestador, porque cambiaría la decisión W11 punto 9.

El token aparece una sola vez: va directo a Key Vault (4.5), nunca a un archivo ni al chat.

### 4.4 App secret e id del número

- *App secret*: en la app, *Configuración de la app → Básica → Clave secreta de la app*.
- *Phone number id*: *WhatsApp → Configuración de la API* (el id del número, no el número).

No configures todavía el webhook ni suscribas campos (paso 11).

### 4.5 Escribirlos en Key Vault (después del primer despliegue)

Acceso temporal a la bóveda como en `guia-owner-seccion-a.md` §5.3 (rol *Key Vault Secrets Officer* y regla de
firewall para tu IP), luego:

```bash
KV="$(az deployment group show -g "$RG" -n env001-security --query properties.outputs.vaultName.value -o tsv)"
bash deploy/azure/pilot/kv-firewall.sh open "$KV"

# Pega cada valor cuando lo pida y pulsa Enter; no se muestra ni queda en el historial.
read -rs META_TOKEN && printf '%s' "$META_TOKEN" > /tmp/mt && \
az keyvault secret set --vault-name "$KV" --name whatsapp-cloud-api-token --file /tmp/mt --encoding utf-8 --output none; \
shred -u /tmp/mt; unset META_TOKEN
read -rs META_PHONE_NUMBER_ID && printf '%s' "$META_PHONE_NUMBER_ID" > /tmp/mp && \
az keyvault secret set --vault-name "$KV" --name whatsapp-phone-number-id --file /tmp/mp --encoding utf-8 --output none; \
shred -u /tmp/mp; unset META_PHONE_NUMBER_ID
read -rs META_APP_SECRET && printf '%s' "$META_APP_SECRET" > /tmp/ma && \
az keyvault secret set --vault-name "$KV" --name whatsapp-app-secret --file /tmp/ma --encoding utf-8 --output none; \
shred -u /tmp/ma; unset META_APP_SECRET

# Solo nombres, nunca valores
az keyvault secret list --vault-name "$KV" --query "[?starts_with(name, 'whatsapp-')].name" -o tsv | sort

bash deploy/azure/pilot/kv-firewall.sh close "$KV"
```

Quita tu rol temporal al terminar (`guia-owner-seccion-a.md` §5.4). Nunca borres un secreto: la bóveda tiene purge
protection y el nombre queda bloqueado 90 días.

**Rotación (W11 punto 2).** Cada secreto de webhook tiene una ranura `-previous` con un valor aleatorio que nadie
tiene. Para rotar el *App secret* de Meta o el secreto de Event Grid: copia el valor actual a la ranura `-previous`
como versión nueva, escribe el valor nuevo en el secreto actual, vuelve a desplegar (las apps leen Key Vault al
arrancar) y, cuando Meta o Event Grid ya usen el nuevo, escribe otra vez un valor aleatorio en `-previous`. Una
rotación a medias deja aceptado el valor viejo hasta ese último paso.

**Dile al orquestador:** "Los 3 secretos de Meta existen en Key Vault: sí" (nunca valores).

## 5. Verificación del negocio en Meta y nombre visible = razón social (E4-P7)

1. En el portafolio: *Centro de seguridad → Verificación del negocio*, con los documentos de la razón social. Si
   Meta pide verificar el dominio `paquetenvia.com`, agrega el registro TXT que te dé (paso 7).
2. El nombre visible del número es la **razón social** (E4-P7 "La razón social (Recommended)": "Es el responsable
   legal del aviso de privacidad y no es la marca comercial"), nunca "Paquetenvia" mientras GATE-001 siga neutral.
3. Meta revisa el nombre visible; puede tardar días o semanas.

**Dile al orquestador:** "Verificación del negocio: iniciada / aprobada; nombre visible: en revisión / aprobado".

## 6. Remitente de ACS: `avisos@paquetenvia.com`, nombre visible «Seguimiento de envío» (E4-P12)

E4-P12 "avisos@paquetenvia.com (Recommended)": "Nombre visible "Seguimiento de envío", sin buzón de respuestas".
Todo se hace en el recurso **Email Communication Services** del portal de Azure:

1. *Provision domains → Add domain → Custom domain*: `paquetenvia.com`. El portal muestra los registros DNS del
   paso 7 (verificación TXT, SPF y dos DKIM).
2. Con los registros creados, *Verify* en el dominio hasta ver **Verified** en Domain, SPF, DKIM y DKIM2.
3. **Límites de envío más altos.** Microsoft solo deja agregar remitentes propios (MailFrom) en un dominio propio "with
   higher than default sending limits"; con los límites por omisión solo existe la dirección `donotreply` del dominio
   (<https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/email/add-multiple-senders>, actualizada
   2026-09-23). El aumento se pide con un ticket de soporte y la información que lista
   <https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/email-quota-increase> (actualizada
   2026-09-23, consultada 2026-10-31); para este piloto: tipo de correo transaccional, unas 20 entregas al día, bajas con
   enlace de un clic y `List-Unsubscribe`, y los acuses de rebote marcan el aviso `FAILED`. Esa página dice que la
   solicitud no se aprueba sola: "Email quota increase requests aren't automatically approved. The reviewing team
   considers your overall sender reputation when determining approval status", con una tasa de fallas de entrega
   "below 2%". También pide construir esa reputación antes, "by sending legitimate production workload emails", y el
   formulario pregunta si el dominio ya "is already set up and currently used for sending messages". `paquetenvia.com`
   todavía no manda correo, así que Microsoft puede rechazar o posponer el aumento. La misma página recomienda un
   registro MX para la reputación; esta guía no lo crea porque E4-P12 no quiere buzón de respuestas (punto 5).
   **No uses `donotreply` en su lugar.** Si Microsoft rechaza o pospone el aumento, o pide el registro MX, **detente y
   avísale al orquestador**: hace falta una decisión nueva tuya sobre E4-P12, y como NTF-008 espera a los pasos 1 a 8
   (W3), el PR sigue en borrador hasta esa decisión.
4. *MailFrom addresses → Add*: dirección `avisos`, nombre visible `Seguimiento de envío`.
5. Deja apagado *User engagement tracking* (AI-24: "plain text only, engagement tracking off"). No crees un buzón,
   un `Reply-To` ni un registro MX: nadie recibe respuestas en esta dirección.
6. En el recurso **Communication Services**: *Email → Domains → Connect domain* y conecta `paquetenvia.com`.
7. No crees suscripciones de Event Grid ni temas de sistema en el recurso de ACS (solo se permite un tema de sistema
   por recurso y lo crea la plantilla en el cambio de compuertas), y no uses las claves de acceso de ACS: el Worker
   manda con su identidad administrada y el rol del paso 2.2.

**Dile al orquestador:** "Dominio paquetenvia.com verificado en ACS (Domain, SPF, DKIM, DKIM2): sí; aumento de
límites: pedido / aprobado / rechazado / pospuesto; remitente avisos con nombre «Seguimiento de envío»: sí; dominio
conectado: sí".

## 7. DNS de `paquetenvia.com`: SPF, DKIM y DMARC en cuarentena (W9)

Los valores de verificación, SPF y DKIM los da el portal de ACS (paso 6.1); cópialos de ahí. DMARC es tu decisión
(W9 "Cuarentena (Recommended)": "Lo falso va a spam. Se puede endurecer a rechazo después de ver correos reales").

Antes de crear nada, revisa qué existe (solo lectura):

```bash
dig +short TXT paquetenvia.com
dig +short TXT _dmarc.paquetenvia.com
dig +short MX paquetenvia.com
```

En tu proveedor de DNS:

| Tipo | Host | Valor |
|---|---|---|
| TXT | `@` | el de verificación del dominio que muestra ACS |
| TXT | `@` | el SPF que muestra ACS. Si ya hay un registro SPF (`v=spf1 ...`), **combínalo en uno solo**: nunca dos registros SPF |
| CNAME | el primer selector DKIM que muestra ACS (`selector1-..._domainkey`) | el valor que muestra ACS |
| CNAME | el segundo selector DKIM que muestra ACS (`selector2-..._domainkey`) | el valor que muestra ACS |
| TXT | `_dmarc` | `v=DMARC1; p=quarantine` |

El registro DMARC no lleva `rua` ni `ruf`: no hay buzón de reportes (E4-P12). Si Meta pidió verificar el dominio
(paso 5), su TXT va también en `@`. Comprueba la propagación con los mismos `dig` y verifica en ACS (paso 6.2).

**Dile al orquestador:** "Registros DNS creados (verificación, SPF, DKIM x2, DMARC p=quarantine): sí; había SPF o
DMARC antes: sí/no".

## 8. What-if en Cloud Shell antes de fusionar (W8)

W8 "Tú en Cloud Shell antes de fusionar (Recommended)": "La guía trae los comandos; pegas en el PR solo los conteos de
cambios". Hazlo al final, con los pasos 1 a 7 hechos. Cloud Shell trae `az` (con Bicep) y `gh`.

El what-if debe describir lo que desplegará la fusión, no la rama sola: la rama no trae los valores del primer
despliegue que fusionaste en `development` (`web.parameters.json`, `observability.parameters.json`). Por eso usa la
**referencia de fusión del PR de NTF-008** (`pull/<número>/merge`, la que GitHub arma con la rama encima del
`development` de hoy) y comprueba antes que ya trae tus valores. Si una de las dos comprobaciones falla, `development`
todavía no los tiene o el PR tiene un conflicto: detente y avísale al orquestador.

```bash
gh auth login
gh repo clone Eduard0corona/Paquetenvia -- --depth 1 && cd Paquetenvia
PR="$(gh pr list --repo Eduard0corona/Paquetenvia --head feat/ntf-008-pilot-notice-wiring --json number --jq '.[0].number')"
git fetch --depth 1 origin "pull/${PR}/merge" && git checkout --detach FETCH_HEAD
python3 tools/azr-001/env001_pilot_guards.py web-check --file deploy/azure/pilot/web.parameters.json
python3 tools/azr-001/env001_pilot_guards.py observability-check --file deploy/azure/pilot/observability.parameters.json

UNWRAP_ROLE="$(az role definition list --custom-role-only true --name "Paquetenvia Pilot Recipient Notice Unwrap" --scope "$RG_ID" --query "[0].id" -o tsv)"
SENDER_ROLE="$(az role definition list --custom-role-only true --name "Paquetenvia Pilot Email Sender" --scope "$RG_ID" --query "[0].id" -o tsv)"
ACS="$(az resource list --resource-group "$RG" --resource-type Microsoft.Communication/communicationServices --query "[0].name" -o tsv)"
VAULT_URI="$(az deployment group show -g "$RG" -n env001-security --query properties.outputs.vaultUri.value -o tsv)"
PII_KEY="$(az deployment group show -g "$RG" -n env001-security --query properties.outputs.piiKeyUri.value -o tsv)"
DP_KEY="$(az deployment group show -g "$RG" -n env001-security --query properties.outputs.dataProtectionKeyUri.value -o tsv)"
BLOB_URI="$(az deployment group show -g "$RG" -n env001-platform --query properties.outputs.blobServiceUri.value -o tsv)"
PROXY="$(az deployment group show -g "$RG" -n env001-platform --query properties.outputs.containerAppsSubnetPrefix.value -o tsv)"
image() { az containerapp show -g "$RG" -n "$1" --query "properties.template.containers[0].image" -o tsv; }

# counts TEMPLATE [PARAMETERS...]: solo el número de cambios por tipo (Create, Modify, NoChange, ...).
counts() {
  local template="$1"; shift
  az deployment group what-if --resource-group "$RG" --template-file "deploy/azure/pilot/${template}.bicep" \
    --parameters "$@" --no-pretty-print --query "changes[].changeType" -o tsv | sort | uniq -c
}

echo "== security"; counts security "recipientNoticeUnwrapRoleDefinitionId=${UNWRAP_ROLE}"
echo "== apps"; counts apps "apiImage=$(image ca-pv-pilot-api)" "workerImage=$(image ca-pv-pilot-worker)" \
  "webImage=$(image ca-pv-pilot-web)" "customDomainPhase=bind" "piiKeyUri=${PII_KEY}" "dataProtectionKeyUri=${DP_KEY}" \
  "recipientNoticeKeyUri=${VAULT_URI}keys/recipient-notice-kek" "blobServiceUri=${BLOB_URI}" "trustedProxyNetwork=${PROXY}" \
  "acsName=${ACS}" "acsEmailSenderRoleDefinitionId=${SENDER_ROLE}" \
  "businessSettings=@deploy/azure/pilot/apps.settings.json" "@deploy/azure/pilot/web.parameters.json"
echo "== events"; counts events "acsName=${ACS}"
```

Qué esperar (los *NoChange* dependen de lo que ya existe; Azure a veces marca *Modify* sin cambio real):

- `security`: **Create 4**. Tres son de NTF-008: la llave `recipient-notice-kek`, *Key Vault Reader* de la API y el rol
  "solo abrir" del Worker sobre ella. La cuarta es *Key Vault Secrets Officer* sobre la bóveda **para tu usuario**: la
  plantilla da ese rol a quien despliega (`deployer()`), que en el workflow es el deployer y en Cloud Shell eres tú. El
  what-if no crea nada y el workflow no crea esa fila; no la cuentes como un cambio de NTF-008.
- `apps`: **Create 14** (nueve asignaciones por secreto nuevas de la API, cuatro del Worker y la del rol de correo
  sobre ACS) y **Modify 2** (la API y el Worker). El what-if puede marcar con un aviso las asignaciones sobre los siete
  secretos que todavía no existen: el workflow los genera en la ventana 1 del primer despliegue con NTF-008, antes de
  la etapa 4.
- `events`: ningún cambio (`emailEventsPhase=none`).

Si los *Create* no coinciden, pega los conteos de todas formas y dilo: el orquestador lo revisa antes de fusionar.

Pega en el PR **solo los conteos** (por ejemplo "security: Create 3, Modify 1, NoChange 4") y, si apareció, el texto
del aviso sin ids ni nombres. Nunca pegues la salida completa: lleva ids de suscripción y nombres de recursos.

**Dile al orquestador:** "What-if hecho: conteos pegados en el PR".

## 9. Plantillas de WhatsApp, después del abogado (W10)

W10 "Después del abogado (Recommended)": "Una sola revisión; los nombres aprobados entran en el cambio de compuertas".
Espera a que el abogado de GATE-007 conteste si el primer mensaje lleva una línea del aviso de privacidad (AI-24
`recipient_notices.templates.whatsapp.privacy_line`); con su respuesta, crea las cuatro plantillas en WhatsApp Manager:

- categoría **Utilidad**, idioma **Español (MEX)** (`es_MX`);
- el cuerpo de cada aviso **exactamente** como AI-24 `recipient_notices.templates.whatsapp.bodies`, con una sola
  variable `{{1}}` (el número de guía) y un ejemplo;
- pie: `Responde BAJA para no recibir más avisos`;
- botones en este orden: primero **URL** "Ver seguimiento" `https://paquetenvia.com/track/{{1}}` (dinámica), luego
  **respuesta rápida** "No recibir avisos" (L4 elección 9). Meta acepta combinar los dos tipos si cada tipo va junto
  (<https://developers.facebook.com/docs/whatsapp/business-management-api/message-templates/components>, consultada
  2026-10-31: "buttons must be organized into two groups"; el pie admite 60 caracteres);
- nombres en minúsculas con guiones bajos.

**Dile al orquestador:** "Plantillas: los cuatro nombres y su estado (en revisión / aprobada / rechazada)". Los nombres
aprobados entran en el cambio de compuertas.

## 10. Qué decirle al orquestador en cada paso

| Paso | Qué decir (nunca valores) |
|---|---|
| 1 | Primer despliegue: hecho, fecha y run id |
| 2 | Roles creados (los dos nombres); condición del deployer recreada con 10 roles |
| 3 | `PILOT_ACS_NAME` creada (sin el nombre) |
| 4 | Los 3 secretos de Meta existen en Key Vault; el token se generó sin caducidad y solo con `whatsapp_business_messaging` |
| 5 | Verificación del negocio y nombre visible: estado |
| 6 | Dominio verificado; aumento de límites pedido/aprobado/rechazado/pospuesto (rechazado o pospuesto: decisión nueva sobre E4-P12); remitente `avisos` con «Seguimiento de envío»; dominio conectado |
| 7 | Registros DNS creados; si ya había SPF o DMARC |
| 8 | Conteos del what-if pegados en el PR |
| 9 | Los cuatro nombres de plantilla y su estado |

Con los pasos 1 a 8 hechos (el 9 puede seguir en revisión), marcas el PR como listo, lo fusionas en `development` y
promueves a `main` (13/13). El siguiente despliegue con `custom_domain_phase=bind` debe terminar con las revisiones
sanas; en la ventana 1 verás "Generated ..." para los siete secretos que crea el workflow, y los webhooks siguen
respondiendo 404. Desde ese despliegue, «Detener avisos» ya no responde 503: registra bajas reales, globales y
definitivas aunque todavía no salga ningún aviso.

## 11. Después del cambio de compuertas (no es parte de NTF-008)

Esto lo hace un PR posterior que registre las filas de resolución de GATE-004 y GATE-007, encienda los interruptores,
ponga los nombres de plantilla aprobados y cambie `emailEventsPhase` a `subscribe` (O8 "Registrar al cerrar las
compuertas (Recommended)"; W11 punto 10). Queda aquí para que no se olvide el orden:

1. Ese cambio se despliega **después de las 21:00** hora de Mazatlán, con `custom_domain_phase=bind`. El mismo run
   crea la suscripción de Event Grid a `https://paquetenvia.com/api/v1/messaging/email/events` (etapa 6) y comprueba
   que quede *Succeeded*.
2. **Antes de las 8:00**, en la app de Meta, *WhatsApp → Configuración*: URL de devolución de llamada
   `https://paquetenvia.com/api/v1/messaging/whatsapp/webhook` y el token de verificación, que lees una sola vez de
   Key Vault (`whatsapp-webhook-verify-token`, con el acceso temporal del paso 4.5); *Verificar y guardar*; suscribe el
   campo `messages`; pasa la app a modo *Live* (puede pedir la URL del aviso de privacidad de GATE-007).
3. Prueba de punta a punta con una orden de prueba a un teléfono y un correo tuyos. No pruebes BAJA, la baja por
   correo ni «Detener avisos» con un contacto que vayas a necesitar: en el piloto la baja es definitiva (E4-P16).

## 12. Riesgos que quedan

Nadie los pudo comprobar sin Azure; por eso quedan escritos aquí y en el PR.

1. **El tipo de tema de Event Grid no está confirmado.** `events.bicep` crea el tema de sistema del recurso de ACS con
   `topicType` `Microsoft.Communication.CommunicationServices`; ninguna página de Microsoft Learn consultada lo muestra
   escrito así. Solo se usa con `emailEventsPhase=subscribe`, en el cambio de compuertas (paso 11), no en NTF-008. Si
   ese despliegue falla en la etapa 6, la causa probable es este valor: la etapa 6 corre después de que la API y el
   Worker ya están sanos, y el cambio de compuertas lo corrige.
2. **El rol de correo viene de la página de SMTP.** Las tres acciones de "Paquetenvia Pilot Email Sender" (paso 2.2)
   son las que Microsoft Learn da para mandar correo por SMTP con un rol propio. Que basten para la API REST de correo
   con la identidad administrada del Worker se comprueba con el primer envío real, después del cambio de compuertas.
   Además, `Microsoft.Communication/CommunicationServices/Write` también deja cambiar la configuración del recurso de
   ACS (más que "solo mandar"); nunca da `ListKeys` ni `RegenerateKey`. Si el primer envío falla por permisos, el rol se
   corrige en el cambio de compuertas con una decisión nueva.
