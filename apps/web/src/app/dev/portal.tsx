"use client";

import Link from "next/link";
import { useRef, useState } from "react";
import {
  localProfiles,
  resolveLocalProfile,
} from "../../dev/dev-portal-policy";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createOperationsApi } from "../../operations/api/operations-api";

function clearLocalSession() {
  delete window.__paquetenviaOperationsSession;
  delete window.__paquetenviaDriverSession;
  window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
  window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
}

type LocalProfile = (typeof localProfiles)[keyof typeof localProfiles];

/**
 * ORG-CONTEXT-ROLES-2026-10-12: the profile's roles in its organization, read
 * from the API's own GET /me/organization-contexts answer as a BFF sign-in
 * reads them. Null (unknown) when the API cannot answer; the driver app then
 * does not offer "No se pudo entregar".
 */
async function readOrganizationRoles(
  profile: LocalProfile,
): Promise<readonly string[] | null> {
  try {
    const contexts = await createOperationsApi(clientApiBaseUrl(), {
      organizationId: profile.organizationId,
      sessionNamespace: profile.namespace,
      getAccessToken: async () => profile.credential,
    }).organizationContexts();
    return (
      contexts.find((context) => context.organization_id === profile.organizationId)
        ?.roles ?? null
    );
  } catch {
    return null;
  }
}

export function DevPortal() {
  const [selected, setSelected] = useState<keyof typeof localProfiles>("dispatcher");
  const [active, setActive] = useState<string>("No local profile selected");
  // Only the latest click installs a session.
  const activation = useRef(0);

  async function activate() {
    const profile = resolveLocalProfile(selected);
    if (!profile) return;
    const current = ++activation.current;
    clearLocalSession();

    if (selected === "dispatcher") {
      window.__paquetenviaOperationsSession = {
        organizationId: profile.organizationId,
        sessionNamespace: profile.namespace,
        getAccessToken: async () => profile.credential,
      };
      window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
    } else {
      const organizationRoles = await readOrganizationRoles(profile);
      if (current !== activation.current) return;
      window.__paquetenviaDriverSession = {
        organizationId: profile.organizationId,
        cacheNamespace: profile.namespace,
        getAccessToken: async () => profile.credential,
        organizationRoles,
      };
      window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
    }

    setActive(profile.label);
  }

  function clear() {
    activation.current += 1;
    clearLocalSession();
    setActive("No local profile selected");
  }

  return (
    <main className="devShell">
      <section className="devCard">
        <p className="eyebrow">Local development only</p>
        <h1>Manual testing</h1>
        <p>
          Select an allowlisted synthetic identity. Switching identities clears
          both runtime sessions; no token is logged or persisted here.
        </p>
        <label className="devLabel" htmlFor="local-profile">Profile</label>
        <select
          id="local-profile"
          value={selected}
          onChange={(event) => setSelected(event.target.value as keyof typeof localProfiles)}
        >
          {Object.entries(localProfiles).map(([key, profile]) => (
            <option key={key} value={key}>{profile.label}</option>
          ))}
        </select>
        <div className="devActions">
          <button type="button" onClick={() => void activate()}>Activate profile</button>
          <button type="button" className="devSecondary" onClick={clear}>Clear</button>
        </div>
        <p className="devStatus" aria-live="polite">{active}</p>
        <nav className="devLinks" aria-label="Local manual testing destinations">
          <Link href="/ops/dashboard">Operations dashboard</Link>
          <Link href="/driver/stops">Driver PWA</Link>
          <Link href="/health">Web health</Link>
        </nav>
      </section>
    </main>
  );
}
