import { beforeEach, describe, expect, it } from 'vitest';
import type { GenerationMode } from '../../api/modes';
import { loadSavedMode, resolveMode, saveMode, stepsTag } from './modes';

const mode = (id: string, available = true, model = '14B', steps = 20): GenerationMode => ({
  id, label: id, description: '', model, steps, splitStep: 0, cfg: 1, shift: 5, sampler: 'euler', fps: 16, available,
});
const modes = [mode('draft', true, '5B', 20), mode('fast'), mode('turbo', false), mode('prime')];

describe('resolveMode', () => {
  it('prefers a saved available mode, then the default', () => {
    expect(resolveMode(modes, 'prime', 'fast')).toBe('fast');
    expect(resolveMode(modes, 'prime', 'turbo')).toBe('prime');
    expect(resolveMode(modes, 'prime', null)).toBe('prime');
  });

  it('falls back to the first available mode', () => {
    expect(resolveMode([mode('turbo', false), mode('fast')], 'turbo', null)).toBe('fast');
    expect(resolveMode([mode('turbo', false)], 'turbo', null)).toBeNull();
  });
});

describe('persistence and tags', () => {
  beforeEach(() => localStorage.clear());

  it('round-trips the saved mode', () => {
    expect(loadSavedMode()).toBeNull();
    saveMode('fast');
    expect(loadSavedMode()).toBe('fast');
  });

  it('formats step tags', () => {
    expect(stepsTag(modes[0]!)).toBe('5B · 20');
    expect(stepsTag(modes[1]!)).toBe('20 steps');
  });
});
