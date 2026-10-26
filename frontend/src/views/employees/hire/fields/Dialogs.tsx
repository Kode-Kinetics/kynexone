'use client';

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { SERVER_CODE_KEYS, type ServerWarning } from '@/src/lib/hire/problem';
import type { AcknowledgedWarning } from '@/src/lib/hire/payload';

const MIN_REASON = 10;

function Shell({ role, title, onClose, children, labelId }: { role: 'dialog' | 'alertdialog'; title: string; onClose: () => void; children: ReactNode; labelId: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const opener = useRef<Element | null>(null);
  useEffect(() => {
    opener.current = document.activeElement;
    ref.current?.querySelector<HTMLElement>('textarea, button')?.focus();
    return () => { if (opener.current instanceof HTMLElement) opener.current.focus(); };
  }, []);
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4" onKeyDown={e => { if (e.key === 'Escape') onClose(); }}>
      <div ref={ref} role={role} aria-modal="true" aria-labelledby={labelId} className="max-h-[90vh] w-full max-w-lg overflow-auto rounded-2xl bg-white p-5 shadow-xl dark:bg-slate-900">
        <h2 id={labelId} className="text-base font-semibold text-slate-900 dark:text-white">{title}</h2>
        {children}
      </div>
    </div>
  );
}

/** Statutory floors are warnings: the person confirms each one with a reason of 10 or more characters (CD6). */
export function WarningAckDialog({ warnings, onConfirm, onCancel }: { warnings: ServerWarning[]; onConfirm: (acks: AcknowledgedWarning[]) => void; onCancel: () => void }) {
  const { t } = useLocale();
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [tried, setTried] = useState(false);
  const short = (c: string) => (reasons[c] ?? '').trim().length < MIN_REASON;
  const submit = () => {
    setTried(true);
    if (warnings.some(w => short(w.code))) return;
    onConfirm(warnings.map(w => ({ code: w.code, reason: reasons[w.code].trim() })));
  };
  return (
    <Shell role="dialog" title={t('Confirm before saving')} onClose={onCancel} labelId="hire-warn-title">
      <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('These are statutory checks, not errors. Give a reason for each to continue.')}</p>
      <ul className="mt-3 space-y-4">
        {warnings.map(w => {
          const id = `hire-warn-${w.code}`;
          const bad = tried && short(w.code);
          return (
            <li key={w.code} className="rounded-lg border border-amber-300 bg-amber-50 p-3 dark:border-amber-400/30 dark:bg-amber-500/10">
              <p className="text-sm font-medium text-amber-900 dark:text-amber-100">{t(SERVER_CODE_KEYS[w.code] ?? 'Some details need your confirmation before saving.')}</p>
              <label htmlFor={id} className="mt-2 block text-sm font-medium text-slate-700 dark:text-slate-200">{t('Reason')}</label>
              <textarea id={id} rows={2} value={reasons[w.code] ?? ''} aria-invalid={bad ? true : undefined} aria-describedby={bad ? `${id}-err` : undefined}
                onChange={e => setReasons(r => ({ ...r, [w.code]: e.target.value }))} className="input mt-1 h-auto w-full" />
              {bad && <p id={`${id}-err`} className="mt-1 text-xs font-medium text-red-700 dark:text-red-300">{t('Enter a reason of at least 10 characters.')}</p>}
            </li>
          );
        })}
      </ul>
      <div className="mt-4 flex flex-wrap justify-end gap-2">
        <button type="button" className="btn-secondary" onClick={onCancel}>{t('Cancel')}</button>
        <button type="button" className="btn-primary" onClick={submit}>{t('Confirm and save')}</button>
      </div>
    </Shell>
  );
}

/** Replaces the native browser prompt. Offers the three real outcomes (A3). */
export function DiscardDialog({ hasDraft, canSave, onKeep, onSaveLeave, onLeaveKeep, onDiscard }: { hasDraft: boolean; canSave: boolean; onKeep: () => void; onSaveLeave: () => void; onLeaveKeep: () => void; onDiscard: () => void }) {
  const { t } = useLocale();
  return (
    <Shell role="alertdialog" title={t('Leave without saving?')} onClose={onKeep} labelId="hire-discard-title">
      <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{hasDraft ? t('A draft of this employee is saved. You can keep it, or delete it.') : t('Nothing has been saved yet.')}</p>
      <div className="mt-4 flex flex-wrap justify-end gap-2">
        <button type="button" className="btn-secondary" onClick={onKeep}>{t('Keep editing')}</button>
        {hasDraft && <button type="button" className="btn-secondary" onClick={onLeaveKeep}>{t('Leave and keep the draft')}</button>}
        {canSave && !hasDraft && <button type="button" className="btn-secondary" onClick={onSaveLeave}>{t('Save draft and leave')}</button>}
        <button type="button" className="btn-danger" onClick={onDiscard}>{hasDraft ? t('Discard this draft') : t('Discard changes')}</button>
      </div>
    </Shell>
  );
}
