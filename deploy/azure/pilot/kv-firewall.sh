#!/usr/bin/env bash
# PILOT-KEYVAULT-PRIVATE-APP-READ: the pilot Key Vault denies public traffic (defaultAction Deny, bypass None);
# only the Container Apps subnet reaches it. An operator machine (the GitHub runner, or the owner's Cloud Shell
# during a restore drill) that must write or read secrets opens a temporary /32 rule for its own public IP and
# always closes it afterwards.
#
#   kv-firewall.sh open  <vault-name>   add this machine's IP and wait until the data plane answers
#   kv-firewall.sh close <vault-name>   remove this machine's IP (never fails: used from always() cleanup)
#
# Redeploying security.bicep also resets ipRules to empty, so a leaked rule cannot outlive the next deploy.
set -euo pipefail

action="${1:-}"
vault="${2:-}"
[[ "${action}" == "open" || "${action}" == "close" ]] || { echo "usage: $0 open|close <vault-name>" >&2; exit 2; }
# close runs from always() cleanup, possibly before the vault exists: nothing to close then.
if [[ "${action}" == "close" && -z "${vault}" ]]; then echo "No vault yet; nothing to close."; exit 0; fi
[[ "${vault}" =~ ^[A-Za-z0-9-]{3,24}$ ]] || { echo "invalid vault name" >&2; exit 2; }

public_ip() {
  local ip
  ip="$(curl -fsS --max-time 10 https://api.ipify.org || true)"
  [[ "${ip}" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || return 1
  printf '%s' "${ip}"
}

if [[ "${action}" == "open" ]]; then
  ip="$(public_ip)" || { echo "::error::cannot determine this machine's public IPv4 address"; exit 1; }
  az keyvault network-rule add --name "${vault}" --ip-address "${ip}/32" --only-show-errors --output none
  # Firewall changes take a short while to apply; probe the data plane with a bounded retry.
  for attempt in $(seq 1 20); do
    if az keyvault secret list --vault-name "${vault}" --query "length(@)" -o tsv >/dev/null 2>&1; then
      echo "Key Vault ${vault}: temporary rule for this machine is active."
      exit 0
    fi
    sleep 15
  done
  echo "::error::Key Vault ${vault} data plane not reachable 5 minutes after opening the temporary rule"
  exit 1
fi

# close: best effort, never fails the caller.
ip="$(public_ip)" || { echo "::warning::cannot determine the public IP to close; the next deploy of security.bicep resets ipRules"; exit 0; }
if az keyvault network-rule remove --name "${vault}" --ip-address "${ip}/32" --only-show-errors --output none 2>/dev/null; then
  echo "Key Vault ${vault}: temporary rule removed."
else
  echo "::warning::could not remove the temporary Key Vault rule; the next deploy of security.bicep resets ipRules"
fi
exit 0
