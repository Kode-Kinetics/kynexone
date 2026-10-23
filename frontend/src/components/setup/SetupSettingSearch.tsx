'use client';

import { useMemo, useRef, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent, type RefObject } from 'react';
import { Search, X } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { matchSetupEntries, type SetupSearchEntry, type SetupSearchHit } from './setupSearch';

export const SETUP_SEARCH_INPUT_ID = 'setup-setting-search';
const RESULTS_ID = 'setup-setting-search-results';

const isPlainPrimaryClick = (event: ReactMouseEvent) => event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;

/**
 * "Find a setting", at the top of Setup's section column (and of the folded list or bottom sheet below 1280px). While
 * it holds a query its results replace the groups, each with its group as a caption. ArrowDown/ArrowUp move through
 * the results, Enter opens one, Escape clears. Results are links: a plain click goes through the page (and its
 * unsaved-changes guard); a modified click opens a new tab.
 */
export function SetupSettingSearch({ entries, query, onQueryChange, onOpen, allAreas, inputRef }: {
  /** Already filtered to what this user may open (buildSetupSearchIndex). */
  entries: readonly SetupSearchEntry[];
  query: string;
  onQueryChange: (query: string) => void;
  onOpen: (entry: SetupSearchEntry) => void;
  /** Offered when nothing matches, to setup writers only (the page is theirs). */
  allAreas?: { href: string; onOpen: () => void };
  inputRef: RefObject<HTMLInputElement | null>;
}) {
  const t = useT();
  const results = useRef<HTMLUListElement>(null);
  const hits = useMemo<SetupSearchHit[]>(() => matchSetupEntries(entries, query, t), [entries, query, t]);
  const searching = query.trim().length > 0;

  const links = () => Array.from(results.current?.querySelectorAll<HTMLAnchorElement>('a[data-setup-search-result]') ?? []);
  const clear = (event: ReactKeyboardEvent) => {
    // Escape with a query only clears it; with none it is the list's (it closes the folded list).
    if (!searching) return;
    event.preventDefault();
    event.stopPropagation();
    onQueryChange('');
    inputRef.current?.focus();
  };
  const onInputKey = (event: ReactKeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Escape') { clear(event); return; }
    if (event.key === 'ArrowDown' && hits.length > 0) { event.preventDefault(); links()[0]?.focus(); return; }
    if (event.key === 'Enter' && hits.length > 0) { event.preventDefault(); links()[0]?.click(); }
  };
  const onResultKey = (event: ReactKeyboardEvent<HTMLAnchorElement>) => {
    if (event.key === 'Escape') { clear(event); return; }
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp' && event.key !== 'Home' && event.key !== 'End') return;
    event.preventDefault();
    const all = links();
    const index = all.indexOf(event.currentTarget);
    if (event.key === 'Home') all[0]?.focus();
    else if (event.key === 'End') all[all.length - 1]?.focus();
    else if (event.key === 'ArrowDown') all[Math.min(index + 1, all.length - 1)]?.focus();
    else if (index <= 0) inputRef.current?.focus();
    else all[index - 1]?.focus();
  };

  return (
    <div role="search" className="setup-section-search min-w-0 px-1 pb-2 pt-1 xl:px-0 xl:pt-0">
      <label htmlFor={SETUP_SEARCH_INPUT_ID} className="sr-only">{t('Find a setting')}</label>
      <div className="relative">
        <Search aria-hidden="true" className="pointer-events-none absolute start-2.5 top-1/2 z-10 h-4 w-4 -translate-y-1/2 text-slate-400 dark:text-slate-500" />
        <input
          ref={inputRef}
          id={SETUP_SEARCH_INPUT_ID}
          type="text"
          inputMode="search"
          autoComplete="off"
          spellCheck={false}
          value={query}
          onChange={event => onQueryChange(event.target.value)}
          onKeyDown={onInputKey}
          placeholder={t('Search settings')}
          aria-controls={searching ? RESULTS_ID : undefined}
          aria-describedby={searching ? `${RESULTS_ID}-count` : undefined}
          className="input h-9 w-full min-w-0 pe-8 ps-8"
        />
        {searching && (
          <button type="button" onClick={() => { onQueryChange(''); inputRef.current?.focus(); }}
            className="absolute end-1 top-1/2 grid h-7 w-7 -translate-y-1/2 place-items-center rounded-md text-slate-500 hover:bg-slate-100 hover:text-slate-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:text-slate-400 dark:hover:bg-white/10 dark:hover:text-white">
            <X aria-hidden="true" className="h-4 w-4" />
            <span className="sr-only">{t('Clear search')}</span>
          </button>
        )}
      </div>
      <p id={`${RESULTS_ID}-count`} className="sr-only" role="status" aria-atomic="true">
        {searching ? (hits.length > 0 ? t('{count, plural, one {# setting found} other {# settings found}}', { count: hits.length }) : t("No setting matches '{q}'", { q: query.trim() })) : ''}
      </p>
      {searching && (hits.length > 0 ? (
        <ul ref={results} id={RESULTS_ID} aria-label={t('Matching settings')} className="mt-2 space-y-0.5" data-testid="setup-search-results">
          {hits.map(hit => (
            <li key={hit.entry.id}>
              <a
                href={hit.entry.href}
                data-setup-search-result={hit.entry.id}
                onKeyDown={onResultKey}
                onClick={event => {
                  if (event.defaultPrevented || !isPlainPrimaryClick(event)) return;
                  event.preventDefault();
                  onOpen(hit.entry);
                }}
                className="block min-h-11 rounded-lg px-3 py-1.5 text-start text-sm leading-5 text-slate-800 hover:bg-slate-100 focus-visible:bg-sapphire/10 focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-sapphire dark:text-slate-200 dark:hover:bg-white/[0.06] dark:focus-visible:bg-cyanAccent/10 xl:min-h-0"
              >
                <span className="block font-medium"><bdi>{hit.label}</bdi></span>
                <span className="block text-[11px] leading-4 text-slate-500 rtl:text-[13px] rtl:leading-5 dark:text-slate-400">{hit.group}</span>
              </a>
            </li>
          ))}
        </ul>
      ) : (
        <div className="mt-2 rounded-lg px-3 py-2 text-sm text-slate-600 dark:text-slate-300" data-testid="setup-search-empty">
          <p>{t("No setting matches '{q}'", { q: query.trim() })}</p>
          {allAreas && (
            <a href={allAreas.href} onClick={event => {
              if (event.defaultPrevented || !isPlainPrimaryClick(event)) return;
              event.preventDefault();
              allAreas.onOpen();
            }} className="mt-1 inline-block font-semibold text-sapphire underline-offset-2 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:text-cyanAccent">
              {t('Browse All setup areas')}
            </a>
          )}
        </div>
      ))}
    </div>
  );
}
