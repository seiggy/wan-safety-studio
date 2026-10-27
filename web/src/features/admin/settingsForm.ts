import type { SettingsView, StudioSettings } from '../../api/studio';

export interface RegionDraft {
  maxConcurrentJobs: string;
  maxJobMinutes: string;
  priority: string;
}

export interface SettingsDraft {
  maxConcurrentJobs: string;
  queueMoveMinutes: string;
  regions: Record<string, RegionDraft>;
}

export const toDraft = ({ settings }: SettingsView): SettingsDraft => ({
  maxConcurrentJobs: String(settings.maxConcurrentJobs),
  queueMoveMinutes: String(settings.queueMoveMinutes),
  regions: Object.fromEntries(
    Object.entries(settings.regions).map(([region, value]) => [
      region,
      { maxConcurrentJobs: String(value.maxConcurrentJobs), maxJobMinutes: String(value.maxJobMinutes), priority: String(value.priority) },
    ]),
  ),
});

const whole = (text: string): number | null => (/^\d{1,6}$/.test(text.trim()) ? Number(text.trim()) : null);

/** Mirrors the server's ceilings so mistakes show before saving; the server stays the authority and re-checks everything. */
export function validateDraft(draft: SettingsDraft, view: SettingsView, version = view.settings.version): { errors: string[]; request: StudioSettings | null } {
  const errors: string[] = [];
  const regions: StudioSettings['regions'] = {};
  let capacity = 0;
  for (const [region, value] of Object.entries(draft.regions)) {
    const policy = view.policy[region];
    const jobs = whole(value.maxConcurrentJobs);
    const minutes = whole(value.maxJobMinutes);
    const priority = whole(value.priority);
    if (!policy) {
      errors.push(`${region}: not a deployed region.`);
      continue;
    }
    if (jobs === null || jobs < 1 || jobs > policy.maxNodes) errors.push(`${region}: concurrent jobs must be 1 to ${policy.maxNodes} (the deployed node limit).`);
    if (minutes === null || minutes < 1 || minutes > policy.maxJobMinutes) {
      errors.push(`${region}: job timeout must be 1 to ${policy.maxJobMinutes} minutes (the deployed limit, never above ${view.absoluteMaxJobMinutes}).`);
    }
    if (priority === null) errors.push(`${region}: priority must be 0 or higher (lower is tried first).`);
    if (jobs !== null && minutes !== null && priority !== null) {
      regions[region] = { maxConcurrentJobs: jobs, maxJobMinutes: minutes, priority };
      capacity += jobs;
    }
  }
  const global = whole(draft.maxConcurrentJobs);
  const queue = whole(draft.queueMoveMinutes);
  if (global === null || global < 1 || global > capacity) errors.push(`Global concurrent jobs must be 1 to ${capacity} (the sum of the regional limits).`);
  if (queue === null || queue < 1 || queue > 120) errors.push('Queue wait before moving must be 1 to 120 minutes.');
  if (errors.length > 0 || global === null || queue === null) return { errors, request: null };
  return { errors, request: { version, maxConcurrentJobs: global, queueMoveMinutes: queue, regions } };
}