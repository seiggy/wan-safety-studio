import { describe, expect, it } from 'vitest';
import { ACCENTS, DEFAULT_APPEARANCE, applyAppearance, parseAppearance } from './appearance';

describe('appearance', () => {
  it('falls back to defaults for missing, corrupt or unknown values', () => {
    expect(parseAppearance(null)).toEqual(DEFAULT_APPEARANCE);
    expect(parseAppearance('not json')).toEqual(DEFAULT_APPEARANCE);
    expect(parseAppearance('{"theme":"neon","accent":"plaid"}')).toEqual(DEFAULT_APPEARANCE);
    expect(parseAppearance('{"theme":"dark","accent":"teal"}')).toEqual({ theme: 'dark', accent: 'teal' });
  });

  it('pins light or dark, and lets the system decide for "system"', () => {
    const root = document.createElement('html');
    applyAppearance({ theme: 'dark', accent: 'violet' }, root);
    expect(root.dataset.theme).toBe('dark');
    expect(root.dataset.accent).toBe('violet');
    applyAppearance({ theme: 'system', accent: 'blue' }, root);
    expect(root.dataset.theme).toBeUndefined();
    expect(ACCENTS).toContain(root.dataset.accent);
  });
});
