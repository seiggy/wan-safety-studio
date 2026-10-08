import { plural } from '../../lib/format';

export const MAX_SCENES = 5;
export const TAKE_OPTIONS = [1, 3, 5] as const;

export const sceneCountLabel = (scenes: number): string => `${scenes} of ${MAX_SCENES}`;

export const totalJobs = (scenes: number, takes: number): number => scenes * takes;

export const batchSummary = (scenes: number, takes: number, mode?: string | null): string => {
  const base = `${plural(scenes, 'scene')} × ${plural(takes, 'video')} = ${plural(totalJobs(scenes, takes), 'GPU job')}`;
  return mode ? `${base} · ${mode}` : base;
};

export const generateLabel = (busy: boolean, scenes: number, takes: number): string =>
  busy ? 'Submitting…' : `Generate ${plural(totalJobs(scenes, takes), 'video')}`;

export const imageRequirementLabel = (index: number): string =>
  index === 0 ? 'Required' : 'Optional · reuses Scene 1’s image';

export const canAddScene = (scenes: number): boolean => scenes < MAX_SCENES;
