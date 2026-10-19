import { useEffect, useRef, useState } from 'react';
import {
  ACCENTS,
  ACCENT_LABELS,
  THEME_LABELS,
  applyAppearance,
  loadAppearance,
  saveAppearance,
  type Accent,
  type Appearance,
  type ThemePreference,
} from '../lib/appearance';

const THEMES: ThemePreference[] = ['system', 'light', 'dark'];

export function AppearanceMenu() {
  const [appearance, setAppearance] = useState<Appearance>(() => loadAppearance());

  const menu = useRef<HTMLDetailsElement>(null);

  useEffect(() => {
    const close = (event: Event): void => {
      const element = menu.current;
      if (!element?.open) return;
      if (event instanceof KeyboardEvent) {
        if (event.key !== 'Escape') return;
        element.querySelector('summary')?.focus();
      } else if (event.target instanceof Node && element.contains(event.target)) return;
      element.open = false;
    };
    document.addEventListener('pointerdown', close);
    document.addEventListener('keydown', close);
    return () => {
      document.removeEventListener('pointerdown', close);
      document.removeEventListener('keydown', close);
    };
  }, []);

  const change = (next: Appearance): void => {
    setAppearance(next);
    applyAppearance(next);
    saveAppearance(next);
  };

  return (
    <details ref={menu} className="appearance">
      <summary>
        <span className="appearance-swatch" aria-hidden="true" />
        Appearance
      </summary>
      <div className="appearance-panel">
        <fieldset>
          <legend>Theme</legend>
          <div className="segmented">
            {THEMES.map((theme) => (
              <label key={theme}>
                <input
                  type="radio"
                  name="theme"
                  value={theme}
                  checked={appearance.theme === theme}
                  onChange={() => change({ ...appearance, theme })}
                />
                <span>{THEME_LABELS[theme]}</span>
              </label>
            ))}
          </div>
        </fieldset>
        <fieldset>
          <legend>Accent color</legend>
          <div className="accent-grid">
            {ACCENTS.map((accent: Accent) => (
              <label key={accent} className="accent-option" data-accent={accent}>
                <input
                  type="radio"
                  name="accent"
                  value={accent}
                  checked={appearance.accent === accent}
                  onChange={() => change({ ...appearance, accent })}
                />
                <span>{ACCENT_LABELS[accent]}</span>
              </label>
            ))}
          </div>
        </fieldset>
      </div>
    </details>
  );
}
