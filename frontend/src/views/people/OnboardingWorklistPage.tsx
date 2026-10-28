'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useState, useSyncExternalStore } from 'react';
import { OWNER_ROLES, onboardingTemplatesApi, type WorklistItem } from '@/src/api/onboardingTemplates';
import { useLocale } from '@/src/contexts/LocaleContext';
import { onboardingProblemKey } from '@/src/lib/onboardingProblem';
import { formatIsoDate } from '@/src/lib/hire/dates';
import { ConfirmDialog, type ConfirmState } from '@/src/components/ui/ConfirmDialog';
import { LoadFailedNotice } from '@/src/components/ui/LoadFailedRow';
import { StatusChip } from '@/src/components/StatusChip';
import { SkipTaskDialog } from './SkipTaskDialog';

const input = 'rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const PAGE_SIZE = 100;
/** Below 768 px a table cannot show seven columns without clipping, so each task becomes a stacked card. */
const NARROW = '(max-width: 767px)';
function useNarrow(): boolean {
  return useSyncExternalStore(
    cb => { const m = window.matchMedia(NARROW); m.addEventListener('change', cb); return () => m.removeEventListener('change', cb); },
    () => window.matchMedia(NARROW).matches,
    () => false,
  );
}
const STATUSES = ['Pending', 'InProgress', 'Blocked'] as const;

