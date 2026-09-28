# ENV-001 pilot: one-time bootstrap (owner)

The deployment workflow authenticates as an Entra service principal through GitHub OIDC. It cannot
create itself, its resource group or its role assignments. The owner runs these steps **once** in
**Azure Cloud Shell (bash)** as a subscription Owner, and then creates the GitHub Environment.

No step creates a client secret, and no credential is stored in GitHub. Nothing here calls
`az ad signed-in-user show`. The templates find the deployer through Bicep's `deployer()`, which works
for a service principal.

## 0. Variables

```bash
az account set --subscription "<subscription name or id>"
SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
TENANT_ID="$(az account show --query tenantId -o tsv)"
RG="rg-pv-pilot"
LOCATION="mexicocentral"
GITHUB_REPO="Eduard0corona/Paquetenvia"
GITHUB_ENVIRONMENT="azure-pilot"
RG_ID="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}"
```

## 1. Register the resource providers

```bash
for ns in Microsoft.App Microsoft.ContainerRegistry Microsoft.DBforPostgreSQL Microsoft.KeyVault \
          Microsoft.Network Microsoft.OperationalInsights Microsoft.Storage Microsoft.Security \
          Microsoft.ManagedIdentity Microsoft.Consumption Microsoft.EventGrid; do
  az provider register --namespace "$ns" --wait
done
```

Defender for Storage malware scanning creates an Event Grid system topic in the resource group. That
is why `Microsoft.EventGrid` is registered.

## 2. Create the resource group (the region is fixed)

```bash
az group create --name "$RG" --location "$LOCATION" \
  --tags project=Paquetenvia environment=PILOT contract=ENV-001 dataClassification=REAL_PEOPLE
```

The workflow refuses any resource group whose location is not `mexicocentral`.

## 3. App registration, service principal and federated credential

```bash
APP_ID="$(az ad app create --display-name "paquetenvia-pilot-deployer" --query appId -o tsv)"
az ad sp create --id "$APP_ID" --output none
SP_OBJECT_ID="$(az ad sp show --id "$APP_ID" --query id -o tsv)"

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

The subject is exactly `repo:Eduard0corona/Paquetenvia:environment:azure-pilot`. Only jobs that
declare `environment: azure-pilot` in this repository can obtain a token. The `azure-dev` environment
and pull requests cannot.

## 4. Custom role: read blob index tags only

The Worker reads the Defender for Storage verdict, which is a blob index tag. No built-in role grants
`tags/read` without also granting `tags/write`, so this custom role is the least privilege. It can be
assigned only inside the pilot resource group.

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
echo "$TAG_READER_ID"
```

## 5. Minimum roles for the deployer (resource group scope only)

- **Contributor on the resource group** covers every resource in the templates:
  - Container Apps, PostgreSQL, Key Vault, Storage, VNet, ACR and Log Analytics;
  - Defender for Storage settings (`Microsoft.Security/defenderForStorageSettings`) and its Event Grid
    system topic;
  - the Cost Management budget (`Microsoft.Consumption/budgets`).
- **Role Based Access Control Administrator** lets the templates assign roles to the workload
  identities. An ABAC condition restricts it to the seven roles the templates use, so the deployer can
  never grant Owner, Contributor, User Access Administrator or Storage Blob Data Owner.

```bash
az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Contributor" --scope "$RG_ID" --output none

# AcrPull, Storage Blob Data Contributor, Storage Blob Delegator, Key Vault Secrets User,
# Key Vault Secrets Officer, Key Vault Crypto Service Encryption User, and the custom tag reader.
ALLOWED="7f951dda-4ed3-4680-a7ca-43fe172d538d, ba92f5b4-2d11-453d-a403-e96b0029c9fe, db58b8e5-c6ad-4a2a-8342-4190687cbf4a, 4633458b-17de-408a-b874-0445c86b69e6, b86a8fe4-44ce-4948-aee5-eccb2c155cd7, e147488a-f6f5-4113-8e2d-b22465e65bf6, ${TAG_READER_ID}"
CONDITION="((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ALLOWED}})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ALLOWED}}))"
az role assignment create --assignee-object-id "$SP_OBJECT_ID" --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope "$RG_ID" \
  --condition "$CONDITION" --condition-version "2.0" --output none

az role assignment list --assignee "$SP_OBJECT_ID" --all -o table
```

