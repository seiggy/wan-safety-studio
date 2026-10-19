import type { RunStatus, TrainingRun } from '../../api/vision';

export type BadgeKind = 'neutral' | 'success' | 'error';

export const isTerminalRun = (status: RunStatus): boolean => status === 'Completed' || status === 'Failed';

export const runBadgeKind = (status: RunStatus): BadgeKind =>
  status === 'Completed' ? 'success' : status === 'Failed' ? 'error' : 'neutral';

export const hasActiveRun = (runs: TrainingRun[]): boolean => runs.some((run) => !isTerminalRun(run.status));

export const newestFirst = (runs: TrainingRun[]): TrainingRun[] =>
  [...runs].sort((a, b) => b.createdAt.localeCompare(a.createdAt));

export const formatMap = (value: number): string => value.toFixed(3);
