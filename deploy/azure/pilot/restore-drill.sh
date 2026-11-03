#!/usr/bin/env bash
# ENV-001 restore drill: point-in-time restore of the pilot PostgreSQL server to a NEW server in the same
# private network, then the canonical migrator `assert` (read-only) runs against it from inside the VNet.
# The pilot database itself is never modified. Run by the owner in Azure Cloud Shell (bash); see
# docs/operations/env-001-pilot/README.md "Restore and restore drill".
#
#   ./restore-drill.sh --resource-group rg-pv-pilot --restore-time 2026-10-05T13:10:00Z [--keep | --real]
#
# OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20: the source is the server in service, the postgresName output of the last
# deployment (it follows the azure-pilot variable PILOT_POSTGRES_SERVER_NAME). New servers are named after its base
# name, the first server's (never after the whole source name, so names do not grow): a drill <base>-drill-<UTC minute>;
# with --real (README §6.4 "Real restore") <base>-r<UTC minute>, kept only when `assert` succeeds, ready to be set as
# PILOT_POSTGRES_SERVER_NAME. A restore always creates a new server; the source is never changed or deleted here.
#
# REL-001 restore vigente (Z9; Z18, project owner: "Exacto, en el job (Recommended)"): a drill (never --real) also
# compares the row counts of organizations, orders, order events and pickup and delivery photos of the restored server
# with the server in service. Only that drill execution runs `assert --compare-row-counts-env`, with one more Key Vault
# mapping: the verify job's own `pg-migrate-connection`, the connection it holds every day, so nothing reads with more
# privilege. Both reads are read-only and any difference fails `assert`. The job itself is never given that mapping
# or argument (`az containerapp job start --yaml` with the job's own template), and its one changed setting, mapping 0,
# is put back by the cleanup on every exit. The EVIDENCE line names the commit `assert` ran: the locked
# <tested_git_sha> tag of the verify job's db-ops image digest.
#
# Prints only resource names, timestamps, job status and counts; never a password, a connection string or a value.
set -euo pipefail

usage() { echo "usage: $0 --resource-group NAME --restore-time YYYY-MM-DDTHH:MM:SSZ [--keep | --real]" >&2; exit 2; }
RG="" RESTORE_TIME="" KEEP=0 REAL=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group) RG="${2:-}"; shift 2 ;;
    --restore-time) RESTORE_TIME="${2:-}"; shift 2 ;;
    --keep) KEEP=1; shift ;;
    --real) REAL=1; shift ;;
    *) usage ;;
  esac
done
[[ "${RG}" =~ ^[A-Za-z0-9._-]{1,90}$ ]] || usage
[[ "${RESTORE_TIME}" =~ ^20[0-9]{2}-[01][0-9]-[0-3][0-9]T[0-2][0-9]:[0-5][0-9]:[0-5][0-9]Z$ ]] || usage
[[ "${KEEP}" == 0 || "${REAL}" == 0 ]] || usage

