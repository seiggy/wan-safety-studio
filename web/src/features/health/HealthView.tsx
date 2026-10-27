import { useConfigHealth } from './useConfigHealth';

const HEADLINE = {
  ready: 'The configuration is complete.',
  'ready-with-warnings': 'The configuration is usable, with warnings.',
  misconfigured: 'The configuration is incomplete. The studio is refusing work until it is fixed.',
} as const;

export function HealthView() {
  const { report, error } = useConfigHealth();
  return (
    <main id="main" className="health">
      <h1>Studio configuration</h1>
      {error && <p className="notice" role="alert">{error}</p>}
      {report && (
        <>
          <p className="notice" role="status">{HEADLINE[report.status]} Mode: {report.mode}.</p>
          {report.issues.length > 0 && (
            <>
              <h2>How to fix it in Azure App Service</h2>
              <ol>
                <li>In the Azure portal open your App Service, then <strong>Settings &gt; Environment variables &gt; App settings</strong>.</li>
                <li>Choose <strong>Add</strong> and enter each name below exactly as shown, with the value you find where described.</li>
                <li>Select <strong>Apply</strong>, then <strong>Confirm</strong>. The site restarts; reload this page to check again.</li>
              </ol>
              <p>Examples are made-up placeholders. This page never shows the values you have set.</p>
              <ul className="health-issues">
                {report.issues.map((issue) => (
                  <li key={`${issue.setting}:${issue.problem}`} className={`health-issue ${issue.severity}`}>
                    <h3><code>{issue.setting}</code> <small>{issue.severity === 'error' ? 'Required' : 'Warning'}</small></h3>
                    <p>{issue.problem}</p>
                    {issue.example && <p>Example: <code>{issue.example}</code></p>}
                    <p>Where to find it: {issue.where}</p>
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
    </main>
  );
}
