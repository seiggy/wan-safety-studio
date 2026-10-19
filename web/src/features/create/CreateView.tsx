import { Suspense, lazy, useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { errorMessage } from '../../api/client';
import { fetchModes, type ModesResponse } from '../../api/modes';
import type { EnvironmentStatus } from '../../api/types';
import { generationHelp } from '../environment/messages';
import { mergeJobs, summarizeJobs } from './batch';
import { applyHelperTool, formStateBlock, type HelperForm } from './helper/formTools';
import { useVideoHelper } from './helper/useVideoHelper';
import { UNAVAILABLE_HELP, loadSavedMode, modeLabel, resolveMode, saveMode, stepsTag } from './modes';
import { OutputPanel } from './OutputPanel';
import { SceneEditor, type Scene } from './SceneEditor';
import {
  MAX_SCENES,
  TAKE_OPTIONS,
  batchSummary,
  canAddScene,
  defaultVideoName,
  generateLabel,
  sceneCountLabel,
} from './scenes';
import { useJobTracker } from './useJobTracker';

const HelperPanel = lazy(() => import('./helper/HelperPanel').then((module) => ({ default: module.HelperPanel })));

const ASPECT_RATIOS = [
  { value: '16:9', size: '1024 × 576' },
  { value: '4:3', size: '896 × 672' },
  { value: '1:1', size: '768 × 768' },
] as const;

interface Props {
  active: boolean;
  environment: EnvironmentStatus | null;
  onError: (message: string) => void;
  onClearError: () => void;
}

let sceneSerial = 0;
const newScene = (): Scene => ({ key: ++sceneSerial, prompt: '', file: null, name: null });

export function CreateView({ active, environment, onError, onClearError }: Props) {
  const [scenes, setScenes] = useState<Scene[]>(() => [newScene()]);
  const [aspect, setAspect] = useState('16:9');
  const [duration, setDuration] = useState(5);
  const [takes, setTakes] = useState<number>(1);
  const [negativePrompt, setNegativePrompt] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [override, setOverride] = useState<string | null>(null);
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [modeInfo, setModeInfo] = useState<ModesResponse | null>(null);
  const [mode, setMode] = useState<string | null>(null);
  const focusRequest = useRef<string | null>(null);
  const [helperOpen, setHelperOpen] = useState(false);
  const { batch, openJobs, submit } = useJobTracker(environment?.storage, onError);

  const busy = submitting || (batch !== null && !batch.done);
  const jobs = mergeJobs(batch?.jobs ?? [], openJobs);
  const selected = jobs.find((job) => job.name === selectedName && job.video) ?? jobs.find((job) => job.video) ?? null;
  const summary = summarizeJobs(batch, jobs);
  const generationEnabled = environment?.generationEnabled === true;

  useEffect(() => {
    let alive = true;
    fetchModes()
      .then((info) => {
        if (!alive || !info) return;
        setModeInfo(info);
        setMode(resolveMode(info.modes, info.defaultMode, loadSavedMode()));
      })
      .catch(() => undefined);
    return () => {
      alive = false;
    };
  }, []);

  const chooseMode = (id: string) => {
    setMode(id);
    saveMode(id);
  };
  const selectedMode = modeInfo?.modes.find((item) => item.id === mode);
  const unavailableModes = modeInfo?.modes.filter((item) => !item.available) ?? [];

  useEffect(() => {
    if (focusRequest.current) {
      document.getElementById(focusRequest.current)?.focus();
      focusRequest.current = null;
    }
  }, [scenes]);

  const addScene = () => {
    if (!canAddScene(scenes.length)) return;
    const scene = newScene();
    focusRequest.current = `scene-${scene.key}-prompt`;
    setScenes([...scenes, scene]);
  };

  const removeScene = (key: number) => {
    const remaining = scenes.filter((scene) => scene.key !== key);
    focusRequest.current = canAddScene(remaining.length) ? 'add-scene' : 'generate';
    setScenes(remaining);
  };

  const updateScene = (key: number, patch: Partial<Scene>) =>
    setScenes((current) => current.map((scene) => (scene.key === key ? { ...scene, ...patch } : scene)));

  // The helper acts on the form through the same state the controls use. `live` is rewritten on every render and on each
  // applied tool call, so several tool calls arriving in one tick build on each other.
  const scenesRef = useRef(scenes);
  scenesRef.current = scenes;
  const live = useRef<HelperForm | undefined>(undefined);
  live.current = { scenes: scenes.map((scene) => ({ key: scene.key, prompt: scene.prompt, hasImage: scene.file !== null })), negativePrompt, aspect, duration, takes, mode };
  const availableModes = modeInfo?.modes.filter((item) => item.available).map((item) => item.id) ?? [];

  const applyHelperAction = useCallback(
    (name: string, args: unknown) => {
      const current = live.current;
      if (!current) return { summary: 'The form is not ready.', error: true };
      const outcome = applyHelperTool(current, name, args, { aspects: ASPECT_RATIOS.map((ratio) => ratio.value), modes: availableModes });
      if ('error' in outcome) return { summary: `Couldn’t apply ${name.replaceAll('_', ' ')}: ${outcome.error}`, error: true };
      const next = outcome.form;
      live.current = next;
      setScenes((existing) =>
        next.scenes.map((scene) => {
          const kept = existing.find((item) => item.key === scene.key);
          return kept ? { ...kept, prompt: scene.prompt } : { ...newScene(), prompt: scene.prompt };
        }),
      );
      setNegativePrompt(next.negativePrompt);
      setAspect(next.aspect);
      setDuration(next.duration);
      setTakes(next.takes);
      if (next.mode && next.mode !== current.mode) chooseMode(next.mode);
      return { summary: outcome.summary };
    },
    // chooseMode and availableModes only change with modeInfo.
    [modeInfo],
  );
  const helper = useVideoHelper({
    applyTool: applyHelperAction,
    formState: () => (live.current ? formStateBlock(live.current, availableModes) : ''),
    sceneImages: () => scenesRef.current.flatMap((scene, index) => (scene.file ? [{ scene: index + 1, file: scene.file }] : [])),
  });

  const onSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (busy || !environment?.generationEnabled || !event.currentTarget.reportValidity()) return;
    const files = scenes.map((scene) => scene.file);
    const bytes = files.reduce((sum, file) => sum + (file?.size ?? 0), 0);
    if (!files[0] || bytes > environment.maxUploadMb * 1048576) {
      onError(`Add a Scene 1 image. All reference images together must be no larger than ${environment.maxUploadMb} MB.`);
      return;
    }
    const form = new FormData();
    form.set('profile', 'wan');
    scenes.forEach((scene, index) => {
      form.append('prompt', scene.prompt);
      form.append('scene_name', (scene.name ?? defaultVideoName(scene.file ?? files[0] ?? null, index)).trim());
      const file = files[index];
      if (file) form.append(`input_image_${index}`, file);
    });
    form.set('aspect_ratio', aspect);
    form.set('duration_seconds', String(duration));
    form.set('takes', String(takes));
    form.set('negative_prompt', negativePrompt);
    if (modeInfo && mode) form.set('mode', mode);
    setSubmitting(true);
    onClearError();
    setOverride('Uploading reference images…');
    try {
      await submit(form);
      setSelectedName(null);
      setOverride(null);
    } catch (error) {
      onError(errorMessage(error));
      setOverride('Submission was not confirmed. Check the job list before retrying; no automatic retry was made.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <section id="create-view" className="view fill" hidden={!active}>
      <div className="page-heading">
        <h1>Create a safety video</h1>
        {helper.status?.enabled !== false && (
          <button type="button" className="secondary-button" aria-pressed={helperOpen} onClick={() => setHelperOpen(!helperOpen)}>
            Video helper
          </button>
        )}
      </div>
      <div className={`creation-layout${helperOpen ? ' with-helper' : ''}`}>
        <form id="create-form" className="creation-form" onSubmit={(event) => void onSubmit(event)}>
          <div className="form-section-heading">
            <h2>Scenes</h2>
            <span id="scene-count" className="field-help">{sceneCountLabel(scenes.length)}</span>
          </div>
          <ol id="scene-list" className={`scene-list${scenes.length === 1 ? ' single' : ''}`}>
            {scenes.map((scene, index) => (
              <SceneEditor
                key={scene.key}
                scene={scene}
                index={index}
                defaultName={defaultVideoName(scene.file ?? scenes[0]?.file ?? null, index)}
                onlyScene={scenes.length === 1}
                onChange={(patch) => updateScene(scene.key, patch)}
                onRemove={() => removeScene(scene.key)}
                onDropError={onError}
              />
            ))}
          </ol>
          <button id="add-scene" type="button" className="secondary-button add-scene" hidden={scenes.length >= MAX_SCENES} onClick={addScene}>
            + Add scene
          </button>
          <fieldset className="field">
            <legend>Aspect ratio</legend>
            <div className="segmented">
              {ASPECT_RATIOS.map(({ value, size }) => (
                <label key={value}>
                  <input type="radio" name="aspect_ratio" value={value} checked={aspect === value} onChange={() => setAspect(value)} />
                  <span>{value}<small>{size}</small></span>
                </label>
              ))}
            </div>
          </fieldset>
          <div className="field">
            <label htmlFor="duration">Duration <output id="duration-value" htmlFor="duration">{duration} s</output></label>
            <input
              id="duration"
              name="duration_seconds"
              type="range"
              min={5}
              max={10}
              step={1}
              value={duration}
              aria-describedby="duration-help"
              onChange={(event) => setDuration(Number(event.target.value))}
            />
            <div className="range-scale" aria-hidden="true"><span>5 s</span><span>10 s</span></div>
            <p id="duration-help" className="field-help">
              Video Safety Studio is tuned for 5-second clips at 16 fps. Longer clips take roughly twice as long at 10 s and can drift.
            </p>
          </div>
          <fieldset className="field">
            <legend>Videos per scene</legend>
            <div className="segmented">
              {TAKE_OPTIONS.map((value) => (
                <label key={value}>
                  <input type="radio" name="takes" value={value} checked={takes === value} onChange={() => setTakes(value)} />
                  <span>{value}</span>
                </label>
              ))}
            </div>
            <p className="field-help">Each video is a separate GPU job with its own random seed.</p>
          </fieldset>
          {modeInfo && (
            <fieldset className="field">
              <legend>Quality</legend>
              <div className="segmented">
                {modeInfo.modes.map((item) => (
                  <label key={item.id}>
                    <input
                      type="radio"
                      name="mode"
                      value={item.id}
                      checked={mode === item.id}
                      disabled={!item.available}
                      onChange={() => chooseMode(item.id)}
                    />
                    <span>{item.label}<small>{stepsTag(item)}</small></span>
                  </label>
                ))}
              </div>
              {selectedMode && <p className="field-help">{selectedMode.description}</p>}
              {unavailableModes.length > 0 && (
                <p className="field-help">{unavailableModes.map((item) => item.label).join(', ')}: {UNAVAILABLE_HELP}</p>
              )}
            </fieldset>
          )}
          <details className="advanced">
            <summary>Advanced options</summary>
            <div className="field">
              <label htmlFor="negative-prompt">Negative prompt</label>
              <textarea
                id="negative-prompt"
                name="negative_prompt"
                rows={3}
                maxLength={4000}
                placeholder="Optional. Applies to every scene. Describe elements to avoid."
                value={negativePrompt}
                onChange={(event) => setNegativePrompt(event.target.value)}
              />
              <p className="field-help">An empty field stays empty; it does not silently restore workflow defaults.</p>
            </div>
          </details>
          <p id="batch-summary" className="batch-summary" aria-live="polite">{batchSummary(scenes.length, takes, modeInfo ? modeLabel(mode) : null)}</p>
          <p id="generation-help" className="field-help">{generationHelp(environment)}</p>
          <button id="generate" className="primary-button" type="submit" disabled={busy || !generationEnabled}>
            {generateLabel(busy, scenes.length, takes)}
          </button>
        </form>
        <OutputPanel
          jobs={jobs}
          selected={selected}
          badge={summary.badge}
          badgeKind={summary.kind}
          message={override ?? summary.message ?? ''}
          showProgress={busy}
          onSelect={setSelectedName}
        />
        {helperOpen && (
          <Suspense fallback={<aside className="helper" aria-busy="true" />}>
          <HelperPanel
            status={helper.status}
            messages={helper.messages}
            running={helper.running}
            error={helper.error}
            onSend={(text) => void helper.send(text)}
            onStop={helper.stop}
            onClear={helper.clear}
            onClose={() => setHelperOpen(false)}
          />
          </Suspense>
        )}
      </div>
    </section>
  );
}
