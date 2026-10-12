"use client";

import Link from "next/link";
import { useState } from "react";
import {
  localProfiles,
  resolveLocalProfile,
} from "../../dev/dev-portal-policy";

function clearLocalSession() {
  delete window.__paquetenviaOperationsSession;
  delete window.__paquetenviaDriverSession;
  window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
  window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
}

export function DevPortal() {
  const [selected, setSelected] = useState<keyof typeof localProfiles>("dispatcher");
  const [active, setActive] = useState<string>("No local profile selected");

  function activate() {
    const profile = resolveLocalProfile(selected);
    if (!profile) return;
    clearLocalSession();

    if (selected === "dispatcher") {
      window.__paquetenviaOperationsSession = {
        organizationId: profile.organizationId,
        sessionNamespace: profile.namespace,
        getAccessToken: async () => profile.credential,
      };
      window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
    } else {
      window.__paquetenviaDriverSession = {
        organizationId: profile.organizationId,
        cacheNamespace: profile.namespace,
        getAccessToken: async () => profile.credential,
      };
      window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
    }

    setActive(profile.label);
  }

  function clear() {
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
          <button type="button" onClick={activate}>Activate profile</button>
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
