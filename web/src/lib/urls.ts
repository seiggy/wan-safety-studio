export type UrlKind = 'studio' | 'blob';

/** Returns the URL only when it is https and on the expected host; otherwise null. */
export function trustedUrl(value: string | null | undefined, kind: UrlKind, storage: string | undefined): string | null {
  if (!value) return null;
  // Local dev mode serves generated videos from the same origin.
  if (kind === 'blob' && /^\/api\/media\/video-library\/[\w./-]+$/.test(value) && !value.includes('..')) return value;
  try {
    const url = new URL(value);
    const host = kind === 'studio' ? 'ml.azure.com' : `${storage}.blob.core.windows.net`;
    return url.protocol === 'https:' && url.hostname === host ? url.href : null;
  } catch {
    return null;
  }
}
