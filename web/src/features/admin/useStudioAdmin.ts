import { useCallback, useEffect, useRef, useState } from 'react';
import { api, errorMessage, isUnauthorized } from '../../api/client';
import type { SettingsView, StudioSettings, StudioStatus } from '../../api/studio';
import { useServerEvents } from '../../api/useServerEvents';

const REFRESH_MS = 30000;

export interface StudioAdminState {
  status: StudioStatus | null;
  settings: SettingsView | null;
  error: string | null;
  notice: string | null;
  busy: string | null;
  refresh: () => Promise<void>;
  selectRelease: (region: string, artifactId: string) => Promise<boolean>;
  start: (region: string, approveGpuSpend: boolean, approveCustomerNsgRules: boolean) => Promise<boolean>;
  stopAll: () => Promise<boolean>;
  saveSettings: (request: StudioSettings) => Promise<boolean>;
}

/** Administration state. Changes arrive as content-free `studio` events; a slow poll covers a dropped stream. */
export function useStudioAdmin(enabled: boolean): StudioAdminState {
  const [status, setStatus] = useState<StudioStatus | null>(null);
  const [settings, setSettings] = useState<SettingsView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const mounted = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const [nextStatus, nextSettings] = await Promise.all([api.get<StudioStatus>('/api/studio/status'), api.get<SettingsView>('/api/studio/settings')]);
      if (!mounted.current) return;
      setStatus(nextStatus);
      setSettings(nextSettings);
      setError(null);
    } catch (caught) {
      if (mounted.current && !isUnauthorized(caught)) setError(errorMessage(caught));
    }
  }, []);

  const run = useCallback(
    async (name: string, action: () => Promise<string>): Promise<boolean> => {
      setBusy(name);
      setNotice(null);
      try {
        const message = await action();
        if (mounted.current) {
          setError(null);
          setNotice(message);
        }
        return true;
      } catch (caught) {
        if (mounted.current && !isUnauthorized(caught)) setError(errorMessage(caught));
        return false;
      } finally {
        if (mounted.current) setBusy(null);
        await refresh();
      }
    },
    [refresh],
  );

  useEffect(() => {
    mounted.current = true;
    if (!enabled) return;
    void refresh();
    const timer = setInterval(() => void refresh(), REFRESH_MS);
    return () => {
      mounted.current = false;
      clearInterval(timer);
    };
  }, [enabled, refresh]);

  useServerEvents({ studio: () => void refresh() }, () => void refresh(), enabled);

  return {
    status,
    settings,
    error,
    notice,
    busy,
    refresh,
    selectRelease: (region, artifactId) =>
      run('release', async () => {
        await api.post(`/api/studio/regions/${encodeURIComponent(region)}/release`, { json: { artifactId } });
        return `Release selected for ${region}.`;
      }),
    start: (region, approveGpuSpend, approveCustomerNsgRules) =>
      run('start', async () => {
        await api.post(`/api/studio/regions/${encodeURIComponent(region)}/start`, { json: { approveGpuSpend, approveCustomerNsgRules } });
        return `${region} is armed. GPU compute is billed while jobs run; Stop when finished.`;
      }),
    stopAll: () =>
      run('stop', async () => {
        await api.post('/api/studio/stop');
        return 'Release verified for every region.';
      }),
    saveSettings: (request) =>
      run('settings', async () => {
        await api.put('/api/studio/settings', {
          json: { expectedVersion: request.version, maxConcurrentJobs: request.maxConcurrentJobs, queueMoveMinutes: request.queueMoveMinutes, regions: request.regions },
        });
        return 'Settings saved. They apply to new submissions; running work is not interrupted.';
      }),
  };
}
