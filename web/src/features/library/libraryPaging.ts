import type { GalleryItem } from '../../api/types';

export const PAGE_SIZE = 25;

export type LibrarySort = 'newest' | 'oldest' | 'name';

export interface LibraryQuery {
  search: string;
  sort: LibrarySort;
  showArchived: boolean;
  page: number;
}

export interface LibraryPage<T extends { item: GalleryItem }> {
  items: T[];
  total: number;
  page: number;
  pages: number;
}

const label = (item: GalleryItem): string => item.display_name || item.job_name;

/** The server returns newest first, so "oldest" reverses it. Page is clamped to the last page that exists. */
export function pageLibrary<T extends { item: GalleryItem }>(
  all: readonly T[],
  archived: ReadonlySet<string>,
  query: LibraryQuery,
): LibraryPage<T> {
  const needle = query.search.trim().toLowerCase();
  let matches = all.filter(({ item }) =>
    (query.showArchived || !archived.has(item.job_name)) &&
    (needle === '' || `${label(item)} ${item.prompt ?? ''}`.toLowerCase().includes(needle)));
  if (query.sort === 'oldest') matches = [...matches].reverse();
  else if (query.sort === 'name') matches = [...matches].sort((a, b) => label(a.item).localeCompare(label(b.item)));
  const pages = Math.max(1, Math.ceil(matches.length / PAGE_SIZE));
  const page = Math.min(Math.max(0, query.page), pages - 1);
  return { items: matches.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE), total: matches.length, page, pages };
}
