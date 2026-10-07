import { useEffect, useRef, useState } from 'react';
import { api, errorMessage, isUnauthorized } from '../../api/client';
import type { Batch, GalleryResponse, Job, JobsResponse, SubmitResponse } from '../../api/types';
import { trustedUrl } from '../../lib/urls';
import { isTerminal, loadSavedBatch, needsVideoLookup, saveBatch } from './batch';

const JOB_POLL_MS = 15000;
const BATCH_POLL_MS = 2000;

export interface JobTracker {
  batch: Batch | null;
  openJobs: Job[];
  submit: (form: FormData) => Promise<void>;
}

/**
 * Tracks the current submission batch and unfinished Azure ML jobs.
 * Mutable state lives in refs so timer callbacks never read stale values.
 */
export function useJobTracker(storage: string | undefined, onError: (message: string) => void): JobTracker {
  const [batch, setBatch] = useState<Batch | null>(null);
  const [openJobs, setOpenJobs] = useState<Job[]>([]);
  const batchRef = useRef<Batch | null>(null);
  const openRef = useRef<Job[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const storageRef = useRef(storage);
  const errorRef = useRef(onError);
  const alive = useRef(true);
  storageRef.current = storage;
  errorRef.current = onError;

  const commit = (next: Batch): void => {
    batchRef.current = next;
    saveBatch(next);
    setBatch(next);
  };

  const refreshJobs = async (): Promise<void> => {
    clearTimeout(timer.current);
    try {
      openRef.current = (await api.get<JobsResponse>('/api/jobs')).jobs;
      if (!alive.current) return;
      setOpenJobs(openRef.current);
    } catch (error) {
      if (isUnauthorized(error) || !alive.current) return;
      errorRef.current(`${errorMessage(error)} Unfinished jobs could not be listed; do not submit the same request again.`);
    }
    const start = batchRef.current;
    let jobs: Job[] = start?.jobs ?? [];
    if (start) {
      jobs = [...jobs];
      for (let i = 0; i < jobs.length; i += 1) {
        const job = jobs[i];
        if (!job || isTerminal(job.status)) continue;
        try {
          const open = openRef.current.find((item) => item.name === job.name);
          jobs[i] = { ...job, ...(open ?? (await api.get<Job>(`/api/jobs/${encodeURIComponent(job.name)}`))) };
        } catch (error) {
          errorRef.current(`${errorMessage(error)} Job status could not be read; do not submit the same request again.`);
          break;
        }
      }
      if (jobs.some(needsVideoLookup)) {
        try {
          const gallery = await api.get<GalleryResponse>('/api/gallery?refresh=1');
          jobs = jobs.map((job) =>
            needsVideoLookup(job)
              ? {
                  ...job,
                  videoChecks: (job.videoChecks ?? 0) + 1,
                  video: trustedUrl(gallery.items.find((item) => item.job_name === job.name)?.video_url, 'blob', storageRef.current),
                }
              : job,
          );
        } catch (error) {
          errorRef.current(errorMessage(error));
        }
      }
      if (!alive.current) return;
      if (batchRef.current?.id === start.id) commit({ ...batchRef.current, jobs });
    }
    if (!alive.current) return;
    if (openRef.current.length || jobs.some((job) => !isTerminal(job.status) || needsVideoLookup(job))) {
      timer.current = setTimeout(() => void refreshJobs(), JOB_POLL_MS);
    }
  };

  const pollBatch = async (): Promise<void> => {
    clearTimeout(timer.current);
    const current = batchRef.current;
    if (!current) {
      await refreshJobs();
      return;
    }
    if (!current.done) {
      let next: Batch;
      try {
        const state = await api.get<Batch>(`/api/batches/${encodeURIComponent(current.id)}`);
        const added = state.jobs.filter((job) => !current.jobs.some((known) => known.name === job.name));
        next = { ...current, jobs: [...current.jobs, ...added], total: state.total, done: state.done, error: state.error };
      } catch (error) {
        if (isUnauthorized(error) || !alive.current) return;
        next = {
          ...current,
          done: true,
          error: `${errorMessage(error)} Jobs listed here were submitted; check the video library for any others.`,
        };
      }
      if (!alive.current) return;
      commit(next);
      if (!next.done) {
        timer.current = setTimeout(() => void pollBatch(), BATCH_POLL_MS);
        return;
      }
    }
    await refreshJobs();
  };

  const submit = async (form: FormData): Promise<void> => {
    const result = await api.post<SubmitResponse>('/api/submit', { body: form });
    commit({ ...result.batch, jobs: [...result.batch.jobs] });
    void pollBatch();
  };

  useEffect(() => {
    alive.current = true;
    const saved = loadSavedBatch();
    batchRef.current = saved;
    setBatch(saved);
    void pollBatch();
    return () => {
      alive.current = false;
      clearTimeout(timer.current);
    };
    // pollBatch only reads refs, so a single mount-time run is intended.
  }, []);

  return { batch, openJobs, submit };
}
