import { modeLabel } from '../create/modes';
import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage, isUnauthorized } from '../../api/client';
import type { ArchivedResponse, GalleryItem, GalleryResponse } from '../../api/types';
import { PAGE_SIZE, pageLibrary, type LibrarySort } from './libraryPaging';
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
  const [archived, setArchived] = useState<ReadonlySet<string>>(new Set());
  const [totals, setTotals] = useState({ count: 0, scanned: 0 });
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState<LibrarySort>('newest');
  const [showArchived, setShowArchived] = useState(false);
  const [page, setPage] = useState(0);
  const [busy, setBusy] = useState<string | null>(null);
  const [status, setStatus] = useState('');
  const [empty, setEmpty] = useState(false);
  const [loading, setLoading] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setStatus('Loading completed videos…');
    setEmpty(false);
    try {
      const [data, saved] = await Promise.all([api.get<GalleryResponse>('/api/gallery?refresh=1'), api.get<ArchivedResponse>('/api/archived')]);
      const trusted = data.items.map((item) => {
        const url = trustedUrl(item.video_url, 'blob', storage);
        if (!url) throw new Error("A video URL does not belong to this workspace's private storage.");
        return { item, url };
      });
      setItems(trusted);
      setArchived(new Set(saved.archived));
      setTotals({ count: data.count, scanned: data.scanned_jobs });
      setPage(0);
      setEmpty(data.items.length === 0);
      setStatus('');
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

  const view = pageLibrary(items, archived, { search, sort, showArchived, page });
  const shown = status || (items.length > 0 ? `${view.total} of ${totals.count} completed videos shown across ${totals.scanned} recent jobs.` : '');

  const toggleArchive = async (jobName: string) => {
    setBusy(jobName);
    try {
      const saved = await api.post<ArchivedResponse>('/api/archived', { json: { jobName, archived: !archived.has(jobName) } });
      setArchived(new Set(saved.archived));
    } catch (error) {
      if (!isUnauthorized(error)) onError(errorMessage(error));
    } finally {
      setBusy(null);
    }
  };

  const change = (apply: () => void) => {
    apply();
    setPage(0);
  };

  return (
    <section id="library-view" className="view" hidden={!active}>
      <div className="page-heading">
        <h1>Video library</h1>
        <button id="refresh-library" type="button" className="secondary-button" disabled={loading} onClick={() => void load()}>
          Refresh library
        </button>
      </div>
      <div className="library-controls">
        <input id="library-search" type="search" placeholder="Search name or prompt" aria-label="Search videos" value={search}
          onChange={(event) => change(() => setSearch(event.target.value))} />
        <select id="library-sort" aria-label="Sort videos" value={sort} onChange={(event) => change(() => setSort(event.target.value as LibrarySort))}>
          <option value="newest">Newest first</option>
          <option value="oldest">Oldest first</option>
          <option value="name">Name A–Z</option>
        </select>
        <label><input id="library-show-archived" type="checkbox" checked={showArchived} onChange={(event) => change(() => setShowArchived(event.target.checked))} /> Show archived</label>
      </div>
      <p id="library-status" role="status" aria-live="polite">{shown}</p>
      <div id="library-empty" className="library-empty" hidden={!empty}>
        <h2>No completed videos yet</h2>
        <p>Completed generations appear here.</p>
        <button type="button" className="secondary-button" onClick={() => onNavigate('create')}>Create a video</button>
      </div>
      <div id="library-grid" className="library-grid">
        {view.items.map(({ item, url }) => {
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
                <a href={url} target="_blank" rel="noopener noreferrer">Open video</a>{' '}
                <button type="button" className="text-button" disabled={busy === item.job_name} onClick={() => void toggleArchive(item.job_name)}>
                  {archived.has(item.job_name) ? 'Unarchive' : 'Archive'}
                </button>
              </div>
            </article>
          );
        })}
      </div>
      <div id="library-pager" className="library-controls" hidden={view.total <= PAGE_SIZE}>
        <button type="button" className="secondary-button" disabled={view.page === 0} onClick={() => setPage(view.page - 1)}>Previous</button>
        <span id="library-page">Page {view.page + 1} of {view.pages}</span>
        <button type="button" className="secondary-button" disabled={view.page >= view.pages - 1} onClick={() => setPage(view.page + 1)}>Next</button>
      </div>
    </section>
  );
}
