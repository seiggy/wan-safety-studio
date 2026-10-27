export type RegionPhaseName = 'off' | 'starting' | 'armed' | 'stopping' | 'unknown';

export interface RegionStatus {
  region: string;
  phase: RegionPhaseName;
  selectedArtifactId: string | null;
  armedArtifactId: string | null;
  detail: string | null;
  live: string;
  nodes: number | null;
  activeJobs: string[];
  natPresent: boolean;
  publicIpPresent: boolean;
  blockers: string[];
  error: string | null;
}

export interface StudioStatus {
  version: number;
  regions: RegionStatus[];
}

export interface RegionSettings {
  maxConcurrentJobs: number;
  maxJobMinutes: number;
  priority: number;
}

export interface StudioSettings {
  version: number;
  maxConcurrentJobs: number;
  queueMoveMinutes: number;
  regions: Record<string, RegionSettings>;
}

export interface SettingsView {
  settings: StudioSettings;
  stored: boolean;
  storedInvalid: boolean;
  policy: Record<string, { maxNodes: number; maxJobMinutes: number }>;
  absoluteMaxJobMinutes: number;
}

export interface StopResponse {
  verified: boolean;
  regions: { region: string; verified: boolean; error: string | null; warnings: string[] }[];
}
