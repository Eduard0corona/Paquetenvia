/** Minimal snapshot store for `useSyncExternalStore`; controllers stay testable without React. */
export class ExternalStore<TState extends object> {
  private readonly listeners = new Set<() => void>();

  protected constructor(private state: TState) {}

  public readonly getSnapshot = (): TState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  protected update(patch: Partial<TState>): void {
    this.state = { ...this.state, ...patch };
    for (const listener of this.listeners) listener();
  }
}
