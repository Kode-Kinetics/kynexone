'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { Check, Download, Loader2, RotateCcw } from 'lucide-react';
import { hrCompletionApi, problemOf, SECTION_ORDER, type InboxItem, type SectionKey } from '../../api/essCompletion';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { canDownloadScanned, scanLabelKey, scanRefusalKey, scanState } from '../../lib/scanState';
import { hijriLine } from '../../lib/hire/dates';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { EssEmpty, EssLoadError, EssNotice, essInput, essPrimaryButton } from '../../components/ess/EssParts';

type Decision = { item: InboxItem; kind: 'accept' | 'reject' } | null;

/** HR's inbox of per-section submissions: what changes (before and after, in words), then accept or send back with a note. */
export function CompletionReviewPage() {
  const { t, locale } = useLocale();
  const { user, hasPermission } = useAuth();
  const { date } = useFormat();
  const [items, setItems] = useState<InboxItem[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [section, setSection] = useState<SectionKey | ''>('');
  const [companyId, setCompanyId] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const [decision, setDecision] = useState<Decision>(null);
  const [note, setNote] = useState('');
  const [override, setOverride] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const noteRef = useRef<HTMLTextAreaElement>(null);
  const ids = { section: useId(), company: useId(), note: useId(), override: useId() };
  const canOverride = hasPermission('employees.override_validation');
  const sensitive = hasPermission('employees.sensitive');

  const load = useCallback(async () => {
    setError(null);
    try { setItems(await hrCompletionApi.inbox({ status: 'PendingHR', section, companyId })); } catch (e) { setError(e); }
  }, [section, companyId]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (decision) requestAnimationFrame(() => noteRef.current?.focus()); }, [decision]);

  const run = async () => {
    if (!decision) return;
    setBusy(true); setMessage(null);
    try {
      if (decision.kind === 'accept') {
        const result = await hrCompletionApi.accept(decision.item.requestId, note.trim(), override.trim() || undefined);
        setMessage({ tone: 'ok', text: result.outcome === 'AwaitingFinance' ? t('Accepted. A Finance approver must now approve the new bank details. It cannot be you or the employee. They apply from the day after Finance approves.') : t('Accepted and applied to the employee record.') });
      } else {
        await hrCompletionApi.requestChanges(decision.item.requestId, note.trim());
        setMessage({ tone: 'ok', text: t('Sent back to the employee.') });
      }
      setDecision(null); setNote(''); setOverride('');
      await load();
    } catch (e) {
      const p = problemOf(e);
      setMessage({ tone: 'error', text: t(p.titleKey ?? `ess.completion.error.${p.code ?? 'validation_failed'}`) });
    } finally { setBusy(false); }
  };

  const companies = user?.companies ?? [];
  return (
    <div className="space-y-4" data-testid="completion-review">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t('Profile completion review')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Review the details employees sent after their first day. Accept a section, or send it back with a note.')}</p>
      </div>
      <div className="flex flex-wrap items-end gap-3" role="group" aria-label={t('Profile review')}>
        <label htmlFor={ids.section} className="space-y-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
          <span>{t('Section')}</span>
          <select id={ids.section} className={essInput} value={section} onChange={e => setSection(e.target.value as SectionKey | '')}>
            <option value="">{t('All sections')}</option>
            {SECTION_ORDER.map(s => <option key={s} value={s}>{t(`ess.completion.section.${s}.title`)}</option>)}
          </select>
        </label>
        {companies.length > 1 && (
          <label htmlFor={ids.company} className="space-y-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            <span>{t('Company')}</span>
            <select id={ids.company} className={essInput} value={companyId} onChange={e => setCompanyId(e.target.value)}>
              <option value="">{t('All companies')}</option>
              {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
        )}
        <p className="text-xs text-slate-500">{t('Waiting for HR review')}</p>
      </div>
      {!sensitive && <p className="text-xs text-slate-500">{t('Values are masked. You need the sensitive-data permission to see them in full.')}</p>}
      <div aria-live="polite">{message && <EssNotice tone={message.tone}>{message.text}</EssNotice>}</div>

      {error ? <EssLoadError error={error} onRetry={() => void load()} /> : !items ? <p aria-busy="true">{t('Loading…')}</p> : items.length === 0 ? <EssEmpty text={t('Nothing is waiting for review.')} /> : (
        <ul className="space-y-3">
          {items.map(item => (
            <li key={item.requestId} className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]" data-testid="inbox-item">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className="text-sm font-bold text-slate-800 dark:text-slate-100"><bdi>{item.employee.name}</bdi> <span className="font-normal text-slate-500" dir="ltr">{item.employee.code}</span></p>
                  <p className="text-xs text-slate-600 dark:text-slate-300">{t(`ess.completion.section.${item.section}.title`)} · {t('Submitted')} {date(item.submittedAtUtc)}</p>
                </div>
                {item.goesToFinance && <span className="rounded-full bg-violet-50 px-2 py-1 text-xs font-semibold text-violet-800 dark:bg-violet-500/10 dark:text-violet-300">{t('Goes to Finance')}</span>}
              </div>
              <button type="button" className="mt-2 text-xs font-semibold text-sapphire underline dark:text-cyanAccent" aria-expanded={open === item.requestId} onClick={() => setOpen(open === item.requestId ? null : item.requestId)}>
                {open === item.requestId ? t('Back to the list') : t('Review')}
              </button>
              {open === item.requestId && (
                <div className="mt-3 space-y-3">
                  <div className="overflow-x-auto" role="region" tabIndex={0} aria-label={t(`ess.completion.section.${item.section}.title`)}>
                    <table className="block w-full text-sm sm:table sm:min-w-[20rem]" role="table">
                      <caption className="sr-only">{t(`ess.completion.section.${item.section}.title`)}</caption>
                      <thead className="sr-only sm:table-header-group sm:not-sr-only" role="rowgroup"><tr className="text-xs text-slate-500" role="row"><th scope="col" role="columnheader" className="py-1 pe-3 text-start">{t('Field')}</th><th scope="col" role="columnheader" className="py-1 pe-3 text-start">{t('Current')}</th><th scope="col" role="columnheader" className="py-1 pe-3 text-start">{t('Proposed')}</th><th scope="col" role="columnheader" className="py-1 text-start">{t('Changed')}</th></tr></thead>
                      <tbody className="block space-y-2 sm:table-row-group sm:space-y-0" role="rowgroup">
                        {item.diff.map(d => (
                          <tr key={d.field} role="row" data-testid="diff-row" className="block space-y-1 rounded-xl border border-slate-200 p-3 sm:table-row sm:space-y-0 sm:rounded-none sm:border-0 sm:border-t sm:border-slate-100 sm:p-0 dark:border-white/10">
                            <th scope="row" role="rowheader" className="block py-1.5 pe-3 text-start font-medium sm:table-cell">{t(d.labelKey)}</th>
                            <td role="cell" className="flex items-start justify-between gap-3 py-1 pe-3 text-start sm:table-cell sm:py-1.5"><span className="shrink-0 text-xs text-slate-500 sm:hidden" aria-hidden="true">{t('Current')}</span><div className="min-w-0 text-end sm:text-start"><span dir="ltr"><bdi>{d.before || '—'}</bdi></span>{hijriLine(d.before, locale) && <span className="block text-xs text-slate-500" data-testid={`hijri-before-${d.field}`} dir="auto">{t('Hijri: {date}', { date: hijriLine(d.before, locale) })}</span>}</div></td>
                            <td role="cell" className={`flex items-start justify-between gap-3 py-1 pe-3 text-start sm:table-cell sm:py-1.5 ${d.changed ? 'font-bold' : ''}`}><span className="shrink-0 text-xs font-normal text-slate-500 sm:hidden" aria-hidden="true">{t('Proposed')}</span><div className="min-w-0 text-end sm:text-start"><span dir="ltr"><bdi>{d.after || '—'}</bdi></span>{hijriLine(d.after, locale) && <span className="block text-xs font-normal text-slate-500" data-testid={`hijri-after-${d.field}`} dir="auto">{t('Hijri: {date}', { date: hijriLine(d.after, locale) })}</span>}</div></td>
                            <td role="cell" className="py-1 text-start sm:table-cell sm:py-1.5"><span className={d.changed ? 'rounded bg-amber-100 px-1.5 py-0.5 text-xs font-semibold text-amber-900 dark:bg-amber-500/20 dark:text-amber-200' : 'text-xs text-slate-500'}>{d.changed ? t('Changed') : t('Unchanged')}</span></td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  {item.documents.length > 0 && (
                    <div>
                      <h3 className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Attachments')}</h3>
                      <ul className="mt-1 text-sm">
                        {item.documents.map((d, index) => (
                          <li key={d.id ?? `${d.fileName}-${index}`} className="flex flex-wrap items-center justify-between gap-2 py-1">
                            <span className="min-w-0"><bdi>{d.fileName}</bdi> · {t(`ess.completion.doc.${d.kind}`)}{scanLabelKey(scanState(d.scanStatus)) && <> · <span data-testid="scan-label">{t(scanLabelKey(scanState(d.scanStatus)) as string)}</span></>}</span>
                            {scanRefusalKey(scanState(d.scanStatus)) && <span className="basis-full text-xs text-rose-700 dark:text-rose-300" data-testid="scan-refused">{t(scanRefusalKey(scanState(d.scanStatus)) as string)}</span>}
                            {!d.id && canDownloadScanned(scanState(d.scanStatus)) && <span className="text-xs text-slate-500" data-testid="attachment-hidden">{t('You need the sensitive-data permission to download this file.')}</span>}
                            {d.id && canDownloadScanned(scanState(d.scanStatus)) && <a href={hrCompletionApi.documentUrl(d.id)} className="inline-flex items-center gap-1 text-xs font-semibold text-sapphire underline dark:text-cyanAccent"><Download className="h-3.5 w-3.5" aria-hidden="true" />{t('Download')}</a>}
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}
                  <div className="flex flex-wrap gap-2">
                    <button type="button" className={essPrimaryButton} onClick={() => { setDecision({ item, kind: 'accept' }); setNote(''); setOverride(''); }}><Check className="h-4 w-4" aria-hidden="true" />{t('Accept')}</button>
                    <button type="button" className="inline-flex items-center gap-1.5 rounded-xl border border-slate-300 px-4 py-2 text-sm font-semibold text-slate-700 dark:border-white/20 dark:text-slate-200" onClick={() => { setDecision({ item, kind: 'reject' }); setNote(''); }}><RotateCcw className="h-4 w-4" aria-hidden="true" />{t('Request changes')}</button>
                    <Link href={employeeWorkspaceHref(item.employee.id, { section: 'access-onboarding' })} className="inline-flex items-center rounded-xl px-3 py-2 text-sm font-semibold text-sapphire underline dark:text-cyanAccent">{t('Open employee')}</Link>
                  </div>
                  {decision?.item.requestId === item.requestId && (
                    <form className="space-y-3 rounded-xl bg-slate-50 p-3 dark:bg-white/[0.04]" onSubmit={e => { e.preventDefault(); void run(); }}>
                      <label htmlFor={ids.note} className="block space-y-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
                        <span>{decision.kind === 'reject' ? t('Note to the employee (required)') : t('Review note')}</span>
                        <textarea id={ids.note} ref={noteRef} rows={3} maxLength={500} className={essInput} value={note} onChange={e => setNote(e.target.value)} required={decision.kind === 'reject'} />
                      </label>
                      {decision.kind === 'accept' && item.section === 'id' && canOverride && (
                        <label htmlFor={ids.override} className="block space-y-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
                          <span>{t('Override reason (needs permission)')}</span>
                          <input id={ids.override} className={essInput} value={override} maxLength={500} onChange={e => setOverride(e.target.value)} />
                          <span className="block font-normal text-slate-500">{t('The ID number fails the checksum. Only an authorised HR user can accept it, with a reason.')}</span>
                        </label>
                      )}
                      <div className="flex flex-wrap gap-2">
                        <button type="submit" className={essPrimaryButton} disabled={busy || (decision.kind === 'reject' && !note.trim())}>
                          {busy && <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />}{decision.kind === 'reject' ? t('Send back to employee') : t('Accept')}
                        </button>
                        <button type="button" className="rounded-xl px-3 py-2 text-sm font-semibold text-slate-600 dark:text-slate-300" onClick={() => setDecision(null)} disabled={busy}>{t('Cancel')}</button>
                      </div>
                    </form>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
