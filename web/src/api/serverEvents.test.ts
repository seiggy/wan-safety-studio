import { describe, expect, it, vi } from 'vitest';
import { EventHub, upsertBy, type EventSourceLike, type EventStatus } from './serverEvents';

class FakeSource implements EventSourceLike {
  readyState = 0;
  onopen: ((event: unknown) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  closed = false;
  private listeners = new Map<string, (event: { data: string }) => void>();
  addEventListener(type: string, listener: (event: { data: string }) => void) {
    this.listeners.set(type, listener);
  }
  close() {
    this.closed = true;
  }
  emit(type: string, data: string) {
    this.listeners.get(type)?.({ data });
  }
}

function setup() {
  const sources: FakeSource[] = [];
  const hub = new EventHub((url) => {
    expect(url).toBe('/api/events');
    const source = new FakeSource();
    sources.push(source);
    return source;
  });
  return { hub, sources };
}

describe('upsertBy', () => {
  it('replaces by key or appends', () => {
    const list = [{ id: 'a', v: 1 }];
    expect(upsertBy(list, { id: 'a', v: 2 }, (x) => x.id)).toEqual([{ id: 'a', v: 2 }]);
    expect(upsertBy(list, { id: 'b', v: 3 }, (x) => x.id)).toHaveLength(2);
  });
});

describe('EventHub', () => {
  it('shares one source, dispatches parsed events by type and closes after the last unsubscribe', () => {
    const { hub, sources } = setup();
    const jobs = vi.fn();
    const runs = vi.fn();
    const off1 = hub.subscribe({ job: jobs });
    const off2 = hub.subscribe({ run: runs });
    expect(sources).toHaveLength(1);
    sources[0]!.emit('job', '{"name":"j1"}');
    sources[0]!.emit('run', 'not json');
    sources[0]!.emit('run', '{"id":"r1"}');
    expect(jobs).toHaveBeenCalledWith({ name: 'j1' });
    expect(runs).toHaveBeenCalledTimes(1);
    off1();
    expect(sources[0]!.closed).toBe(false);
    off2();
    expect(sources[0]!.closed).toBe(true);
  });

  it('reports unavailable when the first connection is closed by the browser', () => {
    const { hub, sources } = setup();
    const statuses: EventStatus[] = [];
    hub.subscribe({}, undefined, (s) => statuses.push(s));
    sources[0]!.readyState = 2;
    sources[0]!.onerror?.({});
    expect(statuses).toEqual(['connecting', 'unavailable']);
  });

  it('reconnects with backoff and resyncs once after a drop', () => {
    vi.useFakeTimers();
    const { hub, sources } = setup();
    const resync = vi.fn();
    hub.subscribe({}, resync);
    sources[0]!.onopen?.({});
    expect(resync).not.toHaveBeenCalled();
    sources[0]!.readyState = 2;
    sources[0]!.onerror?.({});
    vi.advanceTimersByTime(1000);
    expect(sources).toHaveLength(2);
    sources[1]!.onopen?.({});
    expect(resync).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });
});
