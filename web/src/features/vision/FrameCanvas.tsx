import { useEffect, useRef, useState, type PointerEvent } from 'react';
import type { BoundingBox } from '../../api/vision';
import { canvasPoint, dragBox, finalizeBox, type DragBox, type Point } from './geometry';

const COLORS = ['#e81123', '#0f6cbd', '#107c10', '#ca5010', '#8764b8', '#00b7c3'];

interface Props {
  image: HTMLImageElement | null;
  boxes: BoundingBox[];
  classes: string[];
  activeClass: string;
  onAddBox: (box: BoundingBox) => void;
}

const colorOf = (classes: string[], cls: string): string =>
  COLORS[Math.max(0, classes.indexOf(cls)) % COLORS.length] ?? '#e81123';

export function FrameCanvas({ image, boxes, classes, activeClass, onAddBox }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const origin = useRef<Point | null>(null);
  const [drag, setDrag] = useState<DragBox | null>(null);
  const dragRef = useRef<DragBox | null>(null);

  const update = (next: DragBox | null) => {
    dragRef.current = next;
    setDrag(next);
  };

  useEffect(() => {
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx) return;
    if (!image) {
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      return;
    }
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    ctx.drawImage(image, 0, 0);
    ctx.lineWidth = Math.max(2, canvas.width / 300);
    ctx.font = `${Math.max(12, canvas.width / 50)}px sans-serif`;
    const all: (BoundingBox | DragBox)[] = drag ? [...boxes, drag] : boxes;
    for (const box of all) {
      const color = colorOf(classes, box.cls);
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.strokeRect(box.x, box.y, box.w, box.h);
      const auto = 'state' in box && box.state === 'auto' ? ' (auto)' : '';
      ctx.fillText(box.cls + auto, box.x + 3, Math.max(14, box.y - 4));
    }
  }, [image, boxes, drag, classes]);

  const pointFor = (event: PointerEvent<HTMLCanvasElement>): Point => {
    const canvas = event.currentTarget;
    return canvasPoint(event.clientX, event.clientY, canvas.getBoundingClientRect(), canvas.width, canvas.height);
  };

  const onPointerDown = (event: PointerEvent<HTMLCanvasElement>) => {
    if (!image) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    origin.current = pointFor(event);
    update(dragBox(activeClass, origin.current, origin.current));
  };

  const onPointerMove = (event: PointerEvent<HTMLCanvasElement>) => {
    if (!origin.current) return;
    update(dragBox(activeClass, origin.current, pointFor(event)));
  };

  const onPointerUp = (event: PointerEvent<HTMLCanvasElement>) => {
    const finished = dragRef.current;
    origin.current = null;
    update(null);
    if (!finished) return;
    const box = finalizeBox(finished, event.currentTarget.width, event.currentTarget.height);
    if (box) onAddBox(box);
  };

  return (
    <canvas
      id="vision-canvas"
      ref={canvasRef}
      width={640}
      height={360}
      aria-label="Frame: drag to draw a box"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
    />
  );
}