/** Open onboarding tasks across employees for the roles the signed-in person works. */
export function OnboardingWorklistPage() {
  const { t, locale } = useLocale();
  const ar = locale === 'ar';
  const uid = useId();
  const narrow = useNarrow();
  const [ownerRole, setOwnerRole] = useState('');
  const [status, setStatus] = useState('');
  const [overdue, setOverdue] = useState(false);
  const [items, setItems] = useState<WorklistItem[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [skipping, setSkipping] = useState<WorklistItem | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const r = await onboardingTemplatesApi.worklist({ page: 1, pageSize: PAGE_SIZE, ownerRole: ownerRole || undefined, status: status || undefined, overdue: overdue || undefined });
      setItems(r.items); setTotal(r.total); setLoadError(null); setSelected(new Set());
    } catch (e) { setItems([]); setLoadError(e); } finally { setLoading(false); }
  }, [ownerRole, status, overdue]);
  useEffect(() => { void load(); }, [load]);

  const complete = async (ids: string[]) => {
    let done = 0; let refused = 0; let lastError: unknown = null;
    for (const id of ids) {
      try { await onboardingTemplatesApi.updateTask(id, { status: 'Completed' }); done += 1; } catch (e) { refused += 1; lastError = e; }
    }
    setNotice(refused === 0 ? { kind: 'ok', text: t('{count, plural, one {# task completed.} other {# tasks completed.}}', { count: Number(done) }) }
      : { kind: 'error', text: t('{done, plural, one {# task completed} other {# tasks completed}}, {refused, plural, one {# could not be} other {# could not be}}: {reason}', { done: Number(done), refused: Number(refused), reason: t(onboardingProblemKey(lastError)) }) });
    await load();
  };
  const askBulk = () => setConfirm({
    title: t('Complete the selected tasks?'), message: t('{count, plural, one {# task will be marked completed.} other {# tasks will be marked completed.}}', { count: Number(selected.size) }), confirmLabel: t('Complete'), closeOnConfirm: true,
    onConfirm: async () => { await complete([...selected]); },
  });
  const toggle = (id: string) => setSelected(prev => { const n = new Set(prev); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const allSelected = items.length > 0 && selected.size === items.length;

  return (
    <div className="mx-auto w-full max-w-6xl space-y-4 px-4 py-4" data-testid="onboarding-worklist">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-white">{t('Onboarding tasks')}</h1>
        <p className="mt-1 max-w-2xl text-sm text-slate-600 dark:text-slate-300">{t('Open onboarding tasks for the new hires you work with, across all employees.')}</p>
      </header>
      <div className="flex flex-wrap items-end gap-3">
        <div><label htmlFor={`${uid}-r`} className="block text-xs font-medium">{t('Who does it')}</label>
          <select id={`${uid}-r`} className={input} value={ownerRole} onChange={e => setOwnerRole(e.target.value)}><option value="">{t('All roles')}</option>{OWNER_ROLES.map(r => <option key={r} value={r}>{t(`onboarding.owner.${r}`)}</option>)}</select></div>
        <div><label htmlFor={`${uid}-s`} className="block text-xs font-medium">{t('Status')}</label>
          <select id={`${uid}-s`} className={input} value={status} onChange={e => setStatus(e.target.value)}><option value="">{t('All open')}</option>{STATUSES.map(s => <option key={s} value={s}>{t(`onboarding.status.${s}`)}</option>)}</select></div>
        <label className="inline-flex items-center gap-2 pb-2 text-sm"><input type="checkbox" checked={overdue} onChange={e => setOverdue(e.target.checked)} />{t('Overdue only')}</label>
        <button type="button" className="btn-primary ms-auto" disabled={selected.size === 0} onClick={askBulk}>{t('Complete selected')}</button>
      </div>
      {notice && <div role={notice.kind === 'error' ? 'alert' : 'status'} className={`rounded-lg border px-4 py-3 text-sm ${notice.kind === 'error' ? 'border-rose-200 bg-rose-50 text-rose-700' : 'border-emerald-200 bg-emerald-50 text-emerald-700'}`}>{notice.text}</div>}
      {!loading && loadError == null && total > items.length && <p role="status" className="text-sm text-amber-800">{t('Showing the first {shown} of {total, plural, one {# task} other {# tasks}}. Narrow the filters to see the rest.', { shown: Number(items.length), total: Number(total) })}</p>}
      {loading ? <p role="status" className="text-sm text-slate-500">{t('Loading…')}</p>
        : loadError != null ? <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>
        : items.length === 0 ? <p className="surface p-6 text-sm text-slate-600 dark:text-slate-300" data-testid="worklist-empty">{t('No open onboarding tasks for you right now.')}</p>
        : (
          narrow ? (
            <ul className="space-y-3" data-testid="worklist-cards" aria-label={t('Open onboarding tasks, {total} in total', { total })}>
              <li className="flex items-center gap-2 text-sm"><input id={`${uid}-all`} type="checkbox" checked={allSelected} onChange={() => setSelected(allSelected ? new Set() : new Set(items.map(i => i.taskId)))} /><label htmlFor={`${uid}-all`}>{t('Select all tasks')}</label></li>
              {items.map(i => (
                <li key={i.taskId} className="surface min-w-0 space-y-2 p-3" data-testid={`worklist-row-${i.taskId}`}>
                  <div className="flex items-start gap-2">
                    <input type="checkbox" className="mt-1" aria-label={t('Select {task}', { task: ar && i.titleAr ? i.titleAr : i.title })} checked={selected.has(i.taskId)} onChange={() => toggle(i.taskId)} />
                    <div className="min-w-0 flex-1 break-words">
                      <p className="font-medium">{ar && i.titleAr ? i.titleAr : i.title}</p>
                      {i.blocksActivation && <StatusChip label={t('Blocks activation')} tone="amber" />}
                    </div>
                  </div>
                  <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
                    <dt className="text-slate-500">{t('Employee')}</dt>
                    <dd className="min-w-0 break-words"><Link className="text-sapphire underline" href={`/people/${i.employee.id}`}>{i.employee.name}</Link> <span className="text-xs text-slate-500" dir="ltr">{i.employee.code}</span></dd>
                    <dt className="text-slate-500">{t('Who does it')}</dt><dd>{t(`onboarding.owner.${i.ownerRole}`)}</dd>
                    <dt className="text-slate-500">{t('Due')}</dt>
                    <dd>{i.dueDate ? formatIsoDate(i.dueDate, locale) : <span className="text-slate-500">{t('Due when the start date is set')}</span>}{i.overdue && <span className="ms-2"><StatusChip label={t('Overdue')} tone="rose" /></span>}</dd>
                    <dt className="text-slate-500">{t('Status')}</dt><dd>{t(`onboarding.status.${i.status}`)}</dd>
                  </dl>
                  <div className="flex gap-2">
                    <button type="button" className="btn-secondary h-9 flex-1 px-3 text-sm" onClick={() => { void complete([i.taskId]); }}>{t('Mark complete')}</button>
                    <button type="button" className="btn-secondary h-9 flex-1 px-3 text-sm" aria-label={t('Skip {task}', { task: ar && i.titleAr ? i.titleAr : i.title })} onClick={() => setSkipping(i)}>{t('Skip')}</button>
                  </div>
                </li>
              ))}
            </ul>
          ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[40rem] text-sm" data-testid="worklist-table">
              <caption className="sr-only">{t('Open onboarding tasks, {total} in total', { total: String(total) })}</caption>
              <thead><tr className="border-b border-slate-200 text-start text-xs text-slate-500 dark:border-white/10">
                <th scope="col" className="w-8 p-2"><input type="checkbox" aria-label={t('Select all tasks')} checked={allSelected} onChange={() => setSelected(allSelected ? new Set() : new Set(items.map(i => i.taskId)))} /></th>
                <th scope="col" className="p-2 text-start">{t('Task')}</th><th scope="col" className="p-2 text-start">{t('Employee')}</th><th scope="col" className="p-2 text-start">{t('Who does it')}</th>
                <th scope="col" className="p-2 text-start">{t('Due')}</th><th scope="col" className="p-2 text-start">{t('Status')}</th><th scope="col" className="p-2"><span className="sr-only">{t('Actions')}</span></th>
              </tr></thead>
              <tbody>
                {items.map(i => (
                  <tr key={i.taskId} className="border-b border-slate-100 align-top dark:border-white/5" data-testid={`worklist-row-${i.taskId}`}>
                    <td className="p-2"><input type="checkbox" aria-label={t('Select {task}', { task: ar && i.titleAr ? i.titleAr : i.title })} checked={selected.has(i.taskId)} onChange={() => toggle(i.taskId)} /></td>
                    <td className="p-2"><span className="font-medium">{ar && i.titleAr ? i.titleAr : i.title}</span>{i.blocksActivation && <span className="ms-2"><StatusChip label={t('Blocks activation')} tone="amber" /></span>}</td>
                    <td className="p-2"><Link className="text-sapphire underline" href={`/people/${i.employee.id}`}>{i.employee.name}</Link><span className="block text-xs text-slate-500" dir="ltr">{i.employee.code}</span></td>
                    <td className="p-2">{t(`onboarding.owner.${i.ownerRole}`)}</td>
                    <td className="p-2">{i.dueDate ? formatIsoDate(i.dueDate, locale) : <span className="text-slate-500">{t('Due when the start date is set')}</span>}{i.overdue && <span className="ms-2"><StatusChip label={t('Overdue')} tone="rose" /></span>}</td>
                    <td className="p-2">{t(`onboarding.status.${i.status}`)}</td>
                    <td className="p-2 text-end"><div className="inline-flex gap-2"><button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => { void complete([i.taskId]); }}>{t('Mark complete')}</button><button type="button" className="btn-secondary h-8 px-3 text-xs" aria-label={t('Skip {task}', { task: ar && i.titleAr ? i.titleAr : i.title })} onClick={() => setSkipping(i)}>{t('Skip')}</button></div></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          )
        )}
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
      <SkipTaskDialog task={skipping} onClose={() => setSkipping(null)} onSkipped={() => { setSkipping(null); setNotice({ kind: 'ok', text: t('Task skipped.') }); void load(); }} />
    </div>
  );
}
