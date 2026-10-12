'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { CheckCircle2, Loader2, Send } from 'lucide-react';
import { COMPLETION_FIELDS, employeeCompletionApi, type EmployeeCompletion } from '../api/employeeCompletion';
import { useLocale } from '../contexts/LocaleContext';

export function EmployeeCompletionHandoff({ employeeId, onChanged }: { employeeId: number; onChanged?: () => void }) {
  return <CompletionHandoffContent key={employeeId} employeeId={employeeId} onChanged={onChanged} />;
}

function CompletionHandoffContent({ employeeId, onChanged }: { employeeId: number; onChanged?: () => void }) {
  const { t } = useLocale();
  const [state, setState] = useState<EmployeeCompletion | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notes, setNotes] = useState('');
  const mounted = useRef(true);
  const loadSequence = useRef(0);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; loadSequence.current++; }; }, []);
  const load = useCallback(async () => {
    const sequence = ++loadSequence.current;
    setError('');
    try {
      const result = await employeeCompletionApi.get(employeeId);
      if (mounted.current && sequence === loadSequence.current) setState(result);
    } catch { if (mounted.current && sequence === loadSequence.current) setError(t('Employee completion could not be loaded.')); }
  }, [employeeId, t]);
  useEffect(() => { setState(null); setNotes(''); void load(); }, [load]);

  const act = async (decision?: 'approve' | 'reject') => {
    setBusy(true); setError('');
    try {
      if (decision && state?.requestId) {
        await employeeCompletionApi.decide(state.requestId, decision, notes);
        if (mounted.current) { await load(); if (mounted.current) onChanged?.(); }
      } else setState(await employeeCompletionApi.request(employeeId));
    } catch (e) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The request could not be completed. Please try again.'));
    } finally { setBusy(false); }
  };

  return <section data-testid="employee-completion-handoff" className="rounded-xl border border-slate-200 bg-slate-50/60 p-4 dark:border-white/10 dark:bg-white/5">
    <h3 className="font-semibold text-slate-900 dark:text-white">{t('Employee completion')}</h3>
    <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('Let the employee provide contact details and upload identity or bank proof in Self-Service. HR reviews their submissions.')}</p>
    {error && <div role="alert" className="mt-3 text-sm text-red-600">{error} <button type="button" onClick={() => void load()} className="underline">{t('Retry')}</button></div>}
    {!state && !error && <Loader2 className="mt-3 h-4 w-4 motion-safe:animate-spin" aria-label={t('Loading…')} />}
    {state && <div className="mt-3 space-y-3">
      {!state.selfServiceAvailable && <p className="text-sm text-amber-800 dark:text-amber-200">{t('The request will be available after the employee has eligible Self-Service access. HR must complete activation requirements first.')}</p>}
      {!state.requested ? <button type="button" className="btn-secondary" disabled={busy} onClick={() => void act()}><Send className="h-4 w-4" aria-hidden="true" />{t(busy ? 'Saving...' : 'Request employee details')}</button> : <p role="status" className="flex items-center gap-2 text-sm font-medium"><CheckCircle2 className="h-4 w-4 text-emerald-600" aria-hidden="true" />{t(state.status === 'Approved' ? 'Employee details approved' : state.status === 'PendingHR' ? 'Employee details awaiting HR review' : state.status === 'Rejected' ? 'Returned to employee for correction' : 'Completion request saved')}</p>}
      {state.status === 'PendingHR' && state.changes && <>
        <dl className="grid gap-3 sm:grid-cols-2">{COMPLETION_FIELDS.filter(f => Object.hasOwn(state.changes!, f.key)).map(f => <div key={f.key}><dt className="text-xs text-slate-500">{t(f.label)}</dt><dd className="mt-1 break-words text-sm"><bdi>{state.changes?.[f.key] || t('Not provided')}</bdi></dd></div>)}</dl>
        <label className="block text-sm">{t('Review note')}<input value={notes} onChange={e => setNotes(e.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 dark:border-white/10 dark:bg-slate-900" maxLength={500} /></label>
        <div className="flex flex-wrap gap-2"><button type="button" className="btn-secondary" disabled={busy || !notes.trim()} onClick={() => void act('reject')}>{t('Return for correction')}</button><button type="button" className="btn-primary" disabled={busy} onClick={() => void act('approve')}>{t('Approve details')}</button></div>
      </>}
    </div>}
  </section>;
}
