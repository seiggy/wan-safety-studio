export type ThemePreference = 'system' | 'light' | 'dark';

export const ACCENTS = ['blue', 'teal', 'green', 'violet', 'magenta', 'orange'] as const;
export type Accent = (typeof ACCENTS)[number];

export interface Appearance {
  theme: ThemePreference;
  accent: Accent;
}

export const DEFAULT_APPEARANCE: Appearance = { theme: 'system', accent: 'blue' };

const KEY = 'wan-studio.appearance';

export const ACCENT_LABELS: Record<Accent, string> = {
  blue: 'Blue',
  teal: 'Teal',
  green: 'Green',
  violet: 'Violet',
  magenta: 'Magenta',
  orange: 'Orange',
};

export const THEME_LABELS: Record<ThemePreference, string> = {
  system: 'System',
  light: 'Light',
  dark: 'Dark',
};

/** Parses a stored value; anything unknown falls back to the default for that field. */
export function parseAppearance(raw: string | null): Appearance {
  if (!raw) return DEFAULT_APPEARANCE;
  try {
    const value = JSON.parse(raw) as Partial<Appearance> | null;
    return {
      theme: value?.theme === 'light' || value?.theme === 'dark' || value?.theme === 'system' ? value.theme : DEFAULT_APPEARANCE.theme,
      accent: ACCENTS.find((accent) => accent === value?.accent) ?? DEFAULT_APPEARANCE.accent,
    };
  } catch {
    return DEFAULT_APPEARANCE;
  }
}

export function loadAppearance(storage: Pick<Storage, 'getItem'> = window.localStorage): Appearance {
  try {
    return parseAppearance(storage.getItem(KEY));
  } catch {
    return DEFAULT_APPEARANCE;
  }
}

export function saveAppearance(value: Appearance, storage: Pick<Storage, 'setItem'> = window.localStorage): void {
  try {
    storage.setItem(KEY, JSON.stringify(value));
  } catch {
    // Private mode or blocked storage: the choice still applies for this page view.
  }
}

/** "system" removes data-theme so the OS preference (color-scheme: light dark) decides; light/dark pin it. */
export function applyAppearance(value: Appearance, root: HTMLElement = document.documentElement): void {
  if (value.theme === 'system') delete root.dataset.theme;
  else root.dataset.theme = value.theme;
  root.dataset.accent = value.accent;
}
