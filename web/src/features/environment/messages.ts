import type { EnvironmentStatus } from '../../api/types';

export const isLocal = (data: EnvironmentStatus | null | undefined): boolean => data?.mode === 'local';

export function connectionNotice(data: EnvironmentStatus): string {
  if (isLocal(data)) {
    if (data.generationEnabled) return `Local mode: ComfyUI is connected. Videos are stored in ${data.storage}; no Azure ML or cloud compute is used.`;
    return data.computeState === 'Unreachable'
      ? 'Local mode: ComfyUI is unreachable, so generation is disabled. Check the comfyui-url parameter in the Aspire dashboard.'
      : 'Local mode: no ComfyUI workflow file was found, so generation is disabled. See docs/local-generation.md.';
  }
  if (data.generationEnabled) {
    return 'Workspace and private storage connected. The operator-armed cluster accepts jobs and scales from zero when work is queued.';
  }
  if (!data.prepared) {
    return 'Workspace and private storage connected. WAN asset preparation is not complete; generation is disabled.';
  }
  return data.computeState === 'Not configured'
    ? 'Workspace and private storage connected. The GPU cluster has not been configured yet; this is different from a configured cluster with zero running nodes.'
    : 'Workspace and private storage connected. The GPU cluster is configured, but submission is not armed by the operator.';
}

export function generationHelp(data: EnvironmentStatus | null): string {
  if (!data) return 'Checking generation readiness…';
  if (isLocal(data)) return data.generationEnabled ? 'Runs on your local ComfyUI. No cloud cost.' : 'Local generation is disabled until ComfyUI is reachable and a workflow file is present.';
  if (data.generationEnabled) return 'This starts billed GPU jobs. No automatic retries.';
  return data.prepared
    ? 'Generation remains disabled until the operator completes network/egress checks and runs Start -NoPortal with the required approvals.'
    : 'Complete Prepare and restart Portal to load the verified WAN release. No GPU compute is needed to prepare assets.';
}

export function environmentDetails(data: EnvironmentStatus): [string, string][] {
  if (isLocal(data)) {
    return [
      ['Mode', 'Local (no Azure ML)'],
      ['Video storage', data.storage],
      ['ComfyUI', data.compute],
      ['Workflow file', data.prepared ? 'Found' : 'Missing'],
    ];
  }
  return [
    ['Azure ML workspace', data.workspace],
    ['Resource group', data.resourceGroup],
    ['Private Blob Storage', data.storage],
    ['Compute target', data.compute],
    ['Cluster provisioning', data.computeState],
    ['Prepared release', data.version || 'Not prepared'],
    ['GPU scaling', 'Configured for zero minimum / one maximum node; jobs trigger allocation and run one at a time'],
    ['Submission gate', data.armed ? 'Operator-armed' : 'Not armed'],
    ['Server job timeout', `At most ${data.maxJobMinutes} minutes per job`],
    ['Connections checked', new Date(data.checkedAt).toLocaleString()],
  ];
}
