'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { useLocale } from '../contexts/LocaleContext';
import { useTransliterate } from '../hooks/useTransliterate';

/** Personal names use conventional spellings, never the generic letter-by-letter converter. */
export function ArabicNameField({ source, value, onChange }: {
  source: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const { t } = useLocale();
  const id = useId();
  const { suggest, isTranslating } = useTransliterate();
  const [preview, setPreview] = useState('');
  const [feedback, setFeedback] = useState<'manual' | 'error' | null>(null);
  const requestVersion = useRef(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const current = useRef({ source, value });
  current.current = { source, value };

  // Every edit invalidates the request, including A → B → A and closing/reopening a draft.
  useEffect(() => {
    requestVersion.current++;
    setPreview('');
    setFeedback(null);
    return () => { requestVersion.current++; };
  }, [source, value]);

  const requestSuggestion = async () => {
    const version = ++requestVersion.current;
    const requested = { source, value };
    setPreview('');
    setFeedback(null);
    const result = await suggest(source, 'ar', 'person-name');
    if (version !== requestVersion.current || current.current.source !== requested.source || current.current.value !== requested.value) return;
    if (result.failed) { setFeedback('error'); return; }
    // Older API builds have no coverage signal: never accept their phonetic output as a name.
    if (result.requiresManualEntry !== false || !result.suggestion) { setFeedback('manual'); return; }
    setPreview(result.suggestion);
  };

  return (
    <div className="min-w-0">
      <label htmlFor={id} className="block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Arabic full name')}</label>
      <div className="mt-1.5 flex items-stretch gap-1.5">
        <input ref={inputRef} id={id} value={value} onChange={event => onChange(event.target.value)} dir="rtl" aria-describedby={`${id}-guidance ${id}-feedback`} className="input min-w-0 w-full flex-1" />
        <button ref={buttonRef} type="button" onClick={requestSuggestion} disabled={source.trim().length < 2 || isTranslating} aria-controls={`${id}-feedback`} className="shrink-0 whitespace-nowrap rounded-lg border border-slate-200 px-2.5 py-2 text-xs font-medium text-slate-600 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
          {isTranslating ? t('Suggesting…') : t('Suggest (AR)')}
        </button>
      </div>
      <p id={`${id}-guidance`} className="mt-1.5 text-xs leading-relaxed text-slate-500 dark:text-slate-400">{t('Check the Arabic spelling against the employee’s passport or ID.')}</p>
      <div id={`${id}-feedback`} role="status" aria-live="polite">
        {feedback && <p className={`mt-2 text-xs leading-relaxed ${feedback === 'error' ? 'text-red-700 dark:text-red-300' : 'text-slate-600 dark:text-slate-300'}`}>
          {feedback === 'error' ? t('Could not get a suggestion. Try again or enter the Arabic name manually.') : t('No reliable suggestion for this name. Enter the Arabic spelling from the employee’s passport or ID.')}
        </p>}
        {preview && <div className="mt-2 rounded-lg border border-blue-100 bg-blue-50/70 p-3 dark:border-blue-400/20 dark:bg-blue-500/10">
          <p className="text-xs font-medium text-slate-600 dark:text-slate-300">{t('Suggested spelling')}</p>
          <p dir="rtl" className="mt-1 break-words text-lg font-semibold text-slate-900 dark:text-white">{preview}</p>
          <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-2">
            <button type="button" onClick={() => { onChange(preview); setPreview(''); inputRef.current?.focus(); }} className="text-xs font-semibold text-blue-700 hover:underline dark:text-blue-300">{t('Use this spelling')}</button>
            <button type="button" onClick={() => { setPreview(''); buttonRef.current?.focus(); }} className="text-xs text-slate-500 hover:underline dark:text-slate-400">{t('Dismiss suggestion')}</button>
          </div>
        </div>}
      </div>
    </div>
  );
}
