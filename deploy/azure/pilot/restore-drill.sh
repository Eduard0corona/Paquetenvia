#!/usr/bin/env bash
# ENV-001 restore drill: point-in-time restore of the pilot PostgreSQL server to a NEW server in the same
# private network, then the canonical migrator `assert` (read-only) runs against it from inside the VNet.
# The pilot database itself is never modified. Run by the owner in Azure Cloud Shell (bash); see
# docs/operations/env-001-pilot/README.md "Restore and restore drill".
#
#   ./restore-drill.sh --resource-group rg-pv-pilot --restore-time 2026-10-05T13:10:00Z [--keep]
#
# Prints only resource names, timestamps and job status; never a password or a connection string.
set -euo pipefail

usage() { echo "usage: $0 --resource-group NAME --restore-time YYYY-MM-DDTHH:MM:SSZ [--keep]" >&2; exit 2; }
RG="" RESTORE_TIME="" KEEP=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group) RG="${2:-}"; shift 2 ;;
    --restore-time) RESTORE_TIME="${2:-}"; shift 2 ;;
    --keep) KEEP=1; shift ;;
    *) usage ;;
  esac
done
[[ "${RG}" =~ ^[A-Za-z0-9._-]{1,90}$ ]] || usage
[[ "${RESTORE_TIME}" =~ ^20[0-9]{2}-[01][0-9]-[0-3][0-9]T[0-2][0-9]:[0-5][0-9]:[0-5][0-9]Z$ ]] || usage

security="$(az deployment group show -g "${RG}" -n env001-security --query properties.outputs -o json)"
platform="$(az deployment group show -g "${RG}" -n env001-platform --query properties.outputs -o json)"
VAULT="$(jq -r .vaultName.value <<<"${security}")"
SOURCE="$(jq -r .postgresName.value <<<"${platform}")"
ADMIN_LOGIN="$(jq -r .postgresAdministratorLogin.value <<<"${platform}")"
DRILL="${SOURCE}-drill-$(date -u +%Y%m%d%H%M)"
DRILL="${DRILL:0:63}"
JOB="job-pv-pilot-verify"
SECRET="pg-restore-drill-connection"
MIGRATE_PRINCIPAL="$(az identity show -g "${RG}" -n id-pv-pilot-migrate --query principalId -o tsv)"

FIREWALL="$(dirname "$0")/kv-firewall.sh"
SECRET_SCOPE=""
tmp=""
drill_requested=0

# Cleanup on every exit, success or failure, each step best-effort:
# - point the verify job's Key Vault mapping back at the pilot migration connection;
# - revoke the temporary Key Vault Secrets User assignment on the drill secret and disable the secret
#   (the vault has purge protection, so it is disabled rather than deleted; the next drill adds a version);
# - close this machine's temporary Key Vault firewall rule and wipe the local temporary file;
# - unless --keep, delete the restored server. It is a full copy of the pilot database with real data and
#   lives outside the Bicep-managed resource set, so it must never outlive a failed run.
cleanup() {
  local rc=$?
  az containerapp job update -g "${RG}" -n "${JOB}" --only-show-errors --output none \
    --set-env-vars "KeyVaultSecrets__Mappings__0__SecretName=pg-migrate-connection" || true
  if [[ -n "${SECRET_SCOPE}" ]]; then
    az role assignment delete --assignee "${MIGRATE_PRINCIPAL}" --role "Key Vault Secrets User" \
      --scope "${SECRET_SCOPE}" --only-show-errors --output none || true
  fi
  # Unconditional: `kv-firewall.sh open` can fail after adding the rule, and `close` is idempotent.
  az keyvault secret set-attributes --vault-name "${VAULT}" --name "${SECRET}" --enabled false \
    --only-show-errors --output none 2>/dev/null || true
  bash "${FIREWALL}" close "${VAULT}" || true
  if [[ -n "${tmp}" ]]; then shred -u "${tmp}" 2>/dev/null || true; fi
  if [[ "${drill_requested}" == 1 && "${KEEP}" == 0 ]]; then
    if az postgres flexible-server delete -g "${RG}" -n "${DRILL}" --yes --only-show-errors --output none; then
      echo "drill_server_deleted=${DRILL}"
    else
      echo "WARNING drill_server_not_deleted=${DRILL} delete it manually: it holds a copy of the pilot data" >&2
    fi
  fi
  exit "${rc}"
}
trap cleanup EXIT

started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "restore_drill_started=${started} source=${SOURCE} target=${DRILL} restore_point=${RESTORE_TIME}"

# 1. Point-in-time restore into a new server. A restore of a private-access server keeps the source's
#    delegated subnet and private DNS zone, so it stays unreachable from the internet. From here on the
#    cleanup deletes the server (unless --keep), even if the restore itself fails half-way.
drill_requested=1
az postgres flexible-server restore --resource-group "${RG}" --name "${DRILL}" \
  --source-server "${SOURCE}" --restore-time "${RESTORE_TIME}" --only-show-errors --output none
restored="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
DRILL_HOST="$(az postgres flexible-server show -g "${RG}" -n "${DRILL}" --query fullyQualifiedDomainName -o tsv)"
echo "restore_completed=${restored} host=${DRILL_HOST}"

# 2. Connection to the restored server (same administrator credential as at the restore point).
#    PILOT-KEYVAULT-PRIVATE-APP-READ: the vault denies public traffic, so this machine opens a temporary
#    /32 rule for itself (kv-firewall.sh) and the cleanup always closes it.
umask 077
tmp="$(mktemp)"
SECRET_SCOPE="$(az keyvault show -n "${VAULT}" --query id -o tsv)/secrets/${SECRET}"

bash "${FIREWALL}" open "${VAULT}"
admin="$(az keyvault secret show --vault-name "${VAULT}" --name pg-admin-password --query value -o tsv)"
printf 'Host=%s;Database=paqueteria;Username=%s;Password=%s;Maximum Pool Size=2;Minimum Pool Size=0;SSL Mode=VerifyFull;Timeout=15;Command Timeout=120' \
  "${DRILL_HOST}" "${ADMIN_LOGIN}" "${admin}" > "${tmp}"
unset admin
az keyvault secret set --vault-name "${VAULT}" --name "${SECRET}" --file "${tmp}" --encoding utf-8 --only-show-errors --output none
az role assignment create --assignee-object-id "${MIGRATE_PRINCIPAL}" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "${SECRET_SCOPE}" \
  --only-show-errors --output none
sleep 60

# 3. Point the read-only verify job at the restored server (its Key Vault mapping 0 names the drill secret;
#    the job reads it itself at start) and run `assert` (baseline + every module lane).
az containerapp job update -g "${RG}" -n "${JOB}" --only-show-errors --output none \
  --set-env-vars "KeyVaultSecrets__Mappings__0__SecretName=${SECRET}"
execution="$(az containerapp job start -g "${RG}" -n "${JOB}" --query name -o tsv)"
status="Running"
for _ in $(seq 1 60); do
  status="$(az containerapp job execution show -g "${RG}" -n "${JOB}" --job-execution-name "${execution}" --query properties.status -o tsv)"
  [[ "${status}" == "Running" || "${status}" == "Processing" ]] || break
  sleep 15
done
finished="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "verify_execution=${execution} status=${status} finished=${finished}"

# 4. The cleanup trap points the job back at the pilot database, disables the drill secret, closes the
#    firewall and, unless --keep, deletes the restored server.
echo "EVIDENCE restore_point=${RESTORE_TIME} started=${started} restored=${restored} verified=${finished} verify_status=${status} execution=${execution}"
[[ "${status}" == "Succeeded" ]]
