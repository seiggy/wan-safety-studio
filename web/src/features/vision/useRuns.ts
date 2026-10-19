import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, errorMessage, isUnauthorized } from '../../api/client';
import { upsertBy } from '../../api/serverEvents';
import { useServerEvents } from '../../api/useServerEvents';
import * as vision from '../../api/vision';
import type { StartRunRequest, Trainer, TrainingRun } from '../../api/vision';
import { hasActiveRun, newestFirst } from './runs';

const POLL_MS = 5000;

/** Trainers are null until known or when the host has no training support (404). */
export function useRuns(project: string | null, active: boolean) {
  const [trainers, setTrainers] = useState<Trainer[] | null>(null);
  const [runs, setRuns] = useState<TrainingRun[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const alive = useRef(true);
  const pollingRef = useRef(false);
  const projectRef = useRef(project);
  projectRef.current = project;

  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      clearTimeout(timer.current);
    };
  }, []);

  useEffect(() => {
    if (!active || trainers !== null) return;
    vision
      .listTrainers()
      .then((list) => alive.current && setTrainers(list))
      .catch((caught: unknown) => {
        if (!(caught instanceof ApiError && caught.status === 404) && !isUnauthorized(caught) && alive.current) {
          setError(errorMessage(caught));
        }
      });
  }, [active, trainers]);

  const refresh = useCallback(async (name: string): Promise<void> => {
    clearTimeout(timer.current);
    try {
      const next = await vision.listRuns(name);
      if (!alive.current) return;
      setRuns(newestFirst(next));
      if (hasActiveRun(next) && pollingRef.current) timer.current = setTimeout(() => void refresh(name), POLL_MS);
    } catch (caught) {
      if (alive.current && !isUnauthorized(caught)) setError(errorMessage(caught));
    }
  }, []);

  const supported = trainers !== null && trainers.length > 0;

  const status = useServerEvents(
    {
      run: (data) => {
        const run = data as TrainingRun;
        if (!run || typeof run.id !== 'string' || run.project !== projectRef.current) return;
        setRuns((current) => newestFirst(upsertBy(current, run, (item) => item.id)));
      },
    },
    () => projectRef.current && void refresh(projectRef.current),
    active && supported,
  );
  pollingRef.current = status === 'unavailable';

  useEffect(() => {
    setRuns([]);
    clearTimeout(timer.current);
    if (project && active && supported) void refresh(project);
    return () => clearTimeout(timer.current);
  }, [project, active, supported, refresh, status === 'unavailable']);

  const start = async (request: StartRunRequest): Promise<void> => {
    if (!project) return;
    setStarting(true);
    setError(null);
    try {
      await vision.startRun(project, request);
      await refresh(project);
    } catch (caught) {
      setError(
        caught instanceof ApiError && caught.status === 409
          ? `The GPU node is busy with another run. Try again when it finishes. ${caught.message}`
          : errorMessage(caught),
      );
    } finally {
      if (alive.current) setStarting(false);
    }
  };

  return { trainers, runs, error, starting, start };
}
