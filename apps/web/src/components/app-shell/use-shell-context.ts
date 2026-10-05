"use client";

import { useCallback, useEffect, useState } from "react";
import { isSessionBootstrapSettled, sessionBootstrapSettledEvent } from "../../auth/bootstrap-state";
import { readSessionAccount, type SessionAccount } from "../../auth/session-account";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createOperationsApi } from "../../operations/api/operations-api";
import type { OperationsOrganizationContext } from "../../operations/contracts/operations-dashboard";
import { resolveActiveRole } from "../../operations/contracts/capabilities";
import {
  readOperationsSession,
  subscribeToOperationsSession,
  type OperationsSession,
} from "../../operations/session/operations-session";

export type ShellContext =
  | { readonly status: "loading" }
  | { readonly status: "no_session" }
  | {
      readonly status: "ready";
      readonly session: OperationsSession;
      readonly account: SessionAccount | null;
      readonly contexts: readonly OperationsOrganizationContext[];
      /** Organization contexts could not be read; the nav stays neutral. */
      readonly contextsUnavailable: boolean;
      readonly role: string | null;
    };

/**
 * What the shell knows about the signed-in person: the installed operations session, the
 * organization contexts (GET /me/organization-contexts, the same call every screen makes to
 * resolve its role) and the role in the selected organization. It follows the
 * session-changed event, so a sign-in, an organization switch or a logout re-reads it.
 */
export function useShellContext(): ShellContext {
  const [context, setContext] = useState<ShellContext>({ status: "loading" });

  const load = useCallback((signal: AbortSignal) => {
    const session = readOperationsSession();
    if (session === null) {
      // The BFF bootstrap installs the session after the first render; until it settles
      // the shell shows a neutral skeleton instead of the signed-out state.
      setContext({ status: isSessionBootstrapSettled() ? "no_session" : "loading" });
      return;
    }
    setContext({ status: "loading" });
    void createOperationsApi(clientApiBaseUrl(), session)
      .organizationContexts(signal)
      .then(
        (contexts) => ({ contexts, contextsUnavailable: false }),
        () => ({ contexts: [] as readonly OperationsOrganizationContext[], contextsUnavailable: true }),
      )
      .then(({ contexts, contextsUnavailable }) => {
        if (signal.aborted || readOperationsSession() !== session) return;
        setContext({
          status: "ready",
          session,
          account: readSessionAccount(),
          contexts,
          contextsUnavailable,
          role: resolveActiveRole(contexts, session.organizationId),
        });
      });
  }, []);

  useEffect(() => {
    let controller = new AbortController();
    const reload = () => {
      controller.abort();
      controller = new AbortController();
      load(controller.signal);
    };
    const initial = window.setTimeout(() => load(controller.signal), 0);
    const unsubscribe = subscribeToOperationsSession(reload);
    window.addEventListener(sessionBootstrapSettledEvent, reload);
    return () => {
      clearTimeout(initial);
      controller.abort();
      unsubscribe();
      window.removeEventListener(sessionBootstrapSettledEvent, reload);
    };
  }, [load]);

  return context;
}
