import { describe, expect, it } from 'vitest';
import type { Batch } from '../../api/types';
import { mergeJobs, summarizeJobs } from './batch';
import { MAX_SCENES, batchSummary, canAddScene, generateLabel, sceneCountLabel } from './scenes';

describe('scene counts', () => {
  it('describes scenes, takes and GPU jobs with correct plurals', () => {
    expect(batchSummary(1, 1)).toBe('1 scene × 1 video = 1 GPU job');
    expect(batchSummary(3, 5)).toBe('3 scenes × 5 videos = 15 GPU jobs');
    expect(generateLabel(false, 2, 3)).toBe('Generate 6 videos');
    expect(generateLabel(true, 2, 3)).toBe('Submitting…');
  });

  it('caps scenes at the maximum', () => {
    expect(sceneCountLabel(2)).toBe(`2 of ${MAX_SCENES}`);
    expect(canAddScene(MAX_SCENES - 1)).toBe(true);
    expect(canAddScene(MAX_SCENES)).toBe(false);
  });
});

describe('batch summary', () => {
  const batch = (overrides: Partial<Batch>): Batch => ({ id: 'b', total: 2, done: false, jobs: [], ...overrides });

  it('reports submission progress while the batch is open', () => {
    const summary = summarizeJobs(batch({ jobs: [{ name: 'a', status: 'Running' }] }), []);
    expect(summary).toMatchObject({ badge: 'Submitting 1 of 2', kind: 'neutral' });
  });

  it('reports success once every job completed', () => {
    const jobs = [{ name: 'a', status: 'Completed' }, { name: 'b', status: 'Completed' }];
    expect(summarizeJobs(batch({ done: true, jobs }), jobs)).toMatchObject({ badge: '2 of 2 complete', kind: 'success' });
  });

  it('flags failures and batch errors', () => {
    const jobs = [{ name: 'a', status: 'Failed' }];
    expect(summarizeJobs(batch({ done: true, total: 1, jobs }), jobs).kind).toBe('error');
    expect(summarizeJobs(batch({ done: true, error: 'nope' }), []).message).toBe('nope');
  });

  it('lists open jobs when no batch exists and merges without duplicates', () => {
    const open = [{ name: 'x' }, { name: 'y' }];
    expect(summarizeJobs(null, open).badge).toBe('2 open jobs');
    expect(mergeJobs([{ name: 'x', status: 'Completed' }], open).map((job) => job.name)).toEqual(['x', 'y']);
  });
});
