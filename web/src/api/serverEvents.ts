export type EventStatus = 'connecting' | 'live' | 'unavailable';
export type EventHandler = (data: unknown) => void;

export interface EventSourceLike {
  readonly readyState: number;
  onopen: ((event: unknown) => void) | null;
  onerror: ((event: unknown) => void) | null;
  addEventListener(type: string, listener: (event: { data: string }) => void): void;
  close(): void;
}

export type EventSourceFactory = (url: string) => EventSourceLike;

const CLOSED = 2;
const EVENT_TYPES = ['ready', 'job', 'run'] as const;
const MAX_BACKOFF_MS = 15000;

export const reconnectDelay = (attempt: number): number => Math.min(1000 * 2 ** attempt, MAX_BACKOFF_MS);

/** Replaces the item with the same key, or appends it. */
export function upsertBy<T>(list: readonly T[], item: T, key: (value: T) => string): T[] {
  const id = key(item);
  const index = list.findIndex((value) => key(value) === id);
  if (index < 0) return [...list, item];
  const next = [...list];
  next[index] = item;
  return next;
}

/** One EventSource shared by all subscribers; opens on first subscribe and closes after the last leaves. */
export class EventHub {
  private source: EventSourceLike | null = null;
  private handlers = new Map<string, Set<EventHandler>>();
  private resyncs = new Set<() => void>();
  private statuses = new Set<(status: EventStatus) => void>();
  private timer: ReturnType<typeof setTimeout> | undefined;
  private attempt = 0;
  private everOpened = false;
  private dropped = false;
  private count = 0;
  status: EventStatus = 'connecting';

  constructor(
    private readonly factory: EventSourceFactory,
    private readonly url = '/api/events',
  ) {}

  subscribe(handlers: Record<string, EventHandler>, onResync?: () => void, onStatus?: (status: EventStatus) => void): () => void {
    const added = Object.entries(handlers);
    for (const [type, fn] of added) {
      const set = this.handlers.get(type) ?? new Set();
      set.add(fn);
      this.handlers.set(type, set);
    }
    if (onResync) this.resyncs.add(onResync);
    if (onStatus) {
      this.statuses.add(onStatus);
      onStatus(this.status);
    }
    this.count += 1;
    if (this.count === 1) this.connect();
    return () => {
      for (const [type, fn] of added) this.handlers.get(type)?.delete(fn);
      if (onResync) this.resyncs.delete(onResync);
      if (onStatus) this.statuses.delete(onStatus);
      this.count -= 1;
      if (this.count === 0) this.shutdown();
    };
  }

  private setStatus(status: EventStatus): void {
    this.status = status;
    this.statuses.forEach((fn) => fn(status));
  }

  private connect(): void {
    const source = this.factory(this.url);
    this.source = source;
    for (const type of EVENT_TYPES) {
      source.addEventListener(type, (event) => this.dispatch(type, event.data));
    }
    source.onopen = () => {
      if (this.source !== source) return;
      const resync = this.everOpened && this.dropped;
      this.everOpened = true;
      this.dropped = false;
      this.attempt = 0;
      this.setStatus('live');
      if (resync) this.resyncs.forEach((fn) => fn());
    };
    source.onerror = () => {
      if (this.source !== source) return;
      this.dropped = true;
      if (source.readyState !== CLOSED) return; // the browser is retrying by itself
      source.close();
      this.source = null;
      if (!this.everOpened) {
        this.setStatus('unavailable');
        return;
      }
      this.setStatus('connecting');
      this.timer = setTimeout(() => this.count > 0 && this.connect(), reconnectDelay(this.attempt));
      this.attempt += 1;
    };
  }

  dispatch(type: string, raw: string): void {
    let data: unknown;
    try {
      data = JSON.parse(raw);
    } catch {
      return;
    }
    this.handlers.get(type)?.forEach((fn) => fn(data));
  }

  private shutdown(): void {
    clearTimeout(this.timer);
    this.source?.close();
    this.source = null;
    this.everOpened = false;
    this.dropped = false;
    this.attempt = 0;
    this.status = 'connecting';
  }
}

let shared: EventHub | null = null;

export const sharedHub = (): EventHub => (shared ??= new EventHub((url) => new EventSource(url) as unknown as EventSourceLike));
