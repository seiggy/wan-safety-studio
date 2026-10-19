import { describe, expect, it } from 'vitest';
import type { GalleryItem } from '../../api/types';
import { PAGE_SIZE, pageLibrary, type LibraryQuery } from './libraryPaging';

const entry = (job_name: string, extra: Partial<GalleryItem> = {}) => ({ item: { job_name, ...extra } as GalleryItem });
const base: LibraryQuery = { search: '', sort: 'newest', showArchived: false, page: 0 };
const all = [entry('wan-3', { display_name: 'Charlie' }), entry('wan-2', { display_name: 'alpha', prompt: 'Oil leak at night' }), entry('wan-1', { display_name: 'Bravo' })];

const names = (result: { items: { item: GalleryItem }[] }) => result.items.map((e) => e.item.job_name);

describe('pageLibrary', () => {
  it('keeps the server order for newest and reverses it for oldest', () => {
    expect(names(pageLibrary(all, new Set(), base))).toEqual(['wan-3', 'wan-2', 'wan-1']);
    expect(names(pageLibrary(all, new Set(), { ...base, sort: 'oldest' }))).toEqual(['wan-1', 'wan-2', 'wan-3']);
  });

  it('sorts by name ignoring case', () => {
    expect(names(pageLibrary(all, new Set(), { ...base, sort: 'name' }))).toEqual(['wan-2', 'wan-1', 'wan-3']);
  });

  it('searches the name and the prompt', () => {
    expect(names(pageLibrary(all, new Set(), { ...base, search: ' LEAK ' }))).toEqual(['wan-2']);
    expect(names(pageLibrary(all, new Set(), { ...base, search: 'brav' }))).toEqual(['wan-1']);
  });

  it('hides archived videos unless asked', () => {
    const archived = new Set(['wan-2']);
    expect(names(pageLibrary(all, archived, base))).toEqual(['wan-3', 'wan-1']);
    expect(names(pageLibrary(all, archived, { ...base, showArchived: true }))).toHaveLength(3);
  });

  it('pages in blocks and clamps a page that no longer exists', () => {
    const many = Array.from({ length: PAGE_SIZE + 5 }, (_, i) => entry(`wan-${i}`));
    const second = pageLibrary(many, new Set(), { ...base, page: 1 });
    expect(second.items).toHaveLength(5);
    expect(second.pages).toBe(2);
    expect(pageLibrary(many, new Set(), { ...base, page: 9 }).page).toBe(1);
    expect(pageLibrary([], new Set(), base)).toMatchObject({ total: 0, pages: 1, page: 0 });
  });
});
