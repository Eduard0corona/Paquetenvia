import { DriverVoiceApiError, type DriverVoiceApi } from "../api/voice-api";
import { normalizeDriverPhone, type DriverPhoneStatus } from "../contracts/voice";

/**
 * - `loading`: reading whether a number is stored;
 * - `hidden`: the bridge is not enabled here (or this account is not a driver): no phone is collected;
 * - `ready`: the form can be used;
 * - `saving`: a registration or removal is in flight;
 * - `error`: the status could not be read (retry by reopening "Cuenta").
 */
export type DriverPhonePhase = "loading" | "hidden" | "ready" | "saving" | "error";

export interface DriverPhoneViewState {
  readonly phase: DriverPhonePhase;
  /** False while the bridge is off: a stored number can still be removed, a new one is not collected. */
  readonly voiceCallsEnabled: boolean;
  readonly registered: boolean;
  readonly consentedAt: string | null;
  readonly message: string | null;
}

export const driverPhoneMessages = Object.freeze({
  invalid: "Escribe tu celular de 10 dígitos. Puede iniciar con +52.",
  consentRequired: "Para registrar tu celular debes aceptar el aviso.",
  offline: "Necesitas conexión a internet para registrar o borrar tu celular.",
  saved: "Celular registrado. Ya puedes llamar a los destinatarios de tus entregas.",
  removed: "Borramos tu celular.",
  rejected: "Revisa el número: debe ser un celular de México de 10 dígitos.",
  unavailable: "No pudimos guardar el cambio en este momento. Intenta más tarde.",
  loadFailed: "No pudimos consultar tu celular. Intenta más tarde.",
});

const loading: DriverPhoneViewState = Object.freeze({
  phase: "loading",
  voiceCallsEnabled: false,
  registered: false,
  consentedAt: null,
  message: null,
});
const hidden: DriverPhoneViewState = Object.freeze({ ...loading, phase: "hidden" });

/**
 * VOICE-001 "Cuenta": the driver's own mobile for masked calls. The number is validated locally with the server
 * rule, sent once with the consent, and never kept: the controller does not hold it and the API never returns it.
 * Online only.
 */
export class DriverPhoneController {
  private readonly listeners = new Set<(state: DriverPhoneViewState) => void>();
  private current: DriverPhoneViewState = loading;
  private disposed = false;

  public constructor(private readonly api: Pick<DriverVoiceApi, "getPhone" | "registerPhone" | "removePhone">) {}

  public get state(): DriverPhoneViewState {
    return this.current;
  }

  public subscribe(listener: (state: DriverPhoneViewState) => void): () => void {
    this.listeners.add(listener);
    listener(this.current);
    return () => this.listeners.delete(listener);
  }

  public async load(online: boolean): Promise<void> {
    if (!online) {
      this.setState({ ...this.current, phase: this.current.phase === "loading" ? "error" : this.current.phase, message: driverPhoneMessages.offline });
      return;
    }

    try {
      this.apply(await this.api.getPhone(), null);
    } catch (error) {
      if (error instanceof DriverVoiceApiError && error.category === "cancelled") return;
      if (error instanceof DriverVoiceApiError && (error.category === "forbidden" || error.category === "unauthorized")) {
        this.setState(hidden);
        return;
      }
      this.setState({ ...loading, phase: "error", message: driverPhoneMessages.loadFailed });
    }
  }

  /** Returns true when the number was registered; the caller then clears the field. */
  public async register(input: string, consentAccepted: boolean, online: boolean): Promise<boolean> {
    if (this.current.phase !== "ready" || !this.current.voiceCallsEnabled) return false;
    const digits = normalizeDriverPhone(input);
    if (digits === null) {
      this.setState({ ...this.current, message: driverPhoneMessages.invalid });
      return false;
    }
    if (!consentAccepted) {
      this.setState({ ...this.current, message: driverPhoneMessages.consentRequired });
      return false;
    }
    if (!online) {
      this.setState({ ...this.current, message: driverPhoneMessages.offline });
      return false;
    }

    this.setState({ ...this.current, phase: "saving", message: null });
    try {
      this.apply(await this.api.registerPhone(digits), driverPhoneMessages.saved);
      return true;
    } catch (error) {
      this.setState({ ...this.current, phase: "ready", message: this.failureMessage(error) });
      return false;
    }
  }

  public async remove(online: boolean): Promise<boolean> {
    if (this.current.phase !== "ready" || !this.current.registered) return false;
    if (!online) {
      this.setState({ ...this.current, message: driverPhoneMessages.offline });
      return false;
    }

    this.setState({ ...this.current, phase: "saving", message: null });
    try {
      this.apply(await this.api.removePhone(), driverPhoneMessages.removed);
      return true;
    } catch (error) {
      this.setState({ ...this.current, phase: "ready", message: this.failureMessage(error) });
      return false;
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.listeners.clear();
  }

  private apply(status: DriverPhoneStatus, message: string | null): void {
    this.setState(status.voice_calls_enabled || status.registered
      ? {
          phase: "ready",
          voiceCallsEnabled: status.voice_calls_enabled,
          registered: status.registered,
          consentedAt: status.consented_at,
          message,
        }
      : hidden);
  }

  private failureMessage(error: unknown): string {
    if (error instanceof DriverVoiceApiError) {
      if (error.category === "conflict") return driverPhoneMessages.rejected;
      if (error.category === "network") return driverPhoneMessages.offline;
    }
    return driverPhoneMessages.unavailable;
  }

  private setState(state: DriverPhoneViewState): void {
    if (this.disposed) return;
    this.current = Object.freeze({ ...state });
    for (const listener of this.listeners) listener(this.current);
  }
}
