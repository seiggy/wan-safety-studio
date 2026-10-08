import type { JobProgress } from '../../api/types';

const STAGES: Record<string, string> = {
  load_models: 'Loading models',
  model_setup: 'Setting up models',
  text_encode: 'Encoding prompt',
  image_to_latent: 'Preparing image',
  sample_high_noise: 'Sampling (high noise)',
  sample_low_noise: 'Sampling (low noise)',
  vae_decode: 'Decoding video',
  save_video: 'Saving video',
};

export const humanizeStage = (stage: string): string => STAGES[stage] ?? stage;

export function progressLabel(progress: JobProgress | null | undefined): string | null {
  if (!progress) return null;
  const name = humanizeStage(progress.stage);
  return progress.maxSteps > 0 ? `${name} ${progress.step}/${progress.maxSteps}` : name;
}
