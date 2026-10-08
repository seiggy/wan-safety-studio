import { MAX_SCENES, TAKE_OPTIONS } from '../scenes';

/** What the helper can see and change. `key` is null for a scene the helper just added. */
export interface HelperScene {
  key: number | null;
  prompt: string;
  hasImage: boolean;
}

export interface HelperForm {
  scenes: HelperScene[];
  negativePrompt: string;
  aspect: string;
  duration: number;
  takes: number;
  mode: string | null;
}

export interface HelperLimits {
  aspects: readonly string[];
  modes: readonly string[];
}

export type ToolOutcome = { form: HelperForm; summary: string } | { error: string };

export const DURATION_RANGE = { min: 5, max: 10 } as const;

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
const text = (value: unknown): string | null => (typeof value === 'string' ? value : null);
const whole = (value: unknown): number | null => {
  const number = typeof value === 'string' && value.trim() !== '' ? Number(value) : value;
  return typeof number === 'number' && Number.isInteger(number) ? number : null;
};

/** Mirrors the server-side tool contract (VideoHelperTools.cs); the form never accepts a value its own controls would refuse. */
export function applyHelperTool(form: HelperForm, name: string, rawArgs: unknown, limits: HelperLimits): ToolOutcome {
  const args = isRecord(rawArgs) ? rawArgs : {};
  const sceneIndex = (value: unknown): number | null => {
    const scene = whole(value);
    return scene !== null && scene >= 1 && scene <= form.scenes.length ? scene - 1 : null;
  };

  switch (name) {
    case 'set_scene_prompt': {
      const index = sceneIndex(args.scene);
      const prompt = text(args.prompt);
      if (index === null) return { error: `There is no scene ${String(args.scene)}.` };
      if (!prompt?.trim()) return { error: 'The prompt is empty.' };
      return {
        form: { ...form, scenes: form.scenes.map((scene, i) => (i === index ? { ...scene, prompt } : scene)) },
        summary: `Updated the prompt for scene ${index + 1}`,
      };
    }
    case 'set_negative_prompt':
      return { form: { ...form, negativePrompt: text(args.prompt) ?? '' }, summary: 'Updated the negative prompt' };
    case 'add_scene':
      if (form.scenes.length >= MAX_SCENES) return { error: `A request holds at most ${MAX_SCENES} scenes.` };
      return {
        form: { ...form, scenes: [...form.scenes, { key: null, prompt: text(args.prompt) ?? '', hasImage: false }] },
        summary: `Added scene ${form.scenes.length + 1}`,
      };
    case 'remove_scene': {
      const index = sceneIndex(args.scene);
      if (index === null || index === 0) return { error: 'Only scenes 2 and up can be removed.' };
      return { form: { ...form, scenes: form.scenes.filter((_, i) => i !== index) }, summary: `Removed scene ${index + 1}` };
    }
    case 'set_aspect_ratio': {
      const value = text(args.value);
      if (!value || !limits.aspects.includes(value)) return { error: `Aspect ratio must be one of ${limits.aspects.join(', ')}.` };
      return { form: { ...form, aspect: value }, summary: `Set aspect ratio to ${value}` };
    }
    case 'set_duration': {
      const seconds = whole(args.seconds);
      if (seconds === null || seconds < DURATION_RANGE.min || seconds > DURATION_RANGE.max) {
        return { error: `Duration must be ${DURATION_RANGE.min} to ${DURATION_RANGE.max} seconds.` };
      }
      return { form: { ...form, duration: seconds }, summary: `Set duration to ${seconds} s` };
    }
    case 'set_takes': {
      const count = whole(args.count);
      if (count === null || !(TAKE_OPTIONS as readonly number[]).includes(count)) return { error: `Takes must be ${TAKE_OPTIONS.join(', ')}.` };
      return { form: { ...form, takes: count }, summary: `Set videos per scene to ${count}` };
    }
    case 'set_quality': {
      const mode = text(args.mode);
      if (!mode || !limits.modes.includes(mode)) return { error: `Quality must be one of ${limits.modes.join(', ') || 'none available'}.` };
      return { form: { ...form, mode }, summary: `Set quality to ${mode}` };
    }
    default:
      return { error: `Unknown control ${name}.` };
  }
}

/** The block prepended to each user message so the model works from the live form, not its memory of it. */
export function formStateBlock(form: HelperForm, availableModes: readonly string[]): string {
  const state = {
    scenes: form.scenes.map((scene, index) => ({ scene: index + 1, prompt: scene.prompt, hasReferenceImage: scene.hasImage })),
    negativePrompt: form.negativePrompt,
    aspectRatio: form.aspect,
    durationSeconds: form.duration,
    takesPerScene: form.takes,
    qualityMode: form.mode,
    availableQualityModes: availableModes,
    totalGpuJobs: form.scenes.length * form.takes,
  };
  return `<form_state>\n${JSON.stringify(state, null, 2)}\n</form_state>`;
}
