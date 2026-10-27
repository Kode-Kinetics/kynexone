'use client';

import { Loader2 } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { Modal } from '@/src/components/Modal';
import { onboardingTemplatesApi, type WorklistItem } from '@/src/api/onboardingTemplates';
import { useLocale } from '@/src/contexts/LocaleContext';
import { onboardingProblemKey, skipReasonOk } from '@/src/lib/onboardingProblem';

/** Skipping a task asks why: required (five characters or more) when the task holds back activation, optional otherwise. */
export function SkipTaskDialog({ task, onClose, onSkipped }: { task: WorklistItem | null; onClose: () => void; onSkipped: () => void }) {
  const { t, locale } = useLocale();
  const id = useId();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const field = useRef<HTMLTextAreaElement>(null);
  useEffect(() => { setReason(''); setError(''); setBusy(false); if (task) requestAnimationFrame(() => field.current?.focus()); }, [task]);
  if (!task) return null;
  const blocks = task.blocksActivation;
  const name = locale === 'ar' && task.titleAr ? task.titleAr : task.title;
  const valid = skipReasonOk(blocks, reason);
  const submit = async () => {
    if (!valid || busy) return;
    setBusy(true); setError('');
    try {
      await onboardingTemplatesApi.updateTask(task.taskId, { status: 'Skipped', note: reason.trim() || undefined });
      onSkipped();
    } catch (e) { setError(t(onboardingProblemKey(e))); setBusy(false); }
  };
  return (
    <Modal isOpen title={t('Skip this task?')} size="sm" onClose={() => { if (!busy) onClose(); }} closeDisabled={busy} closeLabel={t('Close')}
      footer={<>
        <button type="button" className="btn-secondary" onClick={onClose} disabled={busy}>{t('Cancel')}</button>
        <button type="button" className="btn-primary" onClick={() => void submit()} disabled={!valid || busy}>
          {busy && <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />}{busy ? t('Skipping…') : t('Skip task')}
        </button>
      </>}>
      <form className="space-y-3" onSubmit={e => { e.preventDefault(); void submit(); }}>
        <p className="break-words text-sm font-medium text-slate-800 dark:text-slate-100"><bdi>{name}</bdi></p>
        {blocks && <p className="text-sm text-amber-800 dark:text-amber-300">{t('This task blocks activation. Say why it can be skipped; the reason is kept on the record.')}</p>}
        <label htmlFor={`${id}-reason`} className="block space-y-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
          <span>{blocks ? t('Reason (required, at least 5 characters)') : t('Reason (optional)')}</span>
          <textarea id={`${id}-reason`} ref={field} rows={3} maxLength={500} required={blocks} aria-invalid={error ? true : undefined} aria-describedby={error ? `${id}-err` : undefined}
            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-normal text-slate-900 dark:border-white/10 dark:bg-white/5 dark:text-white" value={reason} onChange={e => setReason(e.target.value)} />
        </label>
        {error && <p id={`${id}-err`} role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}
      </form>
    </Modal>
  );
}
