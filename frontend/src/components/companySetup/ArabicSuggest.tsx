'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Languages } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useLocale } from '../../contexts/LocaleContext';
import { localizationApi } from '../../api/localization';
import { sanitizeTranslation } from '../../hooks/useTransliterate';

/**
 * "Suggest Arabic" for the create-company wizard's Arabic name fields, backed by the offline organisation-label
 * suggester (POST /api/localization/transliterate/batch → TransliterationService.ToArabicOrganizationLabel). Nothing
 * leaves our servers, and nothing is written into a field until the person accepts it: a suggestion never silently
 * overwrites what they typed. No confident suggestion is said in words, never guessed letter by letter.
 *
 * One cache per wizard: a section asks once for the English names of rows whose Arabic is empty, so it can offer
 * "Fill missing Arabic names (N)" with a true N; a field's own button reads the same cache.
 */

type Entry = { status: 'loading' } | { status: 'done'; suggestion: string } | { status: 'failed' };

interface SuggestionsValue {
  get: (text: string) => Entry | undefined;
  /** Ask for these in the background (debounced, batched). Used for the bulk action's count. */
  prefetch: (texts: string[]) => void;
  /** Ask now, for a field's button. */
  fetchNow: (text: string) => Promise<Entry>;
}

const Ctx = createContext<SuggestionsValue | null>(null);
const keyOf = (text: string) => text.trim().replace(/\s+/g, ' ').toLowerCase();
const usable = (text: string | undefined | null) => (text ?? '').trim().length >= 2 && (text ?? '').trim().length <= 200;
const BATCH = 100;

