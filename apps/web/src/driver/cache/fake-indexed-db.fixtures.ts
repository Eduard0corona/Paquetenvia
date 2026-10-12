/**
 * Test support only: an in-memory stand-in for the part of IndexedDB the driver
 * cache and offline queue use (open with upgrade, object stores with array key
 * paths and indexes, getAll and key cursors over IDBKeyRange.only, add, put,
 * get, delete, clear, cursors with update, abort and auto-commit). Every
 * transaction works on a staged copy of its stores and commits it whole when
 * it completes, or drops it when it aborts or a request fails, like the real
 * thing, so atomicity can be asserted. The real browser runs in the Playwright
 * driver suite.
 */

type Key = string | number | readonly (string | number)[];
type KeyPath = string | readonly string[];

interface StoreSchema {
  readonly keyPath: KeyPath;
  readonly indexes: Map<string, KeyPath>;
}

interface DatabaseState {
  version: number;
  readonly schemas: Map<string, StoreSchema>;
  readonly records: Map<string, Map<string, unknown>>;
}

interface KeyRangeOnly {
  readonly only: Key;
}

class FakeRequest<T = unknown> {
  public result: T | undefined = undefined;
  public error: DOMException | null = null;
  public transaction: FakeTransaction | null = null;
  public onsuccess: ((event: unknown) => void) | null = null;
  public onerror: ((event: unknown) => void) | null = null;
  public onupgradeneeded: ((event: { oldVersion: number }) => void) | null = null;
  public onblocked: (() => void) | null = null;
}

class FakeIndex {
  public constructor(
    private readonly store: FakeObjectStore,
    private readonly keyPath: KeyPath,
  ) {}

  public getAll(range?: KeyRangeOnly): FakeRequest<unknown[]> {
    return this.store.request(() =>
      this.matching(range).map(([, value]) => cloneValue(value)),
    );
  }

  public openKeyCursor(range?: KeyRangeOnly): FakeRequest {
    const matches = this.matching(range).map(([key]) => key);
    return this.store.cursor(matches, false);
  }

  private matching(range?: KeyRangeOnly): [string, unknown][] {
    return [...this.store.staged().entries()]
      .filter(
        ([, value]) =>
          range === undefined ||
          serializeKey(extractKey(value, this.keyPath)) === serializeKey(range.only),
      )
      .sort(([left], [right]) => left.localeCompare(right));
  }
}

class FakeObjectStore {
  public constructor(
    private readonly transaction: FakeTransaction,
    public readonly name: string,
    private readonly schema: StoreSchema,
  ) {}

  public staged(): Map<string, unknown> {
    return this.transaction.stagedStore(this.name);
  }

  public createIndex(name: string, keyPath: KeyPath): void {
    this.schema.indexes.set(name, keyPath);
  }

  public index(name: string): FakeIndex {
    const keyPath = this.schema.indexes.get(name);
    if (!keyPath) throw new DOMException(`No index ${name}`, "NotFoundError");
    return new FakeIndex(this, keyPath);
  }

  public get(key: Key): FakeRequest {
    return this.request(() => cloneValue(this.staged().get(serializeKey(key))));
  }

  public getAll(): FakeRequest<unknown[]> {
    return this.request(() => [...this.staged().values()].map(cloneValue));
  }

  public count(): FakeRequest<number> {
    return this.request(() => this.staged().size);
  }

  public add(value: unknown): FakeRequest {
    return this.request(() => {
      const key = serializeKey(extractKey(value, this.schema.keyPath));
      if (this.staged().has(key)) {
        throw new DOMException("Key already exists.", "ConstraintError");
      }
      this.staged().set(key, cloneValue(value));
      return undefined;
    });
  }

  public put(value: unknown): FakeRequest {
    return this.request(() => {
      this.staged().set(
        serializeKey(extractKey(value, this.schema.keyPath)),
        cloneValue(value),
      );
      return undefined;
    });
  }

  public delete(key: Key): FakeRequest {
    return this.request(() => {
      this.staged().delete(serializeKey(key));
      return undefined;
    });
  }

  public clear(): FakeRequest {
    return this.request(() => {
      this.staged().clear();
      return undefined;
    });
  }

