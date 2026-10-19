import { describe, expect, it } from 'vitest';
import { trustedUrl } from './urls';

describe('trustedUrl', () => {
  it('accepts only the studio blob host or same-origin local media', () => {
    expect(trustedUrl('https://acct.blob.core.windows.net/v.mp4', 'blob', 'acct')).toContain('acct.blob');
    expect(trustedUrl('https://evil.example/v.mp4', 'blob', 'acct')).toBeNull();
    expect(trustedUrl('/api/media/video-library/job1/video.mp4', 'blob', 'acct')).toBe('/api/media/video-library/job1/video.mp4');
    expect(trustedUrl('/api/media/video-library/../secret', 'blob', 'acct')).toBeNull();
    expect(trustedUrl('/api/media/vision/projects/x', 'blob', 'acct')).toBeNull();
  });
});