The deployer gets *Key Vault Secrets Officer* on the pilot vault from the templates (`security.bicep`).
It never receives a crypto role, so it cannot unwrap PII or Data Protection keys.

### Owner access to the vault (for §6.6 of the README)

The vault uses RBAC and denies public traffic (PILOT-KEYVAULT-PRIVATE-APP-READ). To write the AuthCenter
client secret after the first run creates the vault, the owner needs two things:
- a temporary firewall rule for their own IP (`deploy/azure/pilot/kv-firewall.sh open|close <kv>`,
  see README §6.6);
- a temporary *Key Vault Secrets Officer* role on the vault:

```bash
KV="$(az deployment group show -g "$RG" -n env001-security --query properties.outputs.vaultName.value -o tsv)"
az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --role "Key Vault Secrets Officer" --scope "$(az keyvault show -n "$KV" --query id -o tsv)"
```

This is the only place that uses `signed-in-user`: the owner runs it interactively. Remove the
assignment after writing the secret if you want no standing access.

## 6. GitHub Environment `azure-pilot`

In the GitHub UI: *Settings → Environments → New environment* `azure-pilot`. Then:

- **Required reviewers:** the owner.
- **Deployment branches and tags:** *Selected branches and tags* → `main` only.
- **Environment variables** (variables, not secrets; none of them is sensitive):

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | `$APP_ID` |
| `AZURE_TENANT_ID` | `$TENANT_ID` |
| `AZURE_SUBSCRIPTION_ID` | `$SUBSCRIPTION_ID` |
| `PILOT_RESOURCE_GROUP` | `rg-pv-pilot` |
| `PILOT_BUDGET_START_DATE` | first day of the current or next month, e.g. `2026-10-01` (keep it unchanged afterwards) |
| `PILOT_BUDGET_EMAILS` | optional, comma-separated extra recipients (resource-group Owners are always notified) |
| `PILOT_TRACKING_SUPPORT_URL` | optional `https://` support link for public tracking (default `https://paquetenvia.com`) |
| `PILOT_GATE_007_DECISION` | ID of the decision-log row `GATE-007-...` whose Type is `Gate resolution` or `Gate scoping` (privacy). Not set until the owner records it; `GATE-007-PRIVACY-DRAFT` is rejected. |
| `PILOT_GATE_012_DECISION` | `GATE-012-PILOT-SCOPE` (Type `Gate scoping`, cloud and data residency) |

Or with the GitHub CLI (`gh auth login` first):

```bash
gh api --method PUT "repos/${GITHUB_REPO}/environments/${GITHUB_ENVIRONMENT}" \
  -F "deployment_branch_policy[protected_branches]=false" -F "deployment_branch_policy[custom_branch_policies]=true"
gh api --method POST "repos/${GITHUB_REPO}/environments/${GITHUB_ENVIRONMENT}/deployment-branch-policies" -f name=main -f type=branch
for pair in "AZURE_CLIENT_ID=$APP_ID" "AZURE_TENANT_ID=$TENANT_ID" "AZURE_SUBSCRIPTION_ID=$SUBSCRIPTION_ID" \
            "PILOT_RESOURCE_GROUP=$RG" "PILOT_BUDGET_START_DATE=2026-10-01"; do
  gh variable set "${pair%%=*}" --env "$GITHUB_ENVIRONMENT" --repo "$GITHUB_REPO" --body "${pair#*=}"
done
# Required reviewers need your user id:
# gh api --method PUT "repos/${GITHUB_REPO}/environments/${GITHUB_ENVIRONMENT}" -F "reviewers[][type]=User" -F "reviewers[][id]=<your numeric user id>"
```

## 7. Check the bootstrap

```bash
az group show -n "$RG" --query location -o tsv                      # mexicocentral
az ad app federated-credential list --id "$APP_ID" --query "[].subject" -o tsv
az role assignment list --assignee "$SP_OBJECT_ID" --all --query "[].{role:roleDefinitionName,scope:scope,condition:condition!=null}" -o table
```

Then follow the README §6.1 from step 2.

## Undo

```bash
az role assignment delete --assignee "$SP_OBJECT_ID" --scope "$RG_ID"
az ad app delete --id "$APP_ID"
az role definition delete --name "Paquetenvia Pilot Blob Tag Reader" --scope "$RG_ID"
```
