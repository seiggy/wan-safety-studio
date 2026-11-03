import { describe, expect, it } from 'vitest';
import { LIVE_LABEL, PHASE_LABEL } from './regionLabels';

describe('region labels', () => {
  it('names every phase and live state the server sends in plain words', () => {
    expect(Object.keys(PHASE_LABEL).sort()).toEqual(['armed', 'off', 'starting', 'stopping', 'unknown']);
    expect(Object.keys(LIVE_LABEL).sort()).toEqual(['IDLE', 'OFF', 'PARTIAL', 'RUNNING', 'UNREADABLE']);
    for (const label of [...Object.values(PHASE_LABEL), ...Object.values(LIVE_LABEL)]) {
      expect(label).not.toMatch(/verify release|^[A-Z]+$/);
    }
  });

  it('shows progress for a running Start or Stop and asks for Stop only when the state is unknown', () => {
    expect(PHASE_LABEL.starting).toBe('Starting…');
    expect(PHASE_LABEL.stopping).toBe('Stopping…');
    expect(PHASE_LABEL.unknown).toBe('Needs Stop');
  });
});
