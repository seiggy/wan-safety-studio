import { describe, expect, it } from 'vitest';
import type { SettingsView } from '../../api/studio';
import { toDraft, validateDraft } from './settingsForm';

const view: SettingsView = {
  settings: {
    version: 3,
    maxConcurrentJobs: 2,
    queueMoveMinutes: 10,
    regions: { eastus2: { maxConcurrentJobs: 1, maxJobMinutes: 120, priority: 0 }, westus3: { maxConcurrentJobs: 1, maxJobMinutes: 60, priority: 1 } },
  },
  stored: true,
  storedInvalid: false,
  policy: { eastus2: { maxNodes: 1, maxJobMinutes: 120 }, westus3: { maxNodes: 2, maxJobMinutes: 90 } },
  absoluteMaxJobMinutes: 120,
};

describe('settings form', () => {
  it('round-trips an unchanged draft into a request carrying the loaded version', () => {
    const draft = toDraft(view);
    const { errors, request } = validateDraft(draft, view);
    expect(errors).toEqual([]);
    expect(request).toEqual({ ...view.settings });
  });

  it('accepts a lower per-region timeout carrying the version the form was loaded at', () => {
    const draft = toDraft(view);
    draft.regions.westus3!.maxJobMinutes = '45';
    const request = validateDraft(draft, view, 2).request;
    expect(request?.regions.westus3?.maxJobMinutes).toBe(45);
    expect(request?.version).toBe(2);
  });

  it('rejects values above the deployed ceilings with the limit named', () => {
    const draft = toDraft(view);
    draft.regions.westus3!.maxJobMinutes = '91';
    draft.regions.eastus2!.maxConcurrentJobs = '2';
    draft.queueMoveMinutes = '121';
    const { errors, request } = validateDraft(draft, view);
    expect(request).toBeNull();
    expect(errors).toContain('westus3: job timeout must be 1 to 90 minutes (the deployed limit, never above 120).');
    expect(errors).toContain('eastus2: concurrent jobs must be 1 to 1 (the deployed node limit).');
    expect(errors).toContain('Queue wait before moving must be 1 to 120 minutes.');
  });

  it('rejects non-numeric input and a global limit above the regional capacity', () => {
    const draft = toDraft(view);
    draft.regions.eastus2!.priority = 'high';
    draft.maxConcurrentJobs = '9';
    const { errors } = validateDraft(draft, view);
    expect(errors).toContain('eastus2: priority must be 0 or higher (lower is tried first).');
    expect(errors.some((error) => error.startsWith('Global concurrent jobs must be 1 to'))).toBe(true);
  });
});
