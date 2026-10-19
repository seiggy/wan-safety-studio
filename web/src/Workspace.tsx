import { useCallback, useState } from 'react';
import { useFeatures } from './api/features';
import type { EnvironmentStatus } from './api/types';
import { Sidebar, type ViewName } from './components/Sidebar';
import { CreateView } from './features/create/CreateView';
import { EnvironmentView } from './features/environment/EnvironmentView';
import { connectionNotice } from './features/environment/messages';
import { useEnvironment } from './features/environment/useEnvironment';
import { LibraryView } from './features/library/LibraryView';
import { VisionView } from './features/vision/VisionView';

const initialView = (): ViewName => (location.pathname === '/videos' ? 'library' : 'create');

/** Only problems are announced; a healthy connection needs no banner. */
function noticeFor(environment: EnvironmentStatus | null, failed: boolean): { text: string; className: string } | null {
  if (environment) return environment.generationEnabled ? null : { text: connectionNotice(environment), className: 'notice warning' };
  if (failed) return { text: 'Live connection checks failed. Generation is disabled until checks succeed.', className: 'notice error' };
  return null;
}

/** Signed-in shell. All views stay mounted so uploads, polling and editor state survive navigation. */
export function Workspace() {
  const [view, setView] = useState<ViewName>(initialView);
  const [operationError, setOperationError] = useState<string | null>(null);
  const { environment, error: connectionError, loading, refresh } = useEnvironment();
  const features = useFeatures();
  const activeView = view === 'vision' && !features.datasetBuilder ? 'create' : view;
  const clearError = useCallback(() => setOperationError(null), []);
  const notice = noticeFor(environment, connectionError !== null);
  const error = operationError ?? connectionError;

  return (
    <div id="workspace" className="workspace">
      <Sidebar view={activeView} onSelect={setView} datasetBuilder={features.datasetBuilder} />
      <div className="workspace-content">
        {notice && <div id="connection-notice" className={notice.className} role="status">{notice.text}</div>}
        <p id="global-error" className="notice error" role="alert" hidden={!error}>{error}</p>
        <CreateView active={activeView === 'create'} environment={environment} onError={setOperationError} onClearError={clearError} />
        <LibraryView active={activeView === 'library'} storage={environment?.storage} onError={setOperationError} onNavigate={setView} />
        {features.datasetBuilder && <VisionView active={activeView === 'vision'} />}
        <EnvironmentView active={activeView === 'environment'} environment={environment} loading={loading} onRefresh={() => void refresh()} />
      </div>
    </div>
  );
}
