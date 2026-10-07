'use client';

import { useCallback, useEffect, useState } from 'react';
import { essActionsApi } from '@/src/api/ess';
import type { OvertimeRequest, OvertimeType } from '@/src/api/overtime';
import { StatusChip } from '@/src/components/StatusChip';
import { EssCard, EssEmpty, EssField, EssLoadError, EssNotice, EssPageHeader, essInput, essPrimaryButton, useOwnEmployeeId, useEssDate, useCanWriteEss, EssReadOnly } from '@/src/components/ess/EssParts';
import { useLocale } from '@/src/contexts/LocaleContext';
import { localClock, overtimeStatus, overtimeWindow, splitMinutes } from '@/src/lib/essSelfService';
import { fillTemplate } from '@/src/lib/gradeLoanLimits';
import { requestFailureReason } from '@/src/lib/requestFailure';

const blankForm = { workDate: '', start: '', end: '', overtimeTypeId: '', reason: '' };

/**
 * Employee self-service: request overtime and follow my requests through approval. Gated on
 * ess.read; the server limits both the list and the request to the caller's own record.
 */
export function MyOvertimePage() {
  const { t } = useLocale();
  const fmtDate = useEssDate();
  const canWrite = useCanWriteEss();
  const ownEmployeeId = useOwnEmployeeId();
  const [employeeId, setEmployeeId] = useState<number | null>(null);
  const [types, setTypes] = useState<OvertimeType[]>([]);
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [form, setForm] = useState(blankForm);
  const [submitting, setSubmitting] = useState(false);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const id = await ownEmployeeId();
      const [ty, r] = await Promise.all([essActionsApi.overtimeTypes(), essActionsApi.myOvertime(id)]);
      setEmployeeId(id);
      setTypes(ty);
      setRequests(r);
    } catch (e) {
      setLoadError(e);
    } finally {
      setLoading(false);
    }
  }, [ownEmployeeId]);

  useEffect(() => { void load(); }, [load]);

  const duration = (minutes: number) => fillTemplate(t('{hours} h {minutes} min'), splitMinutes(minutes));
  const set = <K extends keyof typeof blankForm>(k: K, v: (typeof blankForm)[K]) => setForm((f) => ({ ...f, [k]: v }));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setNotice(null);
    if (!form.workDate || !form.start || !form.end) {
      setNotice({ tone: 'error', text: t('Choose the date and the start and end times.') });
      return;
    }
    if (!form.reason.trim()) {
      setNotice({ tone: 'error', text: t('Say what the overtime was for.') });
      return;
    }
    const span = overtimeWindow(form.workDate, form.start, form.end);
    if (!span || employeeId == null) {
      setNotice({ tone: 'error', text: t('Choose the date and the start and end times.') });
      return;
    }
    setSubmitting(true);
    try {
      await essActionsApi.requestOvertime(employeeId, {
        workDate: form.workDate,
        ...span,
        reason: form.reason.trim(),
        overtimeTypeId: form.overtimeTypeId || undefined,
      });
      setForm(blankForm);
      setNotice({ tone: 'ok', text: t('Your overtime request was sent for approval.') });
      await load();
    } catch (err) {
      setNotice({ tone: 'error', text: t(requestFailureReason(err)) });
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="space-y-4">
      <EssPageHeader title={t('My Overtime')} subtitle={t('Request overtime you have worked and follow it through approval.')} />

      {loading ? (
        <div className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" aria-label={t('Loading your overtime')} />
      ) : loadError ? (
        <EssLoadError error={loadError} onRetry={() => void load()} />
      ) : (
        <>
          {notice && <EssNotice tone={notice.tone}>{notice.text}</EssNotice>}
          <div className="grid gap-4 lg:grid-cols-[22rem_1fr]">
            <EssCard title={t('Request overtime')} testId="my-overtime-apply">
              {!canWrite ? <EssReadOnly /> : (
              <form className="space-y-3" onSubmit={(e) => void submit(e)}>
                <EssField label={t('Date worked')}>
                  <input type="date" className={essInput} value={form.workDate} onChange={(e) => set('workDate', e.target.value)} />
                </EssField>
                <div className="grid grid-cols-2 gap-2">
                  <EssField label={t('Start time')}>
                    <input type="time" className={essInput} value={form.start} onChange={(e) => set('start', e.target.value)} />
                  </EssField>
                  <EssField label={t('End time')}>
                    <input type="time" className={essInput} value={form.end} onChange={(e) => set('end', e.target.value)} />
                  </EssField>
                </div>
                <p className="text-[11px] text-slate-500 dark:text-slate-400">{t('An end time earlier than the start time counts as finishing the next morning.')}</p>
                {types.length > 0 && (
                  <EssField label={t('Overtime type (optional)')}>
                    <select className={essInput} value={form.overtimeTypeId} onChange={(e) => set('overtimeTypeId', e.target.value)}>
                      <option value="">{t('Not specified')}</option>
                      {types.map((ty) => <option key={ty.id} value={ty.id}>{ty.name}</option>)}
                    </select>
                  </EssField>
                )}
                <EssField label={t('Reason (required)')}>
                  <textarea className={essInput} rows={3} value={form.reason} onChange={(e) => set('reason', e.target.value)} />
                </EssField>
                <button type="submit" disabled={submitting} className={`${essPrimaryButton} w-full`}>
                  {submitting ? t('Sending…') : t('Send for approval')}
                </button>
              </form>
              )}
            </EssCard>

            <EssCard title={t('My overtime requests')} testId="my-overtime-requests">
              {requests.length === 0 ? (
                <EssEmpty text={t('You have not requested any overtime yet.')} />
              ) : (
                <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                  {requests.map((r) => {
                    const s = overtimeStatus(r.status);
                    const approved = r.status === 'Approved' || r.status === 'Paid';
                    return (
                      <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                        <div className="min-w-0">
                          <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">
                            {fmtDate(r.workDate)} · <span dir="ltr">{localClock(r.startTimeUtc)}–{localClock(r.endTimeUtc)}</span>
                          </p>
                          <p className="text-xs text-slate-500 dark:text-slate-400">
                            {approved && r.approvedMinutes > 0
                              ? fillTemplate(t('{requested} requested, {approved} approved'), { requested: duration(r.requestedMinutes), approved: duration(r.approvedMinutes) })
                              : fillTemplate(t('{requested} requested'), { requested: duration(r.requestedMinutes) })}
                          </p>
                          {r.reason && <p className="truncate text-xs text-slate-500 dark:text-slate-400">{r.reason}</p>}
                        </div>
                        <StatusChip label={t(s.label)} tone={s.tone} dot />
                      </li>
                    );
                  })}
                </ul>
              )}
            </EssCard>
          </div>
        </>
      )}
    </div>
  );
}
