import type { Job } from '../../api/types';
import { trustedUrl } from '../../lib/urls';
import { badgeKindForStatus, type BadgeKind } from './batch';
import { progressLabel } from './progress';

interface Props {
  jobs: Job[];
  selected: Job | null;
  badge: string;
  badgeKind: BadgeKind;
  message: string;
  showProgress: boolean;
  onSelect: (name: string) => void;
  onNavigate: (view: 'environment') => void;
  local?: boolean;
}

export function OutputPanel({ jobs, selected, badge, badgeKind, message, showProgress, onSelect, onNavigate, local }: Props) {
  return (
    <section className="output-workspace" aria-labelledby="output-heading">
      <div className="section-heading">
        <h2 id="output-heading">Output</h2>
        <span id="job-badge" className={`status-badge ${badgeKind}`}>{badge}</span>
      </div>
      <div className="video-stage">
        <div id="output-empty" className="output-empty" hidden={!!selected}>
          <svg viewBox="0 0 64 48" width="64" height="48" fill="none" aria-hidden="true">
            <rect x="2" y="2" width="60" height="44" rx="4" stroke="currentColor" strokeWidth="2" />
            <path d="m26 15 16 9-16 9V15Z" fill="currentColor" />
          </svg>
          <h3>Your videos will appear here</h3>
          <p>Submit when GPU generation is armed. Each job's progress is listed below; select a completed video to preview it.</p>
        </div>
        <video
          id="output-video"
          key={selected?.name ?? 'none'}
          controls
          preload="metadata"
          hidden={!selected}
          aria-label="Generated video preview"
          src={selected?.video ?? undefined}
        />
      </div>
      <div className="job-detail">
        <p id="job-message" role="status" aria-live="polite">{message}</p>
        <progress id="submit-progress" aria-label="Submitting jobs" hidden={!showProgress} />
        <ol id="job-list" className="job-list" aria-label="Submitted jobs" hidden={!jobs.length}>
          {jobs.map((job) => {
            const studio = trustedUrl(job.studio_url, 'studio', undefined);
            const isSelected = job.name === selected?.name;
            return (
              <li key={job.name} className={`job-row${isSelected ? ' selected' : ''}`}>
                <button
                  type="button"
                  className="job-select"
                  disabled={!job.video}
                  aria-current={isSelected ? 'true' : undefined}
                  onClick={() => onSelect(job.name)}
                >
                  {job.scene ? `Scene ${job.scene} · Video ${job.take}` : job.display_name || 'Earlier request'}
                </button>
                <span className={`status-badge ${badgeKindForStatus(job.status)}`}>{job.status || 'Submitted'}</span>
                {job.status === 'Running' && progressLabel(job.progress) && (
                  <span className="job-progress">
                    {progressLabel(job.progress)}
                    {job.progress && job.progress.maxSteps > 0 && (
                      <progress aria-label="Generation progress" value={job.progress.step} max={job.progress.maxSteps} />
                    )}
                  </span>
                )}
                <code>{job.name}</code>
                {studio && <a href={studio} target="_blank" rel="noopener noreferrer">Open in Azure ML</a>}
              </li>
            );
          })}
        </ol>
        <div className="job-links">
          <a id="download-video" href={selected?.video ?? undefined} target="_blank" rel="noopener noreferrer" hidden={!selected}>
            Open selected video
          </a>
        </div>
      </div>
      <div className="operating-note">
        <h3>Designed for controlled generation</h3>
        <p>
          {local
            ? 'Jobs run on your local ComfyUI, one video per job, one after another. Failed jobs are never resubmitted automatically.'
            : 'Jobs run on the operator-approved private Spot GPU target, one video per job. The single GPU node runs queued jobs one after another. Each job has a two-hour server timeout and is never resubmitted automatically.'}
        </p>
        <button className="text-button" type="button" onClick={() => onNavigate('environment')}>View environment readiness</button>
      </div>
    </section>
  );
}
