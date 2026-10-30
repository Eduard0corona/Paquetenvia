"use client";

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { RateLimitNoticeView } from "../../components/ui/rate-limit-notice";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createRecipientUnsubscribeApi, type RecipientUnsubscribeApi } from "./unsubscribe-api";
import { parseRecipientUnsubscribePathname } from "./unsubscribe-route";
import { recipientUnsubscribeTexts } from "./unsubscribe-texts";

/** Where the page is: before the press, sending, or the one outcome of the last press. */
export type RecipientUnsubscribePhase = "ready" | "sending" | "recorded" | "invalid" | "wait" | "failed";

/**
 * The page as the recipient sees it in each phase (AI-07 /avisos/baja/:token): the neutral heading, the one button
 * while an unsubscribe can still be sent, and one es-MX message for success, an invalid link, a wait (counting down,
 * the button disabled until it ends) and a failure. Opening the page sends nothing.
 */
export function RecipientUnsubscribeView({
  phase,
  secondsLeft,
  onUnsubscribe,
}: {
  readonly phase: RecipientUnsubscribePhase;
  readonly secondsLeft: number;
  readonly onUnsubscribe: () => void;
}) {
  const showsButton = phase === "ready" || phase === "sending" || phase === "wait" || phase === "failed";
  return (
    <main className="trackingShell">
      <section className="trackingCard" aria-busy={phase === "sending"}>
        <h1 className="trackingHeading">{recipientUnsubscribeTexts.heading}</h1>
        <div aria-live="polite">
          {phase === "recorded" ? <p role="status">{recipientUnsubscribeTexts.success}</p> : null}
          {phase === "invalid" ? <p role="status">{recipientUnsubscribeTexts.invalidLink}</p> : null}
          {phase === "failed" ? <p role="status">{recipientUnsubscribeTexts.failure}</p> : null}
        </div>
        {phase === "wait" ? <RateLimitNoticeView secondsLeft={secondsLeft} /> : null}
        {showsButton ? (
          <button
            type="button"
            onClick={onUnsubscribe}
            disabled={phase === "sending" || (phase === "wait" && secondsLeft > 0)}
          >
            {recipientUnsubscribeTexts.button}
          </button>
        ) : null}
      </section>
    </main>
  );
}

/**
 * NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-07 /avisos/baja/:token, O9 texts): reads the token from the address the
 * browser opened (the /avisos/baja/:token rewrite serves one generic page, so no token reaches the server render or a
 * log), and sends unsubscribeRecipientNotices once per press of the button. No session, cookie, storage or manifest.
 */
export function RecipientUnsubscribeShell({ api }: { readonly api?: RecipientUnsubscribeApi } = {}) {
  // The server render (and hydration) sees no address: undefined until the browser's own pathname is read.
  const pathname = useSyncExternalStore(subscribeToNothing, browserPathname, serverPathname);
  const route = pathname === null ? undefined : parseRecipientUnsubscribePathname(pathname);
  const token = route === undefined ? undefined : route.kind === "unsubscribe" ? route.token : null;
  const [phase, setPhase] = useState<RecipientUnsubscribePhase>("ready");
  const [waitUntil, setWaitUntil] = useState(0);
  const [now, setNow] = useState(0);
  const sending = useRef(false);

  // One tick per second while the wait lasts, and none after it ends.
  useEffect(() => {
    if (phase !== "wait" || now >= waitUntil) return undefined;
    const timer = setTimeout(() => setNow(Date.now()), Math.min(1_000, waitUntil - now));
    return () => clearTimeout(timer);
  }, [phase, now, waitUntil]);

  const secondsLeft = phase === "wait" ? Math.max(0, Math.ceil((waitUntil - now) / 1_000)) : 0;

  const unsubscribe = useCallback(() => {
    if (token === undefined || token === null || sending.current) return;
    sending.current = true;
    setPhase("sending");
    const client = api ?? createRecipientUnsubscribeApi({ baseUrl: clientApiBaseUrl() });
    void client
      .unsubscribe(token)
      .then((outcome) => {
        if (outcome.kind === "wait") {
          const started = Date.now();
          setNow(started);
          setWaitUntil(started + outcome.seconds * 1_000);
          setPhase("wait");
        } else {
          setPhase(outcome.kind);
        }
      })
      .catch(() => setPhase("failed"))
      .finally(() => {
        sending.current = false;
      });
  }, [api, token]);

  return (
    <RecipientUnsubscribeView
      phase={recipientUnsubscribeViewPhase(token, phase)}
      secondsLeft={secondsLeft}
      onUnsubscribe={unsubscribe}
    />
  );
}

/**
 * What the page shows: busy until the browser's address is read (the server render and hydration), the invalid link
 * at once for an address without a well-formed token, otherwise the phase of the last press.
 */
export function recipientUnsubscribeViewPhase(
  token: string | null | undefined,
  phase: RecipientUnsubscribePhase,
): RecipientUnsubscribePhase {
  if (token === undefined) return "sending";
  return token === null ? "invalid" : phase;
}

/** The address never changes while the page is open: nothing to subscribe to. */
function subscribeToNothing(): () => void {
  return () => undefined;
}

function browserPathname(): string {
  return window.location.pathname;
}

function serverPathname(): string | null {
  return null;
}
