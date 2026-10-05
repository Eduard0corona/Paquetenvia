"use client";

import { useRouter } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import { clientApiBaseUrl } from "../../lib/api-base-url";
import { createOrderActionsApi } from "../../operations/api/order-actions-api";
import { orderNotFoundMessage } from "../../operations/contracts/order-transitions";
import type { OperationsSession } from "../../operations/session/operations-session";
import {
  orderDetailPath,
  orderSearchFailedMessage,
  searchOrderByPublicId,
} from "../../operations/state/order-search";

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: "Buscar guía" in the top bar. The person types a
 * tracking number; an exact match in the selected organization opens its detail, anything else
 * shows "No encontramos esa guía". Only the tracking number is searched, never personal data,
 * and the typed text is not kept anywhere.
 */
export function OrderSearchBox({ session }: { readonly session: OperationsSession }) {
  const router = useRouter();
  const inputId = useId();
  const messageId = useId();
  const [value, setValue] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // A new session or organization drops any search in flight and its message.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      setMessage(null);
      setBusy(false);
    }, 0);
    return () => {
      clearTimeout(timer);
      abortRef.current?.abort();
      abortRef.current = null;
    };
  }, [session]);

  async function search() {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setBusy(true);
    setMessage(null);
    try {
      const outcome = await searchOrderByPublicId(
        createOrderActionsApi(clientApiBaseUrl(), session),
        value,
        controller.signal,
      );
      if (controller.signal.aborted) return;
      if (outcome.kind === "found") {
        setValue("");
        router.push(orderDetailPath(outcome.orderId));
      } else {
        setMessage(outcome.kind === "not_found" ? orderNotFoundMessage : orderSearchFailedMessage);
      }
    } catch {
      // Aborted because the session changed or another search started.
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
        setBusy(false);
      }
    }
  }

  return (
    <form
      className="appSearch"
      role="search"
      aria-label="Buscar guía"
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        if (!busy) void search();
      }}
    >
      <label htmlFor={inputId} className="srOnly">
        Número de guía
      </label>
      <input
        id={inputId}
        name="public_id"
        type="search"
        inputMode="text"
        autoComplete="off"
        autoCapitalize="off"
        spellCheck={false}
        placeholder="Buscar guía (ORD_…)"
        maxLength={64}
        value={value}
        aria-describedby={message === null ? undefined : messageId}
        onChange={(event) => {
          setValue(event.currentTarget.value);
          setMessage(null);
        }}
      />
      <button type="submit" className="btn btnSecondary" aria-disabled={busy}>
        {busy ? "Buscando…" : "Buscar"}
      </button>
      {message !== null && (
        <p id={messageId} className="appSearchMessage" role="status" aria-live="polite">
          {message}
        </p>
      )}
    </form>
  );
}
