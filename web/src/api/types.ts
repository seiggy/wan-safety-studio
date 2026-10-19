export interface SessionInfo {
  name: string;
  csrfToken: string;
}

export interface EnvironmentStatus {
  /** 'local' = ComfyUI + local/Azurite storage, no Azure ML; 'azureml' = the deployed Azure ML workspace. */
  mode?: 'local' | 'azureml';
  generationEnabled: boolean;
  computeState: string;
  prepared: boolean;
  armed: boolean;
  workspace: string;
  resourceGroup: string;
  storage: string;
  compute: string;
  version?: string | null;
  maxJobMinutes: number;
  maxUploadMb: number;
  checkedAt: string;
}

export interface JobProgress {
  stage: string;
  step: number;
  maxSteps: number;
}

export interface Job {
  name: string;
  status?: string;
  appQueueSeconds?: number | null;
  error?: string | null;
  progress?: JobProgress | null;
  timing?: unknown;
  display_name?: string;
  studio_url?: string | null;
  scene?: number;
  take?: number;
  /** Signed video URL; kept in memory only and never persisted. */
  mode?: string | null;
  video?: string | null;
  videoChecks?: number;
}

export interface Batch {
  id: string;
  total: number;
  done: boolean;
  error?: string | null;
  jobs: Job[];
}

export interface SubmitResponse {
  batch: Batch;
}

export interface JobsResponse {
  jobs: Job[];
}

export interface GalleryItem {
  job_name: string;
  display_name?: string;
  video_url?: string | null;
  width?: number;
  height?: number;
  duration_seconds?: number;
  prompt?: string;
  mode?: string | null;
}

export interface ArchivedResponse {
  archived: string[];
}

export interface GalleryResponse {
  items: GalleryItem[];
  count: number;
  scanned_jobs: number;
}
