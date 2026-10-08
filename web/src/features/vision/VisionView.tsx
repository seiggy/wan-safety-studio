import { useState, type FormEvent } from 'react';
import { categoryIndex } from '../../lib/categories';
import { FrameCanvas } from './FrameCanvas';
import { TrainPanel } from './TrainPanel';
import { useVision } from './useVision';

interface Props {
  active: boolean;
}

const parseClasses = (value: string): string[] =>
  value.split(',').map((c) => c.trim()).filter(Boolean);

export function VisionView({ active }: Props) {
  const v = useVision(active);
  const [newName, setNewName] = useState('');
  const [newClasses, setNewClasses] = useState('');
  const [valFraction, setValFraction] = useState('0.2');
  const [activeClass, setActiveClass] = useState('');
  const classes = v.project?.classes ?? [];
  const currentClass = classes.includes(activeClass) ? activeClass : (classes[0] ?? '');

  const onCreate = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    await v.createProject(newName.trim(), parseClasses(newClasses));
    setNewName('');
    setNewClasses('');
  };

  const onUpload = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = event.currentTarget;
    const file = form.elements.namedItem('file');
    const chosen = file instanceof HTMLInputElement ? file.files?.[0] : undefined;
    if (!chosen) return;
    await v.upload(chosen);
    form.reset();
  };

  return (
    <section id="vision-view" className="view" hidden={!active}>
      <div className="page-heading">
        <h1>Vision datasets</h1>
      </div>
      <p id="vision-error" className="notice error" role="alert" hidden={!v.error}>{v.error}</p>
      <div className="vision-bar">
        <label htmlFor="vision-project">Project</label>
        <select id="vision-project" value={v.selected} onChange={(event) => void v.selectProject(event.target.value)}>
          <option value="">Select a project…</option>
          {v.projects.map((name) => <option key={name} value={name}>{name}</option>)}
        </select>
        <form id="vision-new" className="vision-inline" onSubmit={(event) => void onCreate(event)}>
          <input
            id="vision-new-name"
            placeholder="project-name"
            required
            pattern="[a-z0-9][a-z0-9\-]{1,62}"
            aria-label="New project name"
            value={newName}
            onChange={(event) => setNewName(event.target.value)}
          />
          <input
            id="vision-new-classes"
            placeholder="classes, comma separated (e.g. leak,fire,smoke)"
            required
            aria-label="Classes"
            value={newClasses}
            onChange={(event) => setNewClasses(event.target.value)}
          />
          <button className="secondary-button" type="submit">Create project</button>
        </form>
      </div>
      <div id="vision-body" className="vision-body" hidden={!v.selected}>
        <aside className="vision-side">
          <h2>Videos</h2>
          <form id="vision-upload" className="vision-inline" onSubmit={(event) => void onUpload(event)}>
            <input id="vision-file" name="file" type="file" accept="video/*" required aria-label="Video file" />
            <button className="secondary-button" type="submit">Upload</button>
          </form>
          <ul id="vision-videos" className="vision-list">
            {v.videos.length === 0 && <li>No videos yet.</li>}
            {v.videos.map((video) => (
              <li key={video.id}>
                <span>{`${video.id} · ${video.duration.toFixed(1)}s`}</span>
                <button type="button" className="text-button" disabled={v.extracting === video.id} onClick={() => void v.labelVideo(video)}>
                  {video.frames ? `Label (${video.frames})` : 'Extract frames'}
                </button>
              </li>
            ))}
          </ul>
          <h2>Dataset</h2>
          <div className="vision-inline">
            <label htmlFor="vision-val">Validation share</label>
            <input
              id="vision-val"
              type="number"
              min={0}
              max={0.9}
              step={0.1}
              value={valFraction}
              onChange={(event) => setValFraction(event.target.value)}
            />
            <button id="vision-freeze" className="secondary-button" type="button" onClick={() => void v.freeze(Number(valFraction))}>
              Freeze version
            </button>
          </div>
          <ul id="vision-datasets" className="vision-list">
            {v.datasets.map((dataset) => (
              <li key={dataset.version} title={dataset.warnings.length ? dataset.warnings.join('\n') : undefined}>
                <span>{`${dataset.version} · train ${dataset.counts.train.images} / val ${dataset.counts.val.images}`}</span>
              </li>
            ))}
          </ul>
        </aside>
        <section className="vision-main" aria-label="Label editor">
          <div id="vision-strip" className="vision-strip" role="listbox" aria-label="Frames">
            {v.frames.map((frame) => (
              <button
                key={frame}
                type="button"
                role="option"
                aria-selected={frame === v.frame}
                className={v.doneFrames.has(frame) ? 'done' : undefined}
                onClick={() => void v.selectFrame(frame)}
              >
                {frame.replace(/^0+/, '')}
              </button>
            ))}
          </div>
          {classes.length > 0 && (
          <ul className="class-legend" aria-label="Class colors">
            {classes.map((name) => <li key={name} className={`cat-${categoryIndex(classes, name) + 1}`}>{name}</li>)}
          </ul>
        )}
        <div className="vision-tools">
            <label htmlFor="vision-class">Class</label>
            <select id="vision-class" value={currentClass} onChange={(event) => setActiveClass(event.target.value)}>
              {classes.map((name) => <option key={name} value={name}>{name}</option>)}
            </select>
            <button id="vision-undo" className="text-button" type="button" onClick={v.removeLastBox}>Remove last box</button>
            <button id="vision-save" className="primary-button" type="button" onClick={() => void v.saveReviewed()}>
              Save as reviewed
            </button>
            <span id="vision-msg" role="status">{v.message}</span>
          </div>
          <FrameCanvas image={v.image} boxes={v.boxes} classes={classes} activeClass={currentClass} onAddBox={v.addBox} />
        </section>
      </div>
      <TrainPanel project={v.project?.name ?? null} active={active} datasets={v.datasets} />
    </section>
  );
}
