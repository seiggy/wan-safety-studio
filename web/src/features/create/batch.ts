import { plural } from '../../lib/format';
import type { Batch, Job } from '../../api/types';

export type BadgeKind = 'neutral' | 'success' | 'error';

const TERMINAL_STATUSES = new Set(['Completed', 'Failed', 'Canceled', 'Cancelled', 'NotResponding']);

export const isTerminal = (status: string | undefined): boolean => status !== undefined && TERMINAL_STATUSES.has(status);

export const badgeKindForStatus = (status: string | undefined): BadgeKind =>
  status === 'Completed' ? 'success' : isTerminal(status) ? 'error' : 'neutral';

export const MAX_VIDEO_CHECKS = 3;

export const needsVideoLookup = (job: Job): boolean =>
  job.status === 'Completed' && !job.video && (job.videoChecks ?? 0) < MAX_VIDEO_CHECKS;

/** Own batch jobs first, then any other open jobs. */
export const mergeJobs = (own: Job[], open: Job[]): Job[] => [
  ...own,
  ...open.filter((job) => !own.some((known) => known.name === job.name)),
];

export interface JobSummary {
  badge: string;
  kind: BadgeKind;
  message: string | null;
}

export function summarizeJobs(batch: Batch | null, jobs: Job[]): JobSummary {
  if (!batch) {
    return {
      badge: jobs.length ? plural(jobs.length, 'open job') : '',
      kind: 'neutral',
      message: jobs.length ? 'Unfinished jobs, including earlier submissions. They run one after another.' : null,
    };
  }
  const own = batch.jobs;
  const completed = own.filter((job) => job.status === 'Completed').length;
  const failed = own.filter((job) => isTerminal(job.status) && job.status !== 'Completed').length;
  const finished = batch.done && own.every((job) => isTerminal(job.status));
  const message = batch.error
    ? batch.error
    : !batch.done
      ? 'Submitting your jobs one at a time. You can leave this page; submission continues on the server.'
      : finished
        ? completed
          ? 'All jobs in this request finished. Select a completed video to preview it.'
          : 'No job in this request completed. Review the failed jobs; they will not be resubmitted.'
        : 'Jobs accepted. They run one after another, after any earlier unfinished jobs listed here.';
  return {
    badge: batch.done ? `${completed} of ${batch.total} complete` : `Submitting ${own.length} of ${batch.total}`,
    kind: failed || batch.error ? 'error' : finished ? 'success' : 'neutral',
    message,
  };
}

const STORAGE_KEY = 'wan-current-batch';

export function saveBatch(batch: Batch): void {
  // Signed video URLs stay in memory only; they are fetched again after a reload.
  const jobs = batch.jobs.map(({ video: _video, videoChecks: _checks, ...job }) => job);
  sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ ...batch, jobs }));
}

export function loadSavedBatch(): Batch | null {
  try {
    const saved = sessionStorage.getItem(STORAGE_KEY);
    return saved ? (JSON.parse(saved) as Batch) : null;
  } catch {
    return null;
  }
}

export const clearSavedBatch = (): void => sessionStorage.removeItem(STORAGE_KEY);