  public openCursor(): FakeRequest {
    return this.cursor(
      [...this.staged().keys()].sort((left, right) => left.localeCompare(right)),
      true,
    );
  }

  public request<T>(work: () => T): FakeRequest<T> {
    return this.transaction.run(work);
  }

  public cursor(keys: readonly string[], withValue: boolean): FakeRequest {
    const request = new FakeRequest<unknown>();
    let position = 0;
    const step = () => {
      this.transaction.schedule(request, () => {
        if (position >= keys.length) return null;
        const key = keys[position];
        return {
          primaryKey: JSON.parse(key) as Key,
          value: withValue ? cloneValue(this.staged().get(key)) : undefined,
          update: (value: unknown) => this.put(value),
          continue: () => {
            position += 1;
            step();
          },
        };
      });
    };
    step();
    return request;
  }
}

class FakeTransaction {
  public oncomplete: (() => void) | null = null;
  public onerror: (() => void) | null = null;
  public onabort: (() => void) | null = null;
  private readonly staging = new Map<string, Map<string, unknown>>();
  private pending = 0;
  private finished = false;

  public constructor(
    private readonly state: DatabaseState,
    private readonly scope: readonly string[],
    private readonly upgrade: boolean,
    private readonly onDone: (committed: boolean) => void = () => undefined,
  ) {}

  public objectStore(name: string): FakeObjectStore {
    const schema = this.state.schemas.get(name);
    if (!schema || (!this.upgrade && !this.scope.includes(name))) {
      throw new DOMException(`No store ${name}`, "NotFoundError");
    }
    return new FakeObjectStore(this, name, schema);
  }

  public stagedStore(name: string): Map<string, unknown> {
    let staged = this.staging.get(name);
    if (!staged) {
      staged = new Map(this.state.records.get(name) ?? new Map());
      this.staging.set(name, staged);
    }
    return staged;
  }

  public abort(): void {
    if (this.finished) return;
    this.finished = true;
    this.onDone(false);
    setTimeout(() => this.onabort?.(), 0);
  }

  public run<T>(work: () => T): FakeRequest<T> {
    const request = new FakeRequest<T>();
    this.schedule(request, work);
    return request;
  }

  public schedule<T>(request: FakeRequest<T>, work: () => T): void {
    if (this.finished) {
      throw new DOMException("The transaction has finished.", "TransactionInactiveError");
    }
    this.pending += 1;
    queueMicrotask(() => {
      this.pending -= 1;
      if (this.finished) return;
      try {
        request.result = work();
      } catch (error) {
        request.error =
          error instanceof DOMException ? error : new DOMException(String(error));
        request.onerror?.({ target: request });
        this.fail();
        return;
      }
      request.onsuccess?.({ target: request });
      this.maybeCommit();
    });
  }

  /** Commits once no request is pending after the current task, like auto-commit. */
  public maybeCommit(): void {
    if (this.pending > 0 || this.finished) return;
    setTimeout(() => {
      if (this.pending > 0 || this.finished) return;
      this.finished = true;
      for (const [name, records] of this.staging) {
        this.state.records.set(name, records);
      }
      this.onDone(true);
      this.oncomplete?.();
    }, 0);
  }

  private fail(): void {
    if (this.finished) return;
    this.finished = true;
    this.onDone(false);
    setTimeout(() => {
      this.onerror?.();
      this.onabort?.();
    }, 0);
  }
}

class FakeDatabase {
  public closed = false;

  public constructor(private readonly state: DatabaseState) {}

  public get version(): number {
    return this.state.version;
  }

  public get objectStoreNames(): { contains(name: string): boolean } {
    return { contains: (name: string) => this.state.schemas.has(name) };
  }

  public createObjectStore(
    name: string,
    options: { keyPath: KeyPath },
  ): { createIndex(name: string, keyPath: KeyPath): void } {
    const schema: StoreSchema = { keyPath: options.keyPath, indexes: new Map() };
    this.state.schemas.set(name, schema);
    this.state.records.set(name, new Map());
    return {
      createIndex: (indexName: string, keyPath: KeyPath) => {
        schema.indexes.set(indexName, keyPath);
      },
    };
  }

