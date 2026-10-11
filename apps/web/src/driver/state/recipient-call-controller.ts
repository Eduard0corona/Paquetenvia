import { DriverVoiceApiError, type DriverVoiceApi } from "../api/voice-api";
import {
  recipientCallConflictMessage,
  recipientCallOfflineMessage,
  recipientCallPlacedMessage,
  recipientCallReasonHint,
} from "../contracts/voice";

/**
 * - `hidden`: nothing is shown (not a delivering delivery stop, the server said no without a hint, or the call was
 *   never confirmed available);
 * - `checking`: availability is being read;
 * - `offline`: the button is shown disabled with {@link recipientCallOfflineMessage} (only after the server said the
 *   call is available);
 * - `available`: "Llamar al destinatario" can be tapped;
 * - `hint`: no button, only a hint (for example, register the phone in "Cuenta");
 * - `calling`: the request is in flight.
 */
export type RecipientCallPhase = "hidden" | "checking" | "offline" | "available" | "hint" | "calling";

export interface RecipientCallViewState {
  readonly phase: RecipientCallPhase;
  readonly message: string | null;
}

export const recipientCallUnconfirmedMessage =
  "No pudimos confirmar la llamada. Si tu celular no suena en un minuto, intenta de nuevo.";
export const recipientCallNetworkMessage = "No pudimos pedir la llamada. Revisa tu conexión e intenta de nuevo.";
export const recipientCallUnavailableMessage = "Las llamadas no están disponibles en este momento. Intenta más tarde.";

/** The longest wait the PWA schedules a re-check for (the server's limits never ask for more than an hour). */
const maximumRecheckSeconds = 3600;

const hidden: RecipientCallViewState = Object.freeze({ phase: "hidden", message: null });

/** VOICE-001: only the delivery recipient, and only while the confirmed status is DELIVERING (AI-07). */
export function isRecipientCallEligible(stopType: string, confirmedStatus: string): boolean {
  return stopType === "DELIVERY" && confirmedStatus === "DELIVERING";
}

/**
 * "Llamar al destinatario" for one stop. It is an online-only action: it is never queued, cached or replayed. The
 * Idempotency-Key of a tap is reused only when no answer reached the PWA (the same tap retried); any answer from the
 * API ends it. No phone number is ever received or kept.
 *
 * Every read and tap carries a generation: an answer that arrives after the stop changed (or after dispose) is
 * ignored. When the server says to wait (a 429 Retry-After, or availability RATE_LIMITED with retry_after_seconds),
 * one re-check is scheduled for when the wait is over.
 */
export class RecipientCallController {
  private readonly listeners = new Set<(state: RecipientCallViewState) => void>();
  private current: RecipientCallViewState = hidden;
  private retryKey: string | null = null;
  // Offline, the disabled button is shown only once the server said the call is available for this stop: before that
  // (or after it said no) nothing is offered, since the call might never work here.
  private confirmedAvailable = false;
  private generation = 0;
  private disposed = false;
  private online = true;
  private recheck: ReturnType<typeof setTimeout> | null = null;

  public constructor(
    private readonly api: Pick<DriverVoiceApi, "getRecipientCallAvailability" | "requestRecipientCall">,
    private readonly orderId: string,
    private readonly newKey: () => string = () => crypto.randomUUID(),
  ) {}

  public get state(): RecipientCallViewState {
    return this.current;
  }

  public subscribe(listener: (state: RecipientCallViewState) => void): () => void {
    this.listeners.add(listener);
    listener(this.current);
    return () => this.listeners.delete(listener);
  }

