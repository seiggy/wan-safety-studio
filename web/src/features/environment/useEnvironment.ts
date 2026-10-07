import { useCallback, useEffect, useRef, useState } from 'react';
import { api, errorMessage, isUnauthorized } from '../../api/client';
import type { EnvironmentStatus } from '../../api/types';

const REFRESH_MS = 30000;

export interface EnvironmentState {
  environment: EnvironmentStatus | null;
  error: string | null;
  loading: boolean;
  refresh: () => Promise<void>;
}

export function useEnvironment(): EnvironmentState {
  const [environment, setEnvironment] = useState<EnvironmentStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const mounted = useRef(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      const data = await api.get<EnvironmentStatus>('/api/status');
      if (!mounted.current) return;
      setEnvironment(data);
      setError(null);
    } catch (caught) {
      if (!mounted.current || isUnauthorized(caught)) return;
      setEnvironment(null);
      setError(errorMessage(caught));
    } finally {
      if (mounted.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    void refresh();
    const timer = setInterval(() => void refresh(), REFRESH_MS);
    return () => {
      mounted.current = false;
      clearInterval(timer);
    };
  }, [refresh]);

  return { environment, error, loading, refresh };
}