export function ArabicSuggestionsProvider({ children }: { children: ReactNode }) {
  const [cache, setCache] = useState<ReadonlyMap<string, Entry>>(() => new Map());
  const cacheRef = useRef(cache);
  cacheRef.current = cache;
  const pending = useRef(new Map<string, string>());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const waiters = useRef(new Map<string, ((entry: Entry) => void)[]>());

  const store = useCallback((entries: [string, Entry][]) => {
    setCache(current => {
      const next = new Map(current);
      for (const [key, entry] of entries) next.set(key, entry);
      cacheRef.current = next;
      return next;
    });
    for (const [key, entry] of entries) {
      if (entry.status === 'loading') continue;
      for (const resolve of waiters.current.get(key) ?? []) resolve(entry);
      waiters.current.delete(key);
    }
  }, []);

  const request = useCallback(async (texts: string[]) => {
    if (texts.length === 0) return;
    store(texts.map(text => [keyOf(text), { status: 'loading' }]));
    for (let i = 0; i < texts.length; i += BATCH) {
      const chunk = texts.slice(i, i + BATCH);
      try {
        const { suggestions } = await localizationApi.transliterateOrganizationLabels(chunk);
        if (!Array.isArray(suggestions) || suggestions.length !== chunk.length) throw new Error('unexpected answer');
        store(chunk.map((text, j) => [keyOf(text), { status: 'done', suggestion: sanitizeTranslation(suggestions[j]?.suggestion ?? '') }]));
      } catch {
        store(chunk.map(text => [keyOf(text), { status: 'failed' }]));
      }
    }
  }, [store]);

  const prefetch = useCallback((texts: string[]) => {
    for (const text of texts) {
      if (!usable(text)) continue;
      const key = keyOf(text);
      // Asked already (a failure is retried from a field's own button, not on every render).
      if (cacheRef.current.has(key)) continue;
      pending.current.set(key, text.trim());
    }
    if (pending.current.size === 0) return;
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      const batch = [...pending.current.values()];
      pending.current.clear();
      void request(batch);
    }, 450);
  }, [request]);

  const fetchNow = useCallback((text: string) => {
    const key = keyOf(text);
    const known = cacheRef.current.get(key);
    if (known?.status === 'done') return Promise.resolve(known);
    const answer = new Promise<Entry>(resolve => waiters.current.set(key, [...(waiters.current.get(key) ?? []), resolve]));
    if (known?.status !== 'loading') {
      pending.current.delete(key);
      void request([text.trim()]);
    }
    return answer;
  }, [request]);

  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  const value = useMemo<SuggestionsValue>(() => ({ get: text => cache.get(keyOf(text)), prefetch, fetchNow }), [cache, prefetch, fetchNow]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

function useSuggestions(): SuggestionsValue {
  const value = useContext(Ctx);
  if (!value) throw new Error('ArabicSuggest outside ArabicSuggestionsProvider');
  return value;
}

const linkButton = 'inline-flex min-h-[1.75rem] items-center gap-1 rounded-md px-1.5 text-xs font-semibold text-sapphire hover:bg-sapphire/[0.06] hover:underline disabled:cursor-not-allowed disabled:text-slate-400 disabled:no-underline disabled:hover:bg-transparent dark:text-cyanAccent dark:hover:bg-cyanAccent/10 dark:disabled:text-slate-500 rtl:text-[13px]';

/**
 * Under an Arabic name field: a text button "Suggest Arabic". The answer is offered, never applied: "Use «…»" when
 * the field is empty, "Replace with «…»" when it holds something else (old letter-by-letter values included).
 */
export function ArabicSuggest({ source, value, onAccept, inputId, readOnly, context }: {
  /** The row's English name. */
  source: string | undefined;
  /** The Arabic field's current value. */
  value: string | undefined;
  onAccept: (arabic: string) => void;
  /** The Arabic input, focused again after accepting. */
  inputId: string;
  readOnly?: boolean;
  /** Screen-reader context, e.g. "Branch 2". */
  context?: string;
}) {
  const t = useT();
  const { fetchNow } = useSuggestions();
  const [asked, setAsked] = useState<{ source: string; entry: Entry } | null>(null);
  const english = (source ?? '').trim();
  // A changed English name makes an earlier answer stale.
  const shown = asked && asked.source === english ? asked.entry : null;
  if (readOnly) return null;

  const ask = async () => {
    setAsked({ source: english, entry: { status: 'loading' } });
    const entry = await fetchNow(english);
    setAsked(current => (current && current.source === english ? { source: english, entry } : current));
  };
  const accept = (arabic: string) => {
    onAccept(arabic);
    setAsked(null);
    requestAnimationFrame(() => document.getElementById(inputId)?.focus());
  };
  const current = (value ?? '').trim();
  const sr = context ? <span className="sr-only"> {context}</span> : null;

  let body: ReactNode;
  if (!shown) {
    body = (
      <button type="button" className={linkButton} disabled={!usable(english)} onClick={ask} data-testid="suggest-arabic"
        title={usable(english) ? t('Suggested from KynexOne’s reviewed wording, on our own servers. You choose whether to use it.') : t('Type the English name first.')}>
        <Languages className="h-3.5 w-3.5" aria-hidden="true" />{t('Suggest Arabic')}{sr}
      </button>
    );
  } else if (shown.status === 'loading') {
    body = <span className="inline-flex min-h-[1.75rem] items-center px-1.5 text-xs text-slate-600 dark:text-slate-400">{t('Suggesting…')}</span>;
  } else if (shown.status === 'failed') {
    body = (
      <span className="flex flex-wrap items-center gap-x-1 text-xs text-slate-700 dark:text-slate-300">
        {t('Could not get a suggestion. Try again, or type the Arabic name.')}
        <button type="button" className={linkButton} onClick={ask}>{t('Try again')}{sr}</button>
      </span>
    );
  } else if (!shown.suggestion) {
    body = (
      <span className="flex flex-wrap items-center gap-x-1 text-xs text-slate-700 dark:text-slate-300" data-testid="suggest-arabic-none">
        {t('No suggestion for this wording. Type the Arabic name.')}
        <button type="button" className={linkButton} onClick={() => setAsked(null)}>{t('Dismiss')}{sr}</button>
      </span>
    );
  } else if (shown.suggestion === current) {
    body = (
      <span className="flex flex-wrap items-center gap-x-1 text-xs text-slate-700 dark:text-slate-300" data-testid="suggest-arabic-same">
        {t('The Arabic name already matches the suggestion.')}
        <button type="button" className={linkButton} onClick={() => setAsked(null)}>{t('Dismiss')}{sr}</button>
      </span>
    );
  } else {
    body = (
      <span className="flex flex-wrap items-center gap-x-1">
        <button type="button" className={`${linkButton} max-w-full text-start`} onClick={() => accept(shown.suggestion)} data-testid="suggest-arabic-accept">
          <span className="min-w-0 break-words">{current ? t('Replace with «{arabic}»', { arabic: shown.suggestion }) : t('Use «{arabic}»', { arabic: shown.suggestion })}</span>{sr}
        </button>
        <button type="button" className={`${linkButton} text-slate-600 dark:text-slate-400`} onClick={() => setAsked(null)}>{t('Dismiss')}{sr}</button>
      </span>
    );
  }
  return <div className="mt-1 min-h-[1.75rem]" aria-live="polite">{body}</div>;
}

/**
 * One action per section: fills only the empty Arabic names that have a confident suggestion, in one change, and
 * says which rows were left blank. Rows with a value are never touched (their own button offers a replacement).
 */
export function FillMissingArabic<T>({ rows, eligible, english, arabicKey, onFill, label }: {
  rows: T[];
  /** Editable rows only: a shared catalog row keeps its own names. */
  eligible: (row: T, index: number) => boolean;
  english: (row: T) => string;
  arabicKey: keyof T & string;
  onFill: (rows: T[]) => void;
  /** Row label for the "left blank" list when the English name is empty. */
  label: (row: T, index: number) => string;
}) {
  const t = useT();
  const { locale } = useLocale();
  const { get, prefetch } = useSuggestions();
  const [result, setResult] = useState<{ filled: number; blank: string[] } | null>(null);
  const empty = rows.map((row, index) => ({ row, index })).filter(({ row, index }) =>
    eligible(row, index) && !String(row[arabicKey] ?? '').trim() && usable(english(row)));
  const wanted = empty.map(({ row }) => english(row));
  const wantedKey = wanted.join('\u0000');
  useEffect(() => { prefetch(wanted); }, [wantedKey]); // eslint-disable-line react-hooks/exhaustive-deps
  const withSuggestion = empty.flatMap(({ row, index }) => {
    const entry = get(english(row));
    return entry?.status === 'done' && entry.suggestion ? [{ index, suggestion: entry.suggestion }] : [];
  });

  const fill = () => {
    const by = new Map(withSuggestion.map(s => [s.index, s.suggestion]));
    const blank = empty.filter(({ index }) => !by.has(index)).map(({ row, index }) => english(row).trim() || label(row, index));
    onFill(rows.map((row, index) => (by.has(index) ? { ...row, [arabicKey]: by.get(index) } : row)));
    setResult({ filled: by.size, blank });
  };
  return (
    <>
      {withSuggestion.length > 0 && (
        <button type="button" className="btn-secondary" onClick={fill} data-testid="fill-missing-arabic">
          <Languages className="h-4 w-4" aria-hidden="true" />{t('Fill missing Arabic names ({count})', { count: withSuggestion.length })}
        </button>
      )}
      {result && (
        <div role="status" className="w-full rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-xs leading-5 text-slate-700 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300" data-testid="fill-missing-arabic-result">
          <p>{t('Arabic names filled: {count}. Check them before you save.', { count: result.filled })}</p>
          {result.blank.length > 0 && <p className="mt-0.5">{t('Left blank, with no confident suggestion: {names}', { names: new Intl.ListFormat(locale, { type: 'unit' }).format(result.blank) })}</p>}
          <button type="button" className={`${linkButton} -ms-1.5 mt-0.5`} onClick={() => setResult(null)}>{t('Dismiss')}</button>
        </div>
      )}
    </>
  );
}
