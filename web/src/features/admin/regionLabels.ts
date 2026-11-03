import type { RegionStatus } from '../../api/studio';

export const PHASE_LABEL: Record<RegionStatus['phase'], string> = {
  off: 'Off',
  starting: 'Starting…',
  armed: 'Armed',
  stopping: 'Stopping…',
  unknown: 'Needs Stop',
};

/** Live compute state in plain words (the server sends a short code). */
export const LIVE_LABEL: Record<string, string> = {
  OFF: 'None',
  IDLE: 'Ready, no nodes running',
  RUNNING: 'Running',
  PARTIAL: 'Being created or removed',
  UNREADABLE: 'Could not be read',
};
