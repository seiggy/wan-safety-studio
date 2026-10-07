import { useState, type FormEvent } from 'react';
import type { VisionDataset } from '../../api/vision';
import { formatMap, runBadgeKind } from './runs';
import { useRuns } from './useRuns';

interface Props {
  project: string | null;
  active: boolean;
  datasets: VisionDataset[];
}

export function TrainPanel({ project, active, datasets }: Props) {
  const { trainers, runs, error, starting, start } = useRuns(project, active);
  const [dataset, setDataset] = useState('');
  const [trainerName, setTrainerName] = useState('');
  const [preset, setPreset] = useState('');
  const [epochs, setEpochs] = useState('');

  if (!project || !trainers || trainers.length === 0) return null;

  const trainer = trainers.find((t) => t.name === trainerName) ?? trainers[0];
  if (!trainer) return null;
  const currentDataset = datasets.some((d) => d.version === dataset) ? dataset : (datasets[0]?.version ?? '');
  const currentPreset = trainer.presets.includes(preset) ? preset : trainer.defaultPreset;

  const onSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!currentDataset) return;
    void start({
      dataset: currentDataset,
      trainer: trainer.name,
      preset: currentPreset,
      ...(epochs ? { epochs: Number(epochs) } : {}),
    });
  };

  return (
    <section id="vision-train" className="vision-train" aria-labelledby="vision-train-heading">
      <h2 id="vision-train-heading">Train</h2>
      <p id="train-error" className="notice error" role="alert" hidden={!error}>{error}</p>
      <form id="train-form" className="train-form" onSubmit={onSubmit}>
        <div className="field">
          <label htmlFor="train-dataset">Dataset</label>
          <select id="train-dataset" value={currentDataset} onChange={(event) => setDataset(event.target.value)}>
            {datasets.length === 0 && <option value="">No frozen datasets</option>}
            {datasets.map((d) => <option key={d.version} value={d.version}>{d.version}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="train-trainer">Trainer</label>
          <select id="train-trainer" value={trainer.name} aria-describedby="train-license" onChange={(event) => setTrainerName(event.target.value)}>
            {trainers.map((t) => <option key={t.name} value={t.name}>{`${t.name} ${t.version}`}</option>)}
          </select>
          <p id="train-license" className="field-help">
            Code licence: {trainer.licenseCode} · Weights licence: {trainer.licenseWeights}
          </p>
        </div>
        <div className="field">
          <label htmlFor="train-preset">Preset</label>
          <select id="train-preset" value={currentPreset} onChange={(event) => setPreset(event.target.value)}>
            {trainer.presets.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="train-epochs">Epochs (optional)</label>
          <input id="train-epochs" type="number" min={1} max={1000} step={1} value={epochs} placeholder="Preset default" onChange={(event) => setEpochs(event.target.value)} />
        </div>
        <button id="train-start" className="primary-button" type="submit" disabled={!currentDataset || starting}>
          {starting ? 'Starting…' : 'Start training'}
        </button>
      </form>
      <table id="train-runs" className="train-runs" aria-label="Training runs">
        <thead>
          <tr><th scope="col">Run</th><th scope="col">Dataset</th><th scope="col">Trainer</th><th scope="col">Status</th><th scope="col">mAP@0.5</th><th scope="col">mAP@0.5:0.95</th></tr>
        </thead>
        <tbody>
          {runs.length === 0 && <tr><td colSpan={6}>No training runs yet.</td></tr>}
          {runs.map((run) => (
            <tr key={run.id}>
              <td><code>{run.id}</code></td>
              <td>{run.dataset}</td>
              <td>{`${run.trainer} / ${run.preset}`}</td>
              <td>
                <span className={`status-badge ${runBadgeKind(run.status)}`}>{run.status}</span>
                {run.status === 'Failed' && run.error && <span className="train-error-text">{run.error}</span>}
              </td>
              <td>{run.status === 'Completed' && run.metrics ? formatMap(run.metrics.map50) : ''}</td>
              <td>
                {run.status === 'Completed' && run.metrics && (
                  <>
                    {formatMap(run.metrics.map50_95)}
                    <details>
                      <summary>Per-class AP</summary>
                      <ul className="vision-list">
                        {Object.entries(run.metrics.per_class).map(([name, ap]) => (
                          <li key={name}><span>{name}</span><span>{formatMap(ap)}</span></li>
                        ))}
                      </ul>
                    </details>
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
