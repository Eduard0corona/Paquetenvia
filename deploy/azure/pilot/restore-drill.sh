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
MIGRATE_IDENTITY_ID="$(az identity show -g "${RG}" -n id-pv-pilot-migrate --query id -o tsv)"
MIGRATE_PRINCIPAL="$(az identity show -g "${RG}" -n id-pv-pilot-migrate --query principalId -o tsv)"
VAULT_URI="$(az keyvault show -n "${VAULT}" --query properties.vaultUri -o tsv)"

started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "restore_drill_started=${started} source=${SOURCE} target=${DRILL} restore_point=${RESTORE_TIME}"

# 1. Point-in-time restore into a new server. A restore of a private-access server keeps the source's
#    delegated subnet and private DNS zone, so it stays unreachable from the internet.
az postgres flexible-server restore --resource-group "${RG}" --name "${DRILL}" \
  --source-server "${SOURCE}" --restore-time "${RESTORE_TIME}" --only-show-errors --output none
restored="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
DRILL_HOST="$(az postgres flexible-server show -g "${RG}" -n "${DRILL}" --query fullyQualifiedDomainName -o tsv)"
echo "restore_completed=${restored} host=${DRILL_HOST}"

# 2. Connection to the restored server (same administrator credential as at the restore point).
umask 077
tmp="$(mktemp)"
trap 'shred -u "${tmp}" 2>/dev/null || true' EXIT
admin="$(az keyvault secret show --vault-name "${VAULT}" --name pg-admin-password --query value -o tsv)"
printf 'Host=%s;Database=paqueteria;Username=%s;Password=%s;Maximum Pool Size=2;Minimum Pool Size=0;SSL Mode=VerifyFull;Timeout=15;Command Timeout=120' \
  "${DRILL_HOST}" "${ADMIN_LOGIN}" "${admin}" > "${tmp}"
unset admin
az keyvault secret set --vault-name "${VAULT}" --name "${SECRET}" --file "${tmp}" --encoding utf-8 --only-show-errors --output none
secret_id="$(az keyvault secret show --vault-name "${VAULT}" --name "${SECRET}" --query id -o tsv | sed 's#/[^/]*$##')"
az role assignment create --assignee-object-id "${MIGRATE_PRINCIPAL}" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$(az keyvault show -n "${VAULT}" --query id -o tsv)/secrets/${SECRET}" \
  --only-show-errors --output none
sleep 60

restore_job_secret() {
  az containerapp job secret set -g "${RG}" -n "${JOB}" --only-show-errors --output none \
    --secrets "pg-verify-conn=keyvaultref:${VAULT_URI}secrets/pg-migrate-connection,identityref:${MIGRATE_IDENTITY_ID}"
}
trap 'restore_job_secret; shred -u "${tmp}" 2>/dev/null || true' EXIT

# 3. Point the read-only verify job at the restored server and run `assert` (baseline + every module lane).
az containerapp job secret set -g "${RG}" -n "${JOB}" --only-show-errors --output none \
  --secrets "pg-verify-conn=keyvaultref:${secret_id},identityref:${MIGRATE_IDENTITY_ID}"
execution="$(az containerapp job start -g "${RG}" -n "${JOB}" --query name -o tsv)"
status="Running"
for _ in $(seq 1 60); do
  status="$(az containerapp job execution show -g "${RG}" -n "${JOB}" --job-execution-name "${execution}" --query properties.status -o tsv)"
  [[ "${status}" == "Running" || "${status}" == "Processing" ]] || break
  sleep 15
done
finished="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "verify_execution=${execution} status=${status} finished=${finished}"

# 4. Clean up: the job points back at the pilot database (trap), the drill secret is disabled (the vault has
#    purge protection, so it is disabled rather than deleted and the next drill adds a new version) and,
#    unless --keep, the restored server is deleted.
az keyvault secret set-attributes --vault-name "${VAULT}" --name "${SECRET}" --enabled false --only-show-errors --output none || true
if [[ "${KEEP}" == 0 ]]; then
  az postgres flexible-server delete -g "${RG}" -n "${DRILL}" --yes --only-show-errors --output none
  echo "drill_server_deleted=${DRILL}"
fi

echo "EVIDENCE restore_point=${RESTORE_TIME} started=${started} restored=${restored} verified=${finished} verify_status=${status} execution=${execution}"
[[ "${status}" == "Succeeded" ]]