security="$(az deployment group show -g "${RG}" -n env001-security --query properties.outputs -o json)"
platform="$(az deployment group show -g "${RG}" -n env001-platform --query properties.outputs -o json)"
VAULT="$(jq -r .vaultName.value <<<"${security}")"
SOURCE="$(jq -r .postgresName.value <<<"${platform}")"
ADMIN_LOGIN="$(jq -r .postgresAdministratorLogin.value <<<"${platform}")"
# Same format as tools/azr-001/env001_pilot_guards.py postgres-server-name; group 1 is the base name.
if [[ ! "${SOURCE}" =~ ^(pg-pv-pilot-[a-z0-9]{13})(-[a-z0-9]+)*$ || ${#SOURCE} -gt 63 ]]; then
  echo "env001-platform has no pilot PostgreSQL server name in postgresName; run the deployment first" >&2
  exit 1
fi
BASE="${BASH_REMATCH[1]}"
if [[ "${REAL}" == 1 ]]; then
  DRILL="${BASE}-r$(date -u +%Y%m%d%H%M)"
else
  DRILL="${BASE}-drill-$(date -u +%Y%m%d%H%M)"
fi
# The cleanup may delete the target, so it must not be a server that already exists (a second run in the same minute).
if az postgres flexible-server show -g "${RG}" -n "${DRILL}" --only-show-errors --output none 2>/dev/null; then
  echo "server ${DRILL} already exists; wait a minute and run again" >&2
  exit 1
fi
JOB="job-pv-pilot-verify"
SECRET="pg-restore-drill-connection"
SOURCE_SETTING="PAQUETERIA_ROW_COUNT_SOURCE_CONNECTION"
ASSERT_ARGS='["assert","--connection-env","PAQUETERIA_MIGRATION_CONNECTION"]'
MIGRATE_PRINCIPAL="$(az identity show -g "${RG}" -n id-pv-pilot-migrate --query principalId -o tsv)"

# The verify job must be exactly as the deployment leaves it (one container running `assert`, mapping 0 on
# pg-migrate-connection, no row-count source), so the cleanup's reset restores it exactly.
deployed_template="$(az containerapp job show -g "${RG}" -n "${JOB}" --query properties.template -o json)"
if ! jq -e --argjson args "${ASSERT_ARGS}" --arg source "${SOURCE_SETTING}" '
    (.containers | length) == 1 and .containers[0].args == $args and
    ([.containers[0].env[] | select(.name == "KeyVaultSecrets__Mappings__0__SecretName") | .value] == ["pg-migrate-connection"]) and
    ([.containers[0].env[] | select(.value == $source)] | length) == 0' <<<"${deployed_template}" >/dev/null; then
  echo "${JOB} is not in its deployed state; run Deploy Azure PILOT, then the drill again" >&2
  exit 1
fi

FIREWALL="$(dirname "$0")/kv-firewall.sh"
SECRET_SCOPE=""
tmp=""
override=""
drill_requested=0

# Cleanup on every exit, success or failure, each step best-effort:
# - point the verify job's Key Vault mapping back at the pilot migration connection;
# - revoke the temporary Key Vault Secrets User assignment on the drill secret and disable the secret
#   (the vault has purge protection, so it is disabled rather than deleted; the next drill adds a version);
# - close this machine's temporary Key Vault firewall rule and wipe the local temporary file;
# - unless --keep, delete the restored server. It is a full copy of the pilot database with real data and
#   lives outside the Bicep-managed resource set, so it must never outlive a failed run. With --real it is kept
#   only when the run succeeds (it is not in service until the owner sets PILOT_POSTGRES_SERVER_NAME).
cleanup() {
  local rc=$?
  if [[ "${REAL}" == 1 && "${rc}" == 0 ]]; then KEEP=1; fi
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
  if [[ -n "${override}" ]]; then rm -f "${override}" || true; fi
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

# 1. Point-in-time restore into a new server. Without --vnet, --subnet or --private-dns-zone the Azure CLI gives the
#    new server the source's network (its delegated subnet and private DNS zone), so it stays unreachable from the
#    internet and platform.bicep can adopt it in place after a real restore. From here on the cleanup deletes the
#    server (unless --keep, or --real and success), even if the restore itself fails half-way.
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
template="$(az containerapp job show -g "${RG}" -n "${JOB}" --query properties.template -o json)"

# The commit `assert` runs: the db-ops image is pinned by digest, and the deployment locks its <tested_git_sha> tag.
asserted_git_sha() {
  local image registry repository digest tags sha lock
  image="$(jq -r '.containers[0].image' <<<"${template}")"
  [[ "${image}" =~ ^([a-z0-9]{5,50})\.azurecr\.io/(paquetenvia-db-ops)@(sha256:[0-9a-f]{64})$ ]] || { echo UNVERIFIED; return; }
  registry="${BASH_REMATCH[1]}" repository="${BASH_REMATCH[2]}" digest="${BASH_REMATCH[3]}"
  tags="$(az acr repository show --name "${registry}" --image "${repository}@${digest}" --only-show-errors --output json)" \
    || { echo UNVERIFIED; return; }
  sha="$(jq -r '[.tags[]? | select(test("^[0-9a-f]{40}$"))] | if length == 1 then .[0] else "" end' <<<"${tags}")"
  [[ -n "${sha}" ]] || { echo UNVERIFIED; return; }
  lock="$(az acr repository show --name "${registry}" --image "${repository}:${sha}" --only-show-errors --output json)" \
    || { echo UNVERIFIED; return; }
  if jq -e --arg digest "${digest}" '.digest == $digest and .changeableAttributes.writeEnabled == false' <<<"${lock}" >/dev/null; then
    echo "${sha}"
  else
    echo UNVERIFIED
  fi
}
ASSERTED="$(asserted_git_sha)"
if [[ "${ASSERTED}" == UNVERIFIED ]]; then
  echo "WARNING asserted_git_sha=UNVERIFIED: the verify job's image digest has no locked commit tag" >&2
fi

if [[ "${REAL}" == 1 ]]; then
  # A real restore follows a loss: the server in service may differ on purpose, so counts are not compared.
  ROW_COUNTS="NOT_COMPARED"
  execution="$(az containerapp job start -g "${RG}" -n "${JOB}" --query name -o tsv)"
else
  # This execution only: the job's own template with the drill secret in mapping 0, plus the next mapping for the
  # job's everyday pg-migrate-connection (the server in service) and the argument that compares the counts.
  override="$(mktemp)"
  jq --argjson args "${ASSERT_ARGS}" --arg secret "${SECRET}" --arg source "${SOURCE_SETTING}" '
      .containers[0] as $c
      | if (.containers | length) != 1 or $c.args != $args or
           ([$c.env[] | select(.name == "KeyVaultSecrets__Mappings__0__SecretName") | .value] != [$secret])
        then error("verify job template drifted") else . end
      | ([$c.env[].name | capture("^KeyVaultSecrets__Mappings__(?<i>[0-9]+)__").i | tonumber] | max + 1) as $n
      | {containers: [$c
          | .args += ["--compare-row-counts-env", $source]
          | .env += [{name: "KeyVaultSecrets__Mappings__\($n)__SecretName", value: "pg-migrate-connection"},
                     {name: "KeyVaultSecrets__Mappings__\($n)__ConfigurationKey", value: $source}]]}' \
    <<<"${template}" > "${override}"
  ROW_COUNTS="UNVERIFIED"
  execution="$(az containerapp job start -g "${RG}" -n "${JOB}" --yaml "${override}" --query name -o tsv)"
  rm -f "${override}"
  override=""
fi
status="Running"
for _ in $(seq 1 60); do
  status="$(az containerapp job execution show -g "${RG}" -n "${JOB}" --job-execution-name "${execution}" --query properties.status -o tsv)"
  [[ "${status}" == "Running" || "${status}" == "Processing" ]] || break
  sleep 15
done
finished="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "verify_execution=${execution} status=${status} finished=${finished}"
# `assert --compare-row-counts-env` succeeds only when the four counts match on both servers.
if [[ "${REAL}" == 0 && "${status}" == "Succeeded" ]]; then ROW_COUNTS="MATCHED"; fi

# 4. The cleanup trap points the job back at the pilot database, disables the drill secret, closes the
#    firewall and, unless --keep (or --real and success), deletes the restored server.
echo "EVIDENCE restore_point=${RESTORE_TIME} started=${started} restored=${restored} verified=${finished} verify_status=${status} row_counts=${ROW_COUNTS} asserted_git_sha=${ASSERTED} execution=${execution}"
[[ "${status}" == "Succeeded" ]]
if [[ "${REAL}" == 1 ]]; then
  echo "restored_server_kept=${DRILL} source_unchanged=${SOURCE}"
  echo "next_step=set the azure-pilot variable PILOT_POSTGRES_SERVER_NAME to ${DRILL}, then run Deploy Azure PILOT (README section 6.4)"
fi
