import { useEffect, useState } from 'react';
import type { RegionStatus, SettingsView, StudioSettings } from '../../api/studio';
import { toDraft, validateDraft, type RegionDraft, type SettingsDraft } from './settingsForm';
import { useStudioAdmin } from './useStudioAdmin';

interface Props {
  active: boolean;
}

const PHASE_LABEL: Record<RegionStatus['phase'], string> = {
  off: 'Off',
  starting: 'Starting',
  armed: 'Armed',
  stopping: 'Stopping',
  unknown: 'Unknown - run Stop to verify release',
};

function RegionCard({ region, busy, onSelect, onStart }: {
  region: RegionStatus;
  busy: boolean;
  onSelect: (artifactId: string) => void;
  onStart: (spend: boolean, nsg: boolean) => void;
}) {
  const [artifact, setArtifact] = useState('');
  const [spend, setSpend] = useState(false);
  const [nsg, setNsg] = useState(false);
  const fenced = region.phase !== 'off';
  return (
    <section className="admin-region" aria-label={`Region ${region.region}`}>
      <h2>{region.region} <span className={`admin-phase ${region.phase}`}>{PHASE_LABEL[region.phase]}</span></h2>
      <dl className="environment-details">
        <div><dt>Approved release</dt><dd>{region.selectedArtifactId ?? 'None selected'}</dd></div>
        <div><dt>Armed release</dt><dd>{region.armedArtifactId ?? 'None'}</dd></div>
        <div><dt>Compute</dt><dd>{region.live}{region.nodes !== null ? ` (${region.nodes} node${region.nodes === 1 ? '' : 's'})` : ''}</dd></div>
        <div><dt>Active jobs</dt><dd>{region.activeJobs.length > 0 ? region.activeJobs.join(', ') : 'None'}</dd></div>
        <div><dt>Egress</dt><dd>{region.natPresent || region.publicIpPresent ? `${region.natPresent ? 'NAT present' : ''}${region.publicIpPresent ? ' Public IP present' : ''}`.trim() : 'None owned'}</dd></div>
      </dl>
      {region.detail && <p className="notice warning">{region.detail}</p>}
      {region.blockers.length > 0 && <ul className="notice warning">{region.blockers.map((blocker) => <li key={blocker}>{blocker}</li>)}</ul>}
      {region.error && <p className="notice error" role="alert">{region.error}</p>}
      <div className="admin-actions">
        <label>
          Release artifact ID
          <input value={artifact} onChange={(event) => setArtifact(event.target.value)} autoComplete="off" spellCheck={false} />
        </label>
        <button type="button" className="secondary-button" disabled={busy || artifact.trim() === '' || fenced} onClick={() => onSelect(artifact.trim())}>
          Select release
        </button>
      </div>
      <fieldset className="admin-start" disabled={busy}>
        <legend>Start (spends money)</legend>
        <label><input type="checkbox" checked={spend} onChange={(event) => setSpend(event.target.checked)} /> I approve GPU spend in {region.region} until I stop it.</label>
        <label><input type="checkbox" checked={nsg} onChange={(event) => setNsg(event.target.checked)} /> Customer-managed NSG rules, private DNS and egress are confirmed.</label>
        <button
          type="button"
          className="primary-button"
          disabled={!spend || fenced || !region.selectedArtifactId}
          onClick={() => {
            if (window.confirm(`Start GPU compute in ${region.region}? Billing starts when a job runs.`)) onStart(spend, nsg);
          }}
        >
          Start {region.region}
        </button>
      </fieldset>
    </section>
  );
}

