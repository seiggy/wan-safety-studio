import { useCallback, useState } from 'react';
import type { EnvironmentStatus } from './api/types';
import { Sidebar, type ViewName } from './components/Sidebar';
import { CreateView } from './features/create/CreateView';
import { EnvironmentView } from './features/environment/EnvironmentView';
import { connectionNotice } from './features/environment/messages';
import { useEnvironment } from './features/environment/useEnvironment';
import { LibraryView } from './features/library/LibraryView';
import { VisionView } from './features/vision/VisionView';

const initialView = (): ViewName => (location.pathname === '/videos' ? 'library' : 'create');

function noticeFor(environment: EnvironmentStatus | null, failed: boolean): { text: string; className: string } {
  if (environment) return { text: connectionNotice(environment), className: environment.generationEnabled ? 'notice' : 'notice warning' };
  if (failed) return { text: 'Live connection checks failed. Generation is disabled until checks succeed.', className: 'notice error' };
  return { text: 'Checking the deployed workspace and storage…', className: 'notice' };
}

/** Signed-in shell. All views stay mounted so uploads, polling and editor state survive navigation. */
export function Workspace() {
  const [view, setView] = useState<ViewName>(initialView);
  const [operationError, setOperationError] = useState<string | null>(null);
  const { environment, error: connectionError, loading, refresh } = useEnvironment();
  const clearError = useCallback(() => setOperationError(null), []);
  const notice = noticeFor(environment, connectionError !== null);
  const error = operationError ?? connectionError;

  return (
    <div id="workspace" className="workspace">
      <Sidebar view={view} onSelect={setView} local={environment?.mode === 'local'} />
      <div className="workspace-content">
        <div id="connection-notice" className={notice.className} role="status">{notice.text}</div>
        <p id="global-error" className="notice error" role="alert" hidden={!error}>{error}</p>
        <CreateView
          active={view === 'create'}
          environment={environment}
          environmentFailed={connectionError !== null}
          onError={setOperationError}
          onClearError={clearError}
          onNavigate={setView}
        />
        <LibraryView active={view === 'library'} storage={environment?.storage} onError={setOperationError} onNavigate={setView} />
        <VisionView active={view === 'vision'} />
        <EnvironmentView active={view === 'environment'} environment={environment} loading={loading} onRefresh={() => void refresh()} />
        <footer>Video Safety Studio · Local development · Review generated content before use</footer>
      </div>
    </div>
  );
}
