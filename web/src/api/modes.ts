import { ApiError, api } from './client';

export interface GenerationMode {
  id: string;
  label: string;
  description: string;
  model: string;
  steps: number;
  splitStep: number;
  cfg: number;
  shift: number;
  sampler: string;
  fps: number;
  available: boolean;
}

export interface ModesResponse {
  defaultMode: string;
  modes: GenerationMode[];
}

/** Resolves to null when the host has no mode support (404, e.g. the legacy Azure portal). */
export const fetchModes = (): Promise<ModesResponse | null> =>
  api.get<ModesResponse>('/api/modes').catch((error: unknown) => {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  });
