import type { BoundingBox } from '../../api/vision';

export const MIN_BOX_SIZE = 4;

export interface Point {
  x: number;
  y: number;
}

export interface DragBox {
  cls: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Maps a pointer position in client space to canvas pixel space. */
export function canvasPoint(
  clientX: number,
  clientY: number,
  rect: { left: number; top: number; width: number; height: number },
  canvasWidth: number,
  canvasHeight: number,
): Point {
  return {
    x: ((clientX - rect.left) * canvasWidth) / rect.width,
    y: ((clientY - rect.top) * canvasHeight) / rect.height,
  };
}

/** Rectangle spanned by the drag origin and the current point, regardless of drag direction. */
export function dragBox(cls: string, origin: Point, current: Point): DragBox {
  return {
    cls,
    x: Math.min(current.x, origin.x),
    y: Math.min(current.y, origin.y),
    w: Math.abs(current.x - origin.x),
    h: Math.abs(current.y - origin.y),
  };
}

/** Turns a finished drag into a reviewed box clamped to the canvas, or null when it is too small. */
export function finalizeBox(drag: DragBox, canvasWidth: number, canvasHeight: number): BoundingBox | null {
  if (drag.w <= MIN_BOX_SIZE || drag.h <= MIN_BOX_SIZE) return null;
  const x = Math.max(0, drag.x);
  const y = Math.max(0, drag.y);
  return {
    cls: drag.cls,
    x,
    y,
    w: Math.min(drag.w, canvasWidth - x),
    h: Math.min(drag.h, canvasHeight - y),
    state: 'reviewed',
  };
}
