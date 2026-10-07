import { describe, expect, it } from 'vitest';
import type { TrainingRun } from '../../api/vision';
import { hasActiveRun, runBadgeKind } from './runs';

describe('runs', () => {
  it('maps status to badge kind', () => {
    expect(runBadgeKind('Completed')).toBe('success');
    expect(runBadgeKind('Failed')).toBe('error');
    expect(runBadgeKind('Running')).toBe('neutral');
  });

  it('keeps polling only while a run is non-terminal', () => {
    const run = (status: TrainingRun['status']) => ({ status }) as TrainingRun;
    expect(hasActiveRun([run('Completed'), run('Uploading')])).toBe(true);
    expect(hasActiveRun([run('Completed'), run('Failed')])).toBe(false);
  });
});