function SettingsForm({ view, busy, onSave }: { view: SettingsView; busy: boolean; onSave: (request: StudioSettings) => Promise<boolean> }) {
  // The version the form was loaded at is what Save sends, so an edit made on top of changed settings is refused by the server.
  const [base, setBase] = useState(() => ({ version: view.settings.version, draft: toDraft(view) }));
  const [draft, setDraft] = useState<SettingsDraft>(base.draft);
  const [errors, setErrors] = useState<string[]>([]);
  const dirty = JSON.stringify(draft) !== JSON.stringify(base.draft);
  const stale = view.settings.version !== base.version;

  const load = () => {
    const next = { version: view.settings.version, draft: toDraft(view) };
    setBase(next);
    setDraft(next.draft);
    setErrors([]);
  };

  // Untouched forms follow the latest settings; an edited form is never overwritten.
  useEffect(() => {
    if (stale && !dirty) load();
  }, [view]);
  const setRegion = (region: string, current: RegionDraft, field: keyof RegionDraft, value: string) =>
    setDraft({ ...draft, regions: { ...draft.regions, [region]: { ...current, [field]: value } } });
  return (
    <form
      className="admin-settings"
      onSubmit={(event) => {
        event.preventDefault();
        const { errors: found, request } = validateDraft(draft, view, base.version);
        setErrors(found);
        if (request) void onSave(request).then((saved) => saved && setBase({ version: base.version + 1, draft }));
      }}
    >
      <h2>Routing and limits</h2>
      {view.storedInvalid && <p className="notice error" role="alert">The stored settings are invalid and ignored. Saving replaces them.</p>}
      {!view.stored && <p className="notice">Using limits derived from the deployed policy. Saving stores your own.</p>}
      {stale && dirty && (
        <p className="notice warning">
          Settings changed elsewhere (version {view.settings.version}). Saving your edit will be refused. <button type="button" className="secondary-button" onClick={load}>Discard my edit and load</button>
        </p>
      )}
      <div className="admin-grid">
        <label>Global concurrent jobs<input inputMode="numeric" value={draft.maxConcurrentJobs} onChange={(event) => setDraft({ ...draft, maxConcurrentJobs: event.target.value })} /></label>
        <label>Wait in queue before moving (minutes)<input inputMode="numeric" value={draft.queueMoveMinutes} onChange={(event) => setDraft({ ...draft, queueMoveMinutes: event.target.value })} /></label>
      </div>
      {Object.entries(draft.regions).map(([region, value]) => (
        <fieldset key={region} className="admin-grid">
          <legend>{region}</legend>
          <label>Concurrent jobs (max {view.policy[region]?.maxNodes})<input inputMode="numeric" value={value.maxConcurrentJobs} onChange={(event) => setRegion(region, value, 'maxConcurrentJobs', event.target.value)} /></label>
          <label>Job timeout, minutes (max {view.policy[region]?.maxJobMinutes})<input inputMode="numeric" value={value.maxJobMinutes} onChange={(event) => setRegion(region, value, 'maxJobMinutes', event.target.value)} /></label>
          <label>Priority (lower first)<input inputMode="numeric" value={value.priority} onChange={(event) => setRegion(region, value, 'priority', event.target.value)} /></label>
        </fieldset>
      ))}

      <p className="hint">Limits can only be set at or below what was deployed, and never above {view.absoluteMaxJobMinutes} minutes. Each attempt uses its region's timeout; moving a queued job adds waiting time and possible cost. A timeout is not a dollar budget.</p>
      {errors.length > 0 && <ul className="notice error" role="alert">{errors.map((message) => <li key={message}>{message}</li>)}</ul>}
      <button type="submit" className="primary-button" disabled={busy || !dirty}>Save settings</button>
    </form>
  );
}

export function AdminView({ active }: Props) {
  const admin = useStudioAdmin(active);
  const busy = admin.busy !== null;
  return (
    <section id="admin-view" className="view" hidden={!active}>
      <div className="page-heading">
        <h1>Administration</h1>
        <div>
          <button type="button" className="secondary-button" disabled={busy} onClick={() => void admin.refresh()}>Refresh</button>{' '}
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => {
              if (window.confirm('Stop every region? Jobs are cancelled and owned compute and egress are deleted.')) void admin.stopAll();
            }}
          >
            Stop all regions
          </button>
        </div>
      </div>
      {admin.error && <p className="notice error" role="alert">{admin.error}</p>}
      {admin.notice && <p className="notice" role="status">{admin.notice}</p>}
      <p className="notice">
        Release builds, promotion and foundation changes run as GitHub Actions deployment workflows, not from this site.
        Select a release here after promotion has recorded it for the region.
      </p>
      {admin.status?.regions.map((region) => (
        <RegionCard
          key={region.region}
          region={region}
          busy={busy}
          onSelect={(artifact) => void admin.selectRelease(region.region, artifact)}
          onStart={(spend, nsg) => void admin.start(region.region, spend, nsg)}
        />
      ))}
      {admin.settings && <SettingsForm view={admin.settings} busy={busy} onSave={admin.saveSettings} />}
    </section>
  );
}
