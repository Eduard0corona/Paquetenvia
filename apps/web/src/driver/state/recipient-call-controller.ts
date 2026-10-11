import { DriverVoiceApiError, type DriverVoiceApi } from "../api/voice-api";
import {
  recipientCallConflictMessage,
  recipientCallOfflineMessage,
  recipientCallPlacedMessage,
  recipientCallReasonHint,
} from "../contracts/voice";

/**
 * - `hidden`: nothing is shown (not a delivering delivery stop, or the server said no without a hint);
 * - `checking`: availability is being read;
 * - `offline`: the button is shown disabled with {@link recipientCallOfflineMessage};
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

const hidden: RecipientCallViewState = Object.freeze({ phase: "hidden", message: null });

/** VOICE-001: only the delivery recipient, and only while the confirmed status is DELIVERING (AI-07). */
export function isRecipientCallEligible(stopType: string, confirmedStatus: string): boolean {
  return stopType === "DELIVERY" && confirmedStatus === "DELIVERING";
}

/**
 * "Llamar al destinatario" for one stop. It is an online-only action: it is never queued, cached or replayed. The
 * Idempotency-Key of a tap is reused only when no answer reached the PWA (the same tap retried); any answer from the
 * API ends it. No phone number is ever received or kept.
 */
export class RecipientCallController {
  private readonly listeners = new Set<(state: RecipientCallViewState) => void>();
  private current: RecipientCallViewState = hidden;
  private retryKey: string | null = null;
  // The server answered "not available" without a hint (disabled bridge, other state, no recipient phone): stay
  // hidden while offline instead of offering a button that could never work.
  private knownUnavailable = false;
  private generation = 0;
  private disposed = false;

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
    const generation = ++this.generation;
    if (!eligible) {
      this.retryKey = null;
      this.setState(hidden);
      return;
    }

    if (!online) {
      if (this.current.phase !== "calling") {
        this.setState(this.knownUnavailable ? hidden : { phase: "offline", message: recipientCallOfflineMessage });
      }
      return;
    }

    if (this.current.phase === "calling") return;
    this.setState({ phase: "checking", message: this.current.phase === "offline" ? null : this.current.message });
    try {
      const availability = await this.api.getRecipientCallAvailability(this.orderId);
      if (generation !== this.generation) return;
      if (availability.available) {
        this.knownUnavailable = false;
        this.setState({ phase: "available", message: this.current.message });
        return;
      }

      const hint = recipientCallReasonHint(availability.reason);
      this.knownUnavailable = hint === null;
      this.setState(hint === null ? hidden : { phase: "hint", message: hint });
    } catch (error) {
      if (generation !== this.generation) return;
      if (error instanceof DriverVoiceApiError && error.category === "cancelled") return;
      this.setState(hidden);
    }
  }

  /** One tap. Offline it only explains; it never queues. */
  public async call(online: boolean): Promise<void> {
    if (this.disposed || this.current.phase !== "available") return;
    if (!online) {
      this.setState({ phase: "offline", message: recipientCallOfflineMessage });
      return;
    }

    const key = this.retryKey ?? this.newKey();
    this.retryKey = key;
    this.generation++;
    this.setState({ phase: "calling", message: null });
    try {
      const result = await this.api.requestRecipientCall(this.orderId, key);
      this.retryKey = null;
      this.setState({
        phase: "available",
        message: result.status === "UNCONFIRMED" ? recipientCallUnconfirmedMessage : recipientCallPlacedMessage,
      });
    } catch (error) {
      this.setState(this.failure(error));
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.generation++;
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
        return error.code === "DRIVER_PHONE_REQUIRED" || error.code === "DRIVER_PHONE_REJECTED" ||
          error.code === "RECIPIENT_PHONE_UNAVAILABLE" || error.code === "ORDER_STATE_NOT_ALLOWED"
          ? { phase: "hint", message: recipientCallConflictMessage(error.code) }
          : { phase: "available", message: recipientCallConflictMessage(error.code) };
      case "rate-limited": {
        const minutes = Math.max(1, Math.ceil((error.retryAfterSeconds ?? 60) / 60));
        return {
          phase: "hint",
          message: `Ya pediste varias llamadas para esta entrega. Intenta de nuevo en ${minutes} ${minutes === 1 ? "minuto" : "minutos"}.`,
        };
      }
      case "unavailable":
        return { phase: "available", message: recipientCallUnavailableMessage };
      case "not-found":
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

  private setState(state: RecipientCallViewState): void {
    if (this.disposed) return;
    this.current = Object.freeze({ ...state });
    for (const listener of this.listeners) listener(this.current);
  }
}
