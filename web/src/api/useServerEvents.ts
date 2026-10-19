import { useEffect, useRef, useState } from 'react';
import { sharedHub, type EventHandler, type EventStatus } from './serverEvents';

/**
 * Subscribes to server-sent events by type over the page's single EventSource.
 * Returns 'unavailable' when the first connection fails permanently so callers can poll instead.
 */
export function useServerEvents(
  handlers: Record<string, EventHandler>,
  onResync?: () => void,
  enabled = true,
): EventStatus {
  const [status, setStatus] = useState<EventStatus>('connecting');
  const handlersRef = useRef(handlers);
  const resyncRef = useRef(onResync);
  handlersRef.current = handlers;
  resyncRef.current = onResync;
  const types = Object.keys(handlers).join(',');

  useEffect(() => {
    if (!enabled) return;
    const stable: Record<string, EventHandler> = {};
    for (const type of types.split(',').filter(Boolean)) {
      stable[type] = (data) => handlersRef.current[type]?.(data);
    }
    return sharedHub().subscribe(stable, () => resyncRef.current?.(), setStatus);
  }, [types, enabled]);

  return enabled ? status : 'connecting';
}