  public transaction(
    names: string | readonly string[],
    mode: "readonly" | "readwrite" = "readonly",
  ): FakeTransaction {
    if (this.closed) throw new DOMException("closed", "InvalidStateError");
    const scope = typeof names === "string" ? [names] : [...names];
    void mode;
    const transaction = new FakeTransaction(this.state, scope, false);
    // A transaction with no request at all still completes.
    transaction.maybeCommit();
    return transaction;
  }

  public close(): void {
    this.closed = true;
  }
}

/** One fake browser profile: its databases survive "reloads" (new opens). */
export class FakeIndexedDb {
  private readonly databases = new Map<string, DatabaseState>();
  public opens = 0;

  public open(name: string, version?: number): FakeRequest<FakeDatabase> {
    const request = new FakeRequest<FakeDatabase>();
    this.opens += 1;
    setTimeout(() => {
      let state = this.databases.get(name);
      const oldVersion = state?.version ?? 0;
      const target = version ?? (oldVersion || 1);
      if (target < oldVersion) {
        request.error = new DOMException("The requested version is less than the existing version.", "VersionError");
        request.onerror?.({ target: request });
        return;
      }
      if (!state) {
        state = { version: 0, schemas: new Map(), records: new Map() };
        this.databases.set(name, state);
      }
      const database = new FakeDatabase(state);
      if (target === oldVersion) {
        request.result = database;
        request.onsuccess?.({ target: request });
        return;
      }
      const upgradeState = state;
      const previousSchemas = new Map(state.schemas);
      const previousRecords = new Map(state.records);
      upgradeState.version = target;
      const upgrade = new FakeTransaction(upgradeState, [], true, (committed) => {
        if (committed) {
          request.transaction = null;
          request.result = database;
          request.onsuccess?.({ target: request });
          return;
        }
        upgradeState.version = oldVersion;
        upgradeState.schemas.clear();
        previousSchemas.forEach((value, key) => upgradeState.schemas.set(key, value));
        upgradeState.records.clear();
        previousRecords.forEach((value, key) => upgradeState.records.set(key, value));
        request.error = new DOMException("Upgrade aborted.", "AbortError");
        request.onerror?.({ target: request });
      });
      request.result = database;
      request.transaction = upgrade;
      request.onupgradeneeded?.({ oldVersion });
      upgrade.maybeCommit();
    }, 0);
    return request;
  }

  /** Every stored value of a store, cloned, for assertions. */
  public records(name: string, store: string): unknown[] {
    return [...(this.databases.get(name)?.records.get(store)?.values() ?? [])].map(
      cloneValue,
    );
  }

  public version(name: string): number {
    return this.databases.get(name)?.version ?? 0;
  }

  /** Writes a raw record as an older or newer app would have stored it. */
  public seed(name: string, store: string, value: unknown): void {
    const state = this.databases.get(name);
    const schema = state?.schemas.get(store);
    if (!state || !schema) throw new Error(`No store ${store}`);
    state.records
      .get(store)!
      .set(serializeKey(extractKey(value, schema.keyPath)), cloneValue(value));
  }
}

/** `IDBKeyRange.only`, the only key range the driver code builds. */
export const fakeIdbKeyRange = Object.freeze({
  only: (value: Key): KeyRangeOnly => Object.freeze({ only: value }),
});

function extractKey(value: unknown, keyPath: KeyPath): Key {
  const record = value as Record<string, unknown>;
  const read = (path: string): string | number => {
    const part = record?.[path];
    if (typeof part !== "string" && typeof part !== "number") {
      throw new DOMException(`Invalid key at ${path}`, "DataError");
    }
    return part;
  };
  return typeof keyPath === "string" ? read(keyPath) : keyPath.map(read);
}

function serializeKey(key: Key): string {
  return JSON.stringify(key);
}

/** Structured clone that keeps Blob identity (Blobs are immutable). */
function cloneValue<T>(value: T): T {
  if (value === null || typeof value !== "object" || value instanceof Blob) {
    return value;
  }
  if (Array.isArray(value)) return value.map(cloneValue) as T;
  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>).map(([key, entry]) => [
      key,
      cloneValue(entry),
    ]),
  ) as T;
}
