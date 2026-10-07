'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { essActionsApi, type EssBalance } from '@/src/api/ess';
import type { LeaveRequest, LeaveType } from '@/src/api/leave';
import { StatusChip } from '@/src/components/StatusChip';
import { EssCard, EssEmpty, EssField, EssLoadError, EssNotice, EssPageHeader, essInput, essPrimaryButton, useOwnEmployeeId, useEssDate, useCanWriteEss, EssReadOnly } from '@/src/components/ess/EssParts';
import { useLocale } from '@/src/contexts/LocaleContext';
import { canCancelLeave, leaveStatus } from '@/src/lib/essSelfService';
import { isDeclarableLeave, isSaudiStatutoryLeave } from '@/src/lib/ksaStatutoryLeave';
import { fillTemplate } from '@/src/lib/gradeLoanLimits';
import { requestFailureReason } from '@/src/lib/requestFailure';

const blankForm = { leaveTypeId: '', startDate: '', endDate: '', halfDay: false, reason: '', statutoryEventDate: '', separateEventReason: '' };

/**
 * Employee self-service: my leave balances, apply for leave, my requests, cancel one that is still
 * waiting for approval. Gated on ess.read; every call is limited to the caller's own record.
 */
export function MyLeavePage() {
  const { t, locale } = useLocale();
  const fmtDate = useEssDate();
  const canWrite = useCanWriteEss();
  const ownEmployeeId = useOwnEmployeeId();
  const [balances, setBalances] = useState<EssBalance[]>([]);
  const [types, setTypes] = useState<LeaveType[]>([]);
  const [requests, setRequests] = useState<LeaveRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [form, setForm] = useState(blankForm);
  const [submitting, setSubmitting] = useState(false);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [cancellingId, setCancellingId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const employeeId = await ownEmployeeId();
      const [b, ty, r] = await Promise.all([essActionsApi.leaveBalances(), essActionsApi.leaveTypes(), essActionsApi.myLeaveRequests(employeeId)]);
      setBalances(b);
      setTypes(ty);
      setRequests(r);
    } catch (e) {
      setLoadError(e);
    } finally {
      setLoading(false);
    }
  }, [ownEmployeeId]);

  useEffect(() => { void load(); }, [load]);

  const selectedType = types.find((x) => x.id === form.leaveTypeId);
  const statutoryKind = selectedType ? isSaudiStatutoryLeave(selectedType.code, selectedType.nameEn, selectedType.category) : null;
  const declarable = isDeclarableLeave(statutoryKind);
  const typeName = (ty: LeaveType) => (locale === 'ar' && ty.nameAr ? ty.nameAr : ty.nameEn);
  /** A balance or request names its type in English; show the type's Arabic name when it has one. */
  const typeNameById = (id: string, stored: string) => {
    const ty = types.find((x) => x.id === id);
    return ty ? typeName(ty) : stored;
  };
  const sorted = useMemo(() => [...requests].sort((a, b) => b.startDate.localeCompare(a.startDate)), [requests]);
  const set = <K extends keyof typeof blankForm>(k: K, v: (typeof blankForm)[K]) => setForm((f) => ({ ...f, [k]: v }));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setNotice(null);
    if (!form.leaveTypeId || !form.startDate || !form.endDate) {
      setNotice({ tone: 'error', text: t('Choose the leave type and the first and last day.') });
      return;
    }
    if (form.endDate < form.startDate) {
      setNotice({ tone: 'error', text: t('The last day cannot be before the first day.') });
      return;
    }
    if (selectedType?.requiresReason && !form.reason.trim()) {
      setNotice({ tone: 'error', text: t('This leave type needs a reason.') });
      return;
    }
    setSubmitting(true);
    try {
      await essActionsApi.applyLeave({
        leaveTypeId: form.leaveTypeId,
        startDate: form.startDate,
        endDate: form.endDate,
        dayType: form.halfDay ? 'Half' : 'Full',
        reason: form.reason.trim(),
        statutoryEventDate: declarable && form.statutoryEventDate ? form.statutoryEventDate : undefined,
        separateEventReason: declarable && form.separateEventReason.trim() ? form.separateEventReason.trim() : undefined,
      });
      setForm(blankForm);
      setNotice({ tone: 'ok', text: t('Your leave request was sent for approval.') });
      await load();
    } catch (err) {
      setNotice({ tone: 'error', text: t(requestFailureReason(err)) });
    } finally {
      setSubmitting(false);
    }
  };

  const cancel = async (r: LeaveRequest) => {
    if (!window.confirm(t('Cancel this leave request?'))) return;
    setCancellingId(r.id);
    setNotice(null);
    try {
      await essActionsApi.cancelLeave(r.id, 'Cancelled by the employee in self-service');
      setNotice({ tone: 'ok', text: t('Your leave request was cancelled.') });
      await load();
    } catch (err) {
      setNotice({ tone: 'error', text: t(requestFailureReason(err)) });
    } finally {
      setCancellingId(null);
    }
  };

  return (
    <div className="space-y-4">
      <EssPageHeader title={t('My Leave')} subtitle={t('Your leave balances and requests. Apply for leave and follow it through approval.')} />

      {loading ? (
        <div className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" aria-label={t('Loading your leave')} />
      ) : loadError ? (
        <EssLoadError error={loadError} onRetry={() => void load()} />
      ) : (
        <>
          <EssCard title={t('Leave balances')} testId="my-leave-balances">
            {balances.length === 0 ? (
              <EssEmpty text={t('No leave balances have been set up for you yet. Ask HR if you expected to see one.')} />
            ) : (
              <ul className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
                {balances.map((b) => (
                  <li key={b.leaveTypeId} className="rounded-xl bg-slate-50 p-3 dark:bg-white/[0.03]">
                    <p className="text-xs font-semibold text-slate-600 dark:text-slate-300">{typeNameById(b.leaveTypeId, b.leaveTypeName)}</p>
                    {b.statutoryEntitlementDays != null ? (
                      <>
                        <p className="mt-1 text-sm font-bold text-emerald-700 dark:text-emerald-300">{t('Statutory entitlement')}</p>
                        <p className="text-xs text-slate-600 dark:text-slate-300">
                          {fillTemplate(t('{days} days per event, set by Saudi labour law.'), { days: b.statutoryEntitlementDays })}
                        </p>
                      </>
                    ) : (
                      <p className="mt-1 text-sm font-bold text-slate-800 dark:text-slate-100">
                        {fillTemplate(t('{available} of {entitled} days available'), { available: b.available, entitled: b.entitled })}
                      </p>
                    )}
                    {b.pending > 0 && (
                      <p className="text-[11px] text-amber-700 dark:text-amber-300">{fillTemplate(t('{days} days waiting for approval'), { days: b.pending })}</p>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </EssCard>

          {notice && <EssNotice tone={notice.tone}>{notice.text}</EssNotice>}

          <div className="grid gap-4 lg:grid-cols-[22rem_1fr]">
            <EssCard title={t('Apply for leave')} testId="my-leave-apply">
              {!canWrite ? (
                <EssReadOnly />
              ) : types.length === 0 ? (
                <EssEmpty text={t('No leave types are available yet. Ask HR to set them up.')} />
              ) : (
                <form className="space-y-3" onSubmit={(e) => void submit(e)}>
                  <EssField label={t('Leave type')}>
                    <select className={essInput} value={form.leaveTypeId} onChange={(e) => set('leaveTypeId', e.target.value)}>
                      <option value="">{t('Choose a leave type')}</option>
                      {types.map((ty) => <option key={ty.id} value={ty.id}>{typeName(ty)}</option>)}
                    </select>
                  </EssField>
                  <div className="grid grid-cols-2 gap-2">
                    <EssField label={t('First day')}>
                      <input type="date" className={essInput} value={form.startDate} onChange={(e) => set('startDate', e.target.value)} />
                    </EssField>
                    <EssField label={t('Last day')}>
                      <input type="date" className={essInput} value={form.endDate} min={form.startDate || undefined} onChange={(e) => set('endDate', e.target.value)} />
                    </EssField>
                  </div>
                  {selectedType?.isHalfDayAllowed && form.startDate && form.startDate === form.endDate && (
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <input type="checkbox" checked={form.halfDay} onChange={(e) => set('halfDay', e.target.checked)} className="rounded" />
                      {t('Half a day only')}
                    </label>
                  )}
                  <EssField label={selectedType?.requiresReason ? t('Reason (required)') : t('Reason (optional)')}>
                    <textarea className={essInput} rows={3} value={form.reason} onChange={(e) => set('reason', e.target.value)} />
                  </EssField>
                  {declarable && (
                    <>
                      <EssField label={t('Date of the event (death, birth or marriage)')}>
                        <input type="date" className={essInput} value={form.statutoryEventDate} onChange={(e) => set('statutoryEventDate', e.target.value)} />
                      </EssField>
                      <EssField label={t('Separate event? Say why (for example, a second bereavement)')}>
                        <textarea className={essInput} rows={2} value={form.separateEventReason} onChange={(e) => set('separateEventReason', e.target.value)}
                          placeholder={t('Only if this is a different event from your earlier leave of this kind. The event date is required too.')} />
                      </EssField>
                    </>
                  )}
                  {selectedType?.requiresAttachment && (
                    <p className="text-[11px] text-slate-500 dark:text-slate-400">{t('This leave type needs a supporting document. HR will ask you for it.')}</p>
                  )}
                  <button type="submit" disabled={submitting} className={`${essPrimaryButton} w-full`}>
                    {submitting ? t('Sending…') : t('Send for approval')}
                  </button>
                </form>
              )}
            </EssCard>

            <EssCard title={t('My leave requests')} testId="my-leave-requests">
              {sorted.length === 0 ? (
                <EssEmpty text={t('You have not requested any leave yet.')} />
              ) : (
                <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                  {sorted.map((r) => {
                    const s = leaveStatus(r.status);
                    return (
                      <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                        <div className="min-w-0">
                          <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">{typeNameById(r.leaveTypeId, r.leaveTypeName)}</p>
                          <p className="text-xs text-slate-500 dark:text-slate-400">
                            {fillTemplate(t('{start} to {end} ({days} days)'), { start: fmtDate(r.startDate), end: fmtDate(r.endDate), days: r.totalDays })}
                          </p>
                          {r.status === 'Rejected' && r.rejectionReason && (
                            <p className="text-xs text-rose-600 dark:text-rose-400">{r.rejectionReason}</p>
                          )}
                        </div>
                        <div className="flex items-center gap-2">
                          <StatusChip label={t(s.label)} tone={s.tone} dot />
                          {canWrite && canCancelLeave(r.status) && (
                            <button type="button" disabled={cancellingId === r.id} onClick={() => void cancel(r)}
                              className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-60 dark:border-white/[0.12] dark:text-slate-200 dark:hover:bg-white/[0.06]">
                              {cancellingId === r.id ? t('Cancelling…') : t('Cancel request')}
                            </button>
                          )}
                        </div>
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