  /** The stop or the connectivity changed: hide, show offline, or ask the server again. */
  public async update(eligible: boolean, online: boolean): Promise<void> {
    if (this.disposed) return;
    this.online = online;
    if (!eligible) {
      // Also ends a tap in flight: its answer belongs to a stop that is no longer offered.
      this.generation++;
      this.cancelRecheck();
      this.retryKey = null;
      this.confirmedAvailable = false;
      this.setState(hidden);
      return;
    }

    // A tap in flight is never interrupted: its own answer decides what comes next.
    if (this.current.phase === "calling") return;
    const generation = ++this.generation;
    this.cancelRecheck();
    if (!online) {
      // The disabled button only for a call the server confirmed; a hint stays; anything else shows nothing.
      this.setState(this.confirmedAvailable
        ? { phase: "offline", message: recipientCallOfflineMessage }
        : this.current.phase === "hint" ? this.current : hidden);
      return;
    }

    this.setState({ phase: "checking", message: this.current.phase === "offline" ? null : this.current.message });
    try {
      const availability = await this.api.getRecipientCallAvailability(this.orderId);
      if (generation !== this.generation) return;
      if (availability.available) {
        this.confirmedAvailable = true;
        this.setState({ phase: "available", message: this.current.message });
        return;
      }

      this.confirmedAvailable = false;
      const hint = recipientCallReasonHint(availability.reason);
      this.setState(hint === null ? hidden : { phase: "hint", message: hint });
      if (availability.reason === "RATE_LIMITED") this.scheduleRecheck(availability.retry_after_seconds);
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof DriverVoiceApiError && error.category === "cancelled") return;
      this.setState(hidden);
    }
  }

  /** One tap. Offline it only explains; it never queues. */
  public async call(online: boolean): Promise<void> {
    if (this.disposed || this.current.phase !== "available") return;
    this.online = online;
    if (!online) {
      this.setState({ phase: "offline", message: recipientCallOfflineMessage });
      return;
    }

    const key = this.retryKey ?? this.newKey();
    this.retryKey = key;
    const generation = ++this.generation;
    this.cancelRecheck();
    this.setState({ phase: "calling", message: null });
    try {
      const result = await this.api.requestRecipientCall(this.orderId, key);
      if (generation !== this.generation) return;
      this.retryKey = null;
      this.setState({
        phase: "available",
        message: result.status === "UNCONFIRMED" ? recipientCallUnconfirmedMessage : recipientCallPlacedMessage,
      });
    } catch (error) {
      if (generation !== this.generation) return;
      this.setState(this.failure(error));
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.generation++;
    this.cancelRecheck();
    this.listeners.clear();
  }

  private failure(error: unknown): RecipientCallViewState {
    if (!(error instanceof DriverVoiceApiError)) {
      this.retryKey = null;
      return { phase: "available", message: recipientCallConflictMessage(null) };
    }

    if (error.category === "network") {
      // No answer arrived: the same tap may be retried with the same key, so a call is never doubled.
      return { phase: "available", message: recipientCallNetworkMessage };
    }

    this.retryKey = null;
    switch (error.category) {
      case "conflict":
        if (error.code === "DRIVER_PHONE_REQUIRED" || error.code === "DRIVER_PHONE_REJECTED" ||
            error.code === "RECIPIENT_PHONE_UNAVAILABLE" || error.code === "ORDER_STATE_NOT_ALLOWED") {
          // The call cannot work as things are: offline, nothing is offered until the server says otherwise.
          this.confirmedAvailable = false;
          return { phase: "hint", message: recipientCallConflictMessage(error.code) };
        }
        return { phase: "available", message: recipientCallConflictMessage(error.code) };
      case "rate-limited": {
        const seconds = error.retryAfterSeconds ?? 60;
        this.scheduleRecheck(seconds);
        const minutes = Math.max(1, Math.ceil(seconds / 60));
        return {
          phase: "hint",
          message: `Ya pediste varias llamadas para esta entrega. Intenta de nuevo en ${minutes} ${minutes === 1 ? "minuto" : "minutos"}.`,
        };
      }
      case "unavailable":
        return { phase: "available", message: recipientCallUnavailableMessage };
      case "not-found":
        this.confirmedAvailable = false;
        return hidden;
      case "unauthorized":
      case "forbidden":
        return { phase: "hint", message: "Tu sesión no permite pedir llamadas. Vuelve a iniciar sesión." };
      case "cancelled":
        return { phase: "available", message: null };
      default:
        return { phase: "available", message: recipientCallConflictMessage(null) };
    }
  }

  /** One re-check when the server's wait is over; a newer read, tap or wait replaces it. */
  private scheduleRecheck(seconds: number | null): void {
    this.cancelRecheck();
    if (this.disposed || seconds === null || !Number.isFinite(seconds) || seconds < 1) return;
    const delay = Math.min(Math.ceil(seconds), maximumRecheckSeconds) * 1000;
    this.recheck = setTimeout(() => {
      this.recheck = null;
      void this.update(true, this.online);
    }, delay);
  }

  private cancelRecheck(): void {
    if (this.recheck === null) return;
    clearTimeout(this.recheck);
    this.recheck = null;
  }

  private setState(state: RecipientCallViewState): void {
    if (this.disposed) return;
    this.current = Object.freeze({ ...state });
    for (const listener of this.listeners) listener(this.current);
  }
}
