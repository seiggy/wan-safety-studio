import { describe, expect, it } from 'vitest';
import { canvasPoint, dragBox, finalizeBox } from './geometry';

describe('geometry', () => {
  it('scales client coordinates into canvas pixels', () => {
    const point = canvasPoint(150, 110, { left: 50, top: 10, width: 320, height: 180 }, 640, 360);
    expect(point).toEqual({ x: 200, y: 200 });
  });

  it('normalizes drags made in any direction', () => {
    expect(dragBox('leak', { x: 100, y: 80 }, { x: 40, y: 20 })).toEqual({ cls: 'leak', x: 40, y: 20, w: 60, h: 60 });
  });

  it('rejects boxes that are not larger than the minimum size', () => {
    expect(finalizeBox({ cls: 'a', x: 10, y: 10, w: 4, h: 50 }, 640, 360)).toBeNull();
    expect(finalizeBox({ cls: 'a', x: 10, y: 10, w: 50, h: 4 }, 640, 360)).toBeNull();
  });

  it('clamps boxes to the canvas and marks them reviewed', () => {
    expect(finalizeBox({ cls: 'fire', x: -20, y: 300, w: 100, h: 100 }, 640, 360)).toEqual({
      cls: 'fire',
      x: 0,
      y: 300,
      w: 100,
      h: 60,
      state: 'reviewed',
    });
  });
});
