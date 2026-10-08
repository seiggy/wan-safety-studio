/** Category colours for label classes. Keep in sync with --cat-1..6 in styles/app.css. */
export const CATEGORY_COLORS = ['#ff4d5a', '#4da3ff', '#3ecf5e', '#ff9a3d', '#b794ff', '#2fd6e0'] as const;

export const categoryIndex = (classes: readonly string[], cls: string): number =>
  Math.max(0, classes.indexOf(cls)) % CATEGORY_COLORS.length;

export const categoryColor = (classes: readonly string[], cls: string): string =>
  CATEGORY_COLORS[categoryIndex(classes, cls)] ?? CATEGORY_COLORS[0];
