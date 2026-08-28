import { ExternalOffersApiError, type ExternalOffersApi } from "../api/external-offers-api";
import type { ExternalOffer } from "../contracts/external-offer";

export interface ExternalOffersState {
  readonly offers: readonly ExternalOffer[];
  readonly loading: boolean;
  readonly pendingOfferId: string | null;
  readonly message: string | null;
}

export class ExternalOffersController {
  private readonly listeners = new Set<(state: ExternalOffersState) => void>();
  private readonly dismissed = new Set<string>();
  private readonly idempotency = new Map<string, string>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;
  private state: ExternalOffersState = Object.freeze({ offers: [], loading: true, pendingOfferId: null, message: null });

  public constructor(private readonly api: ExternalOffersApi) {}

  public subscribe(listener: (state: ExternalOffersState) => void): () => void {
    this.listeners.add(listener);
    listener(this.state);
    return () => this.listeners.delete(listener);
  }

  public start(): Promise<void> { return this.refresh(); }
  public refreshForReconnect(): Promise<void> { return this.refresh(); }

  public scheduleRefresh(): void {
    if (this.disposed || this.timer) return;
    this.timer = setTimeout(() => { this.timer = null; void this.refresh(); }, 250);
  }

  public dismiss(offerId: string): void {
    this.dismissed.add(offerId);
    this.setState({ ...this.state, offers: this.state.offers.filter((offer) => offer.id !== offerId), message: null });
  }

  public async accept(offerId: string): Promise<void> {
    if (this.disposed || this.state.pendingOfferId) return;
    const key = this.idempotency.get(offerId) ?? crypto.randomUUID();
    this.idempotency.set(offerId, key);
    this.setState({ ...this.state, pendingOfferId: offerId, message: null });
    try {
      await this.api.accept(offerId, key);
      this.idempotency.delete(offerId);
      await this.refresh("Oferta aceptada. La asignacion externa quedo confirmada.");
    } catch (error) {
      const message = error instanceof ExternalOffersApiError && error.category === "conflict"
        ? "La oferta ya no esta disponible o fue aceptada por otro repartidor."
        : "No pudimos aceptar la oferta. Intenta nuevamente.";
      this.setState({ ...this.state, pendingOfferId: null, message });
      if (error instanceof ExternalOffersApiError && error.category === "conflict") await this.refresh(message);
    }
  }

  public dispose(): void {
    this.disposed = true;
    if (this.timer) clearTimeout(this.timer);
    this.listeners.clear();
  }

  private async refresh(message: string | null = null): Promise<void> {
    if (this.disposed) return;
    this.setState({ ...this.state, loading: true });
    try {
      const page = await this.api.list();
      this.setState({
        offers: page.items.filter((offer) => offer.status === "OPEN" && !this.dismissed.has(offer.id)),
        loading: false,
        pendingOfferId: null,
        message,
      });
    } catch (error) {
      if (error instanceof ExternalOffersApiError && error.category === "cancelled") return;
      this.setState({ ...this.state, loading: false, pendingOfferId: null, message: "No pudimos actualizar las ofertas." });
    }
  }

  private setState(state: ExternalOffersState): void {
    if (this.disposed) return;
    this.state = Object.freeze(state);
    for (const listener of this.listeners) listener(this.state);
  }
}
