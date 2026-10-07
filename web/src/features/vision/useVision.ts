import { useCallback, useEffect, useRef, useState } from 'react';
import { errorMessage } from '../../api/client';
import * as vision from '../../api/vision';
import type { BoundingBox, VisionDataset, VisionProject, VisionVideo } from '../../api/vision';

const EXTRACT_FPS = 2;

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('Frame image failed to load.'));
    image.src = src;
  });
}

export function useVision(active: boolean) {
  const [projects, setProjects] = useState<string[]>([]);
  const [selected, setSelected] = useState('');
  const [project, setProject] = useState<VisionProject | null>(null);
  const [videos, setVideos] = useState<VisionVideo[]>([]);
  const [datasets, setDatasets] = useState<VisionDataset[]>([]);
  const [activeVideo, setActiveVideo] = useState<string | null>(null);
  const [frames, setFrames] = useState<string[]>([]);
  const [frame, setFrame] = useState<string | null>(null);
  const [boxes, setBoxes] = useState<BoundingBox[]>([]);
  const [image, setImage] = useState<HTMLImageElement | null>(null);
  const [doneFrames, setDoneFrames] = useState<ReadonlySet<string>>(new Set());
  const [message, setMessage] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [extracting, setExtracting] = useState<string | null>(null);
  const selectedRef = useRef('');
  selectedRef.current = selected;

  const run = useCallback(async (action: () => Promise<void>) => {
    try {
      setError(null);
      await action();
    } catch (caught) {
      setError(errorMessage(caught));
    }
  }, []);

  const refreshSide = async (name: string) => {
    const [nextVideos, nextDatasets] = await Promise.all([vision.listVideos(name), vision.listDatasets(name)]);
    setVideos(nextVideos);
    setDatasets(nextDatasets);
  };

  const openProject = async (name: string) => {
    setSelected(name);
    setActiveVideo(null);
    setFrame(null);
    setFrames([]);
    setImage(null);
    setBoxes([]);
    setMessage('');
    if (!name) {
      setProject(null);
      return;
    }
    const detail = await vision.getProject(name);
    setProject(detail);
    await refreshSide(name);
  };

  const loadProjects = async (select: string) => {
    const names = await vision.listProjects();
    setProjects(names);
    await openProject(names.includes(select) ? select : '');
  };

  const openFrame = async (name: string, video: string, frameId: string) => {
    const labels = await vision.getLabels(name, video, frameId);
    const loaded = await loadImage(vision.frameImageUrl(name, video, frameId));
    setFrame(frameId);
    setBoxes(labels.boxes);
    setImage(loaded);
    setMessage(labels.reviewed ? 'Reviewed' : 'Not reviewed');
    if (labels.reviewed) setDoneFrames((current) => new Set(current).add(frameId));
  };

  const openVideo = async (name: string, video: string) => {
    const index = await vision.listFrames(name, video);
    setActiveVideo(video);
    setFrames(index);
    setDoneFrames(new Set());
    const first = index[0];
    if (first) await openFrame(name, video, first);
  };

  useEffect(() => {
    if (active) void run(() => loadProjects(selectedRef.current));
    // Reload each time the view is opened; loadProjects only reads refs and stable setters.
  }, [active]); // eslint-disable-line react-hooks/exhaustive-deps

  const labelVideo = (video: VisionVideo) =>
    run(async () => {
      if (!project) return;
      if (!video.frames) {
        setExtracting(video.id);
        try {
          await vision.extractFrames(project.name, video.id, EXTRACT_FPS);
          await refreshSide(project.name);
        } finally {
          setExtracting(null);
        }
      }
      await openVideo(project.name, video.id);
    });

  const selectFrame = (frameId: string) =>
    run(async () => {
      if (project && activeVideo) await openFrame(project.name, activeVideo, frameId);
    });

  const saveReviewed = () =>
    run(async () => {
      if (!project || !activeVideo || !frame) return;
      const toSave = boxes.map((box) => ({ ...box, state: box.state || 'reviewed' }));
      await vision.saveLabels(project.name, activeVideo, frame, toSave);
      setMessage(`Saved ${toSave.length} box(es)`);
      setDoneFrames((current) => new Set(current).add(frame));
      const next = frames[frames.indexOf(frame) + 1];
      if (next) await openFrame(project.name, activeVideo, next);
    });

  const createProject = (name: string, classes: string[]) =>
    run(async () => {
      await vision.createProject(name, classes);
      await loadProjects(name);
    });

  const upload = (file: File) =>
    run(async () => {
      if (!project) return;
      await vision.uploadVideo(project.name, file);
      await refreshSide(project.name);
    });

  const freeze = (valFraction: number) =>
    run(async () => {
      if (!project) return;
      await vision.freezeDataset(project.name, valFraction);
      await refreshSide(project.name);
    });

  return {
    projects,
    selected,
    project,
    videos,
    datasets,
    frames,
    frame,
    boxes,
    image,
    doneFrames,
    message,
    error,
    extracting,
    selectProject: (name: string) => run(() => openProject(name)),
    labelVideo,
    selectFrame,
    saveReviewed,
    createProject,
    upload,
    freeze,
    addBox: (box: BoundingBox) => setBoxes((current) => [...current, box]),
    removeLastBox: () => setBoxes((current) => current.slice(0, -1)),
  };
}
