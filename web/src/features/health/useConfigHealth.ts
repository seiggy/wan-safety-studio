import { useEffect, useState } from 'react';

export interface ConfigIssue {
  setting: string;
  severity: 'error' | 'warning';
  problem: string;
  example: string;
  where: string;
}

export interface ConfigHealthReport {
  status: 'ready' | 'ready-with-warnings' | 'misconfigured';
  mode: string;
  issues: ConfigIssue[];
}

/** Plain fetch: the health page is anonymous and must work when sign-in itself is not configured. */
export function useConfigHealth(): { report: ConfigHealthReport | null; error: string | null } {
  const [report, setReport] = useState<ConfigHealthReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let current = true;
    fetch('/api/health/config', { cache: 'no-store' })
      .then((response) => (response.ok ? (response.json() as Promise<ConfigHealthReport>) : Promise.reject(new Error(`The server answered ${response.status}.`))))
      .then((data) => current && setReport(data))
      .catch((caught: unknown) => current && setError(caught instanceof Error ? caught.message : 'The configuration report could not be loaded.'));
    return () => {
      current = false;
    };
  }, []);
  return { report, error };
}
