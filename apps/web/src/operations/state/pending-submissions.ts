/**
 * Idempotency-Key memory for user-submitted writes, following the existing client
 * convention (a `crypto.randomUUID()` key kept until the server confirms): a retry of
 * the same submission resends the same key and the exact same payload, so the API
 * replays the stored response instead of creating a second effect. A changed
 * submission gets a fresh key. Everything lives in memory only and is dropped when
 * the tenant changes.
 */
export interface PreparedSubmission<T> {
  readonly key: string;
  readonly payload: T;
}

interface Entry {
  readonly fingerprint: string;
  readonly key: string;
  readonly payload: unknown;
}

export class PendingSubmissions {
  private readonly entries = new Map<string, Entry>();

  public constructor(
    private readonly randomUuid: () => string = () => crypto.randomUUID(),
  ) {}

  public prepare<T>(
    scope: string,
    fingerprint: string,
    build: () => T,
  ): PreparedSubmission<T> {
    const existing = this.entries.get(scope);
    if (existing !== undefined && existing.fingerprint === fingerprint) {
      return { key: existing.key, payload: existing.payload as T };
    }
    const entry = { fingerprint, key: this.randomUuid(), payload: build() };
    this.entries.set(scope, entry);
    return { key: entry.key, payload: entry.payload as T };
  }

  /** The server gave a definitive answer; the next submission uses a new key. */
  public settle(scope: string): void {
    this.entries.delete(scope);
  }

  public clear(): void {
    this.entries.clear();
  }

  public get size(): number {
    return this.entries.size;
  }
}
