import type { GenerationMode } from '../../api/modes';

export const MODE_STORAGE_KEY = 'wan-mode';
export const UNAVAILABLE_HELP = 'Workflow file missing on the server';

export const modeLabel = (id: string | null | undefined): string | null =>
  id ? id.charAt(0).toUpperCase() + id.slice(1) : null;

export const stepsTag = (mode: GenerationMode): string =>
  mode.model.includes('5B') ? `5B · ${mode.steps}` : `${mode.steps} steps`;

/** Saved choice if still available, else the server default if available, else the first available mode. */
export function resolveMode(modes: GenerationMode[], defaultMode: string, saved: string | null): string | null {
  const usable = (id: string | null | undefined) => modes.find((mode) => mode.id === id && mode.available)?.id;
  return usable(saved) ?? usable(defaultMode) ?? modes.find((mode) => mode.available)?.id ?? null;
}

export function loadSavedMode(): string | null {
  try {
    return localStorage.getItem(MODE_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function saveMode(id: string): void {
  try {
    localStorage.setItem(MODE_STORAGE_KEY, id);
  } catch {
    // Persistence is best-effort.
  }
}
