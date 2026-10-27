import type { EnvironmentStatus } from '../../api/types';
import { environmentDetails, isLocal } from './messages';

interface Props {
  active: boolean;
  environment: EnvironmentStatus | null;
  loading: boolean;
  onRefresh: () => void;
}

export function EnvironmentView({ active, environment, loading, onRefresh }: Props) {
  return (
    <section id="environment-view" className="view" hidden={!active}>
      <div className="page-heading">
        <h1>Environment</h1>
        <button id="refresh-environment" type="button" className="secondary-button" disabled={loading} onClick={onRefresh}>
          Check connections
        </button>
      </div>
      <dl id="environment-details" className="environment-details">
        {environment &&
          environmentDetails(environment).map(([label, value]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd>{value}</dd>
            </div>
          ))}
      </dl>
      {isLocal(environment) ? (
        <div className="environment-guidance">
          <h2>Local mode</h2>
          <p>Generation runs on your ComfyUI and nothing is sent to Azure ML. To use a deployed Azure ML workspace from this machine instead, set <code>Generation:Provider</code> to <code>azureml</code> (see docs/local-generation.md).</p>
        </div>
      ) : (
      <div className="environment-guidance">
        <h2>Before generating</h2>
        <p>
          The operator must confirm customer-managed NSG rules, private DNS and outbound egress, then explicitly approve GPU spend.
          This dashboard cannot change network policy. A StudioAdmin starts and stops hosted regions from Administration.
        </p>
        <p>
          On a local or legacy deployment, keep the portal running here and use the documented Start action with <code>-NoPortal</code> and the
          required approvals. Use Stop to release owned compute and egress afterward.
        </p>
        <p>Stopping GPU compute does not remove standing storage, ACR, private-endpoint or logging charges.</p>
      </div>
      )}
    </section>
  );
}
