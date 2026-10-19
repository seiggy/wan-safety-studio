import { describe, expect, it } from 'vitest';
import { CATEGORY_COLORS, categoryColor, categoryIndex } from './categories';

describe('categories', () => {
  it('maps classes to stable colors and wraps after six', () => {
    const classes = ['a', 'b', 'c', 'd', 'e', 'f', 'g'];
    expect(categoryColor(classes, 'a')).toBe(CATEGORY_COLORS[0]);
    expect(categoryIndex(classes, 'g')).toBe(0);
    expect(categoryColor(classes, 'unknown')).toBe(CATEGORY_COLORS[0]);
  });
});
