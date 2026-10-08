import { describe, expect, it } from 'vitest';
import { humanizeStage, progressLabel } from './progress';

describe('progress labels', () => {
  it('humanizes known stages and passes unknown ones through', () => {
    expect(humanizeStage('sample_high_noise')).toBe('Sampling (high noise)');
    expect(humanizeStage('vae_decode')).toBe('Decoding video');
    expect(humanizeStage('mystery_stage')).toBe('mystery_stage');
  });

  it('adds step counts only when maxSteps is positive', () => {
    expect(progressLabel({ stage: 'sample_high_noise', step: 5, maxSteps: 10 })).toBe('Sampling (high noise) 5/10');
    expect(progressLabel({ stage: 'save_video', step: 0, maxSteps: 0 })).toBe('Saving video');
    expect(progressLabel(null)).toBeNull();
  });
});
