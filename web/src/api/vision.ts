import { api } from './client';

const BASE = '/api/vision';

export interface BoundingBox {
  cls: string;
  x: number;
  y: number;
  w: number;
  h: number;
  state?: string;
}

export interface VisionProject {
  name: string;
  classes: string[];
}

export interface VisionVideo {
  id: string;
  duration: number;
  frames: number;
}

export interface VisionDataset {
  version: string;
  counts: { train: { images: number }; val: { images: number } };
  warnings: string[];
}

export interface FrameLabels {
  boxes: BoundingBox[];
  reviewed: boolean;
}

export const listProjects = () => api.get<{ projects: string[] }>(`${BASE}/projects`).then((r) => r.projects);

export const getProject = (name: string) => api.get<VisionProject>(`${BASE}/projects/${name}`);

export const createProject = (name: string, classes: string[]) =>
  api.post<unknown>(`${BASE}/projects`, { json: { name, classes } });

export const listVideos = (project: string) =>
  api.get<{ videos: VisionVideo[] }>(`${BASE}/projects/${project}/videos`).then((r) => r.videos);

export const listDatasets = (project: string) =>
  api.get<{ datasets: VisionDataset[] }>(`${BASE}/projects/${project}/datasets`).then((r) => r.datasets);

export const uploadVideo = (project: string, file: File) => {
  const form = new FormData();
  form.append('file', file);
  return api.post<unknown>(`${BASE}/projects/${project}/videos`, { body: form });
};

export const extractFrames = (project: string, video: string, fps: number) =>
  api.post<unknown>(`${BASE}/projects/${project}/videos/${video}/extract`, { json: { fps } });

export const listFrames = (project: string, video: string) =>
  api.get<{ frames: string[] }>(`${BASE}/projects/${project}/frames/${video}`).then((r) => r.frames);

export const getLabels = (project: string, video: string, frame: string) =>
  api.get<FrameLabels>(`${BASE}/projects/${project}/labels/${video}/${frame}`);

export const saveLabels = (project: string, video: string, frame: string, boxes: BoundingBox[]) =>
  api.put<unknown>(`${BASE}/projects/${project}/labels/${video}/${frame}`, { json: { reviewed: true, boxes } });

export const freezeDataset = (project: string, valFraction: number) =>
  api.post<unknown>(`${BASE}/projects/${project}/datasets`, { json: { valFraction } });

export const frameImageUrl = (project: string, video: string, frame: string) =>
  `${BASE}/projects/${project}/frames/${video}/${frame}.jpg`;

export interface Trainer {
  name: string;
  version: string;
  image: string;
  licenseCode: string;
  licenseWeights: string;
  presets: string[];
  defaultPreset: string;
}

export type RunStatus = 'Queued' | 'Preparing' | 'Running' | 'Uploading' | 'Completed' | 'Failed';

export interface RunMetrics {
  map50: number;
  map50_95: number;
  per_class: Record<string, number>;
}

export interface TrainingRun {
  id: string;
  project: string;
  dataset: string;
  trainer: string;
  trainerVersion: string;
  preset: string;
  epochs: number | null;
  compute: string;
  status: RunStatus;
  error: string | null;
  metrics: RunMetrics | null;
  createdAt: string;
}

export interface StartRunRequest {
  dataset: string;
  trainer: string;
  preset?: string;
  epochs?: number;
}

export const listTrainers = () => api.get<{ trainers: Trainer[] }>(`${BASE}/trainers`).then((r) => r.trainers);

export const startRun = (project: string, request: StartRunRequest) =>
  api.post<TrainingRun>(`${BASE}/projects/${project}/runs`, { json: request });

export const listRuns = (project: string) =>
  api.get<{ runs: TrainingRun[] }>(`${BASE}/projects/${project}/runs`).then((r) => r.runs);
