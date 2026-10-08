import { modeLabel } from '../create/modes';
import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage, isUnauthorized } from '../../api/client';
import type { GalleryItem, GalleryResponse } from '../../api/types';
import { trustedUrl } from '../../lib/urls';

interface LibraryItem {
  item: GalleryItem;
  url: string;
}

interface Props {
  active: boolean;
  storage: string | undefined;
  onError: (message: string) => void;
  onNavigate: (view: 'create') => void;
}

const RATIOS = [
  { name: 'ratio-16-9', value: 16 / 9 },
  { name: 'ratio-4-3', value: 4 / 3 },
  { name: 'ratio-1-1', value: 1 },
  { name: 'ratio-3-4', value: 3 / 4 },
  { name: 'ratio-9-16', value: 9 / 16 },
];

/** CSP forbids inline styles, so the nearest standard aspect ratio is applied as a class. */
export function ratioClass(width: number | undefined, height: number | undefined): string {
  if (!width || !height) return 'ratio-16-9';
  const ratio = width / height;
  return RATIOS.reduce((best, next) => (Math.abs(next.value - ratio) < Math.abs(best.value - ratio) ? next : best)).name;
}

export function LibraryView({ active, storage, onError, onNavigate }: Props) {
  const [items, setItems] = useState<LibraryItem[]>([]);
  const [status, setStatus] = useState('');
  const [empty, setEmpty] = useState(false);
  const [loading, setLoading] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setStatus('Loading completed videos…');
    setEmpty(false);
    try {
      const data = await api.get<GalleryResponse>('/api/gallery?refresh=1');
      const trusted = data.items.map((item) => {
        const url = trustedUrl(item.video_url, 'blob', storage);
        if (!url) throw new Error("A video URL does not belong to this workspace's private storage.");
        return { item, url };
      });
      setItems(trusted);
      setEmpty(data.items.length === 0);
      setStatus(`${data.count} completed videos found across ${data.scanned_jobs} recent jobs.`);
    } catch (error) {
      if (isUnauthorized(error)) return;
      setItems([]);
      setStatus('The video library could not be loaded.');
      onError(errorMessage(error));
    } finally {
      setLoading(false);
    }
  }, [storage, onError]);

  // Load each time the view is opened, like the original view switch.
  useEffect(() => {
    if (active) void load();
    // Only the activation edge should trigger a reload, not storage/onError identity changes.
  }, [active]);

  return (
    <section id="library-view" className="view" hidden={!active}>
      <div className="page-heading">
        <h1>Video library</h1>
        <button id="refresh-library" type="button" className="secondary-button" disabled={loading} onClick={() => void load()}>
          Refresh library
        </button>
      </div>
      <p id="library-status" role="status" aria-live="polite">{status}</p>
      <div id="library-empty" className="library-empty" hidden={!empty}>
        <h2>No completed videos yet</h2>
        <p>Completed generations appear here.</p>
        <button type="button" className="secondary-button" onClick={() => onNavigate('create')}>Create a video</button>
      </div>
      <div id="library-grid" className="library-grid">
        {items.map(({ item, url }) => {
          const meta = [
            item.width && item.height ? `${item.width} × ${item.height}` : null,
            item.duration_seconds ? `${item.duration_seconds} s` : null,
            modeLabel(item.mode),
          ].filter(Boolean).join(' · ');
          return (
            <article key={item.job_name} className="library-item">
              <video className={ratioClass(item.width, item.height)} controls preload="metadata" src={url} />
              <div className="item-body">
                <h2>{item.display_name || item.job_name}</h2>
                <span className="item-meta">{meta}</span>
                <p>{item.prompt || 'No prompt was recorded.'}</p>
                <a href={url} target="_blank" rel="noopener noreferrer">Open video</a>
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}
