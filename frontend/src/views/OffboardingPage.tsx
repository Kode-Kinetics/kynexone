'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { calendarDaysBetween, daysUntilCalendarDate } from '../lib/calendarDate';
import { UserMinus, CheckCircle2, Star, Undo2, AlertTriangle, ShieldOff, Search, Loader2, ExternalLink, Clock } from 'lucide-react';
import {
  offboardingApi, SEPARATION_TYPE_FALLBACK,
  type Offboarding, type OffboardingSummary, type SeparationTypeInfo,
} from '../api/offboarding';
import { payrollApi, type FinalSettlementListRow } from '../api/payroll';
import { employeesApi } from '../api/employees';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { requestFailureReason } from '../lib/requestFailure';
import { Modal } from '../components/Modal';
import { ConfirmDialog, type ConfirmState } from '../components/ui/ConfirmDialog';
import { EnumLabel } from '../components/EnumLabel';
import { FINAL_SETTLEMENT_HREF, isSettlementCleared, settlementFor } from '../lib/offboardingSettlement';

const EXIT_REASONS = ['Compensation', 'Career Growth', 'Management', 'Work-Life Balance', 'Relocation', 'Job Content', 'Company Culture', 'Better Offer', 'Personal', 'Other'] as const;
const EXIT_INTERVIEW_STATUSES = ['Pending', 'Scheduled', 'Completed', 'Waived'] as const;

/**
 * S2-F4 — who can be offboarded. Mirrors EstablishmentOccupancy.OccupyingStatuses minus Offboarded
 * (already leaving). The picker used to filter `status: 'Active'` server-side, so the
 * "suspended pending investigation, then dismissed" path — the exact case Article 80 exists for —
 * could not be started from the UI at all.
 */
const OFFBOARDABLE_STATUSES = new Set(['Active', 'Suspended']);

function addDays(iso: string, days: number) {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
/** KSA working week is Sun–Thu, so a computed LWD routinely lands on a Friday or Saturday. */
function weekendName(iso: string): 'Friday' | 'Saturday' | null {
  if (!iso) return null;
  const day = new Date(`${iso}T00:00:00Z`).getUTCDay();
  return day === 5 ? 'Friday' : day === 6 ? 'Saturday' : null;
}
function apiMessage(e: unknown, fallback: string) {
  return (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? fallback;
}

export function OffboardingPage() {
  // Initiating a separation is `employees.approve` on the API (OffboardingController.Initiate). The
  // page is open to employees.write and payroll.approve holders, who were offered the button and got a 403.
  const { hasPermission } = useAuth();
  const { t } = useLocale();
  const canInitiate = hasPermission('employees.approve');
  const canReadSettlements = hasPermission('payroll.read');
  const [items, setItems] = useState<Offboarding[]>([]);
  const [summary, setSummary] = useState<OffboardingSummary | null>(null);
  // null = the settlement list is not available to this user (or failed): the card says so and
  // falls back to the manual tick instead of pretending there is no settlement.
  const [settlements, setSettlements] = useState<FinalSettlementListRow[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [showInitiate, setShowInitiate] = useState(false);
  // A failed load used to be swallowed and render "No offboardings yet" — HR read an outage as
  // "nobody is leaving". The list and summary now fail independently, and each says so.
  const [listError, setListError] = useState('');
  const [summaryError, setSummaryError] = useState('');
  const [listLoaded, setListLoaded] = useState(false);
  // Only the newest load may write state, so a slow earlier response (or Strict Mode's double
  // mount) can never overwrite a newer result.
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++loadSeq.current;
    setLoading(true);
    const [list, sum, settle] = await Promise.allSettled([
      offboardingApi.list(),
      offboardingApi.summary(),
      canReadSettlements ? payrollApi.listFinalSettlements() : Promise.reject(new Error('no payroll.read')),
    ]);
    if (seq !== loadSeq.current) return;
    if (list.status === 'fulfilled') { setItems(list.value); setListLoaded(true); setListError(''); }
    else setListError(requestFailureReason(list.reason));
    if (sum.status === 'fulfilled') { setSummary(sum.value); setSummaryError(''); }
    else setSummaryError(requestFailureReason(sum.reason));
    setSettlements(settle.status === 'fulfilled' ? settle.value.rows : null);
    setLoading(false);
  }, [canReadSettlements]);
  useEffect(() => { void load(); }, [load]);

  const inProgress = items.filter(o => o.status === 'InProgress');
  const closed = items.filter(o => o.status !== 'InProgress');
  const listUnavailable = !!listError && !listLoaded;
  const staleNotices = [
    listError && listLoaded ? t('The list could not be refreshed and shows the last successful load. {reason}', { reason: listError }) : '',
    summaryError
      ? (summary
        ? t('The summary figures could not be refreshed and show the last successful load. {reason}', { reason: summaryError })
        : t('The summary figures are unavailable. {reason}', { reason: summaryError }))
      : '',
  ].filter(Boolean);

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Offboarding')}</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{t('Manage separations end-to-end — notice period, exit interview, clearance checklist and final archive.')}</p>
        </div>
        {canInitiate && <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={() => setShowInitiate(true)}>
          <UserMinus className="h-3.5 w-3.5" aria-hidden="true" /> {t('Initiate Offboarding')}
        </button>}
      </div>

      {!loading && !listUnavailable && staleNotices.length > 0 && (
        <div role="alert" className="flex items-start justify-between gap-3 rounded-lg bg-amber-50 px-3 py-2.5 text-sm text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">
          <div className="space-y-1">{staleNotices.map(n => <p key={n}>{n}</p>)}</div>
          <button type="button" onClick={() => void load()} className="btn-secondary h-7 shrink-0 px-2 text-xs">{t('Retry')}</button>
        </div>
      )}

      {/* Summary + attrition insight */}
      {summary && (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <SummaryCard label={t('Serving Notice')} value={summary.inNotice} tone="bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400" />
          <SummaryCard label={t('Exit Interviews Pending')} value={summary.exitInterviewsPending} tone="bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400" />
          <SummaryCard label={t('Avg Exit Rating')} value={summary.avgExitRating ? `${summary.avgExitRating} / 5` : '—'} tone="bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent" />
          <SummaryCard label={t('Completed')} value={summary.completed} tone="bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400" />
        </div>
      )}
      {summary && summary.reasons.length > 0 && (
        <div className="surface p-4">
          <p className="mb-3 text-sm font-semibold text-slate-900 dark:text-white">{t('Why people leave')}</p>
          <div className="space-y-1.5">
            {summary.reasons.map(r => {
              const max = Math.max(...summary.reasons.map(x => x.count));
              return (
                <div key={r.category} className="flex items-center gap-3 text-xs">
                  <span className="w-36 shrink-0 text-slate-600 dark:text-slate-300">{exitReasonLabel(t, r.category)}</span>
                  <div className="h-2 flex-1 rounded-full bg-slate-100 dark:bg-white/10">
                    <div className="h-2 rounded-full bg-sapphire dark:bg-cyanAccent" style={{ width: `${(r.count / max) * 100}%` }} />
                  </div>
                  <span className="w-6 text-end font-medium text-slate-500">{r.count}</span>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {loading ? (
        <div className="flex justify-center py-12" role="status" aria-label={t('Loading…')}><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
      ) : listUnavailable ? (
        <div role="alert" className="surface flex flex-col items-center gap-3 p-10 text-center">
          <AlertTriangle className="h-6 w-6 text-amber-500" aria-hidden="true" />
          <p className="text-sm font-medium text-slate-700 dark:text-slate-200">{t('Offboardings could not be loaded. {reason}', { reason: listError })}</p>
          <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">{t('This does not mean there are no separations in progress.')}</p>
          <button type="button" onClick={() => void load()} className="btn-secondary h-8 px-3 text-sm">{t('Retry')}</button>
        </div>
      ) : items.length === 0 ? (
        <div className="surface p-10 text-center text-sm text-slate-500">{t('No offboardings yet. Click “Initiate Offboarding” to start a separation.')}</div>
      ) : (
        <div className="space-y-3">
          {inProgress.map(o => <OffboardingCard key={o.id} o={o} settlements={settlements} onChange={load} />)}
          {closed.length > 0 && <p className="pt-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{t('Closed')}</p>}
          {closed.map(o => <OffboardingCard key={o.id} o={o} settlements={settlements} onChange={load} />)}
        </div>
      )}

      {showInitiate && <InitiateModal onClose={() => setShowInitiate(false)} onDone={() => { setShowInitiate(false); load(); }} />}
    </div>
  );
}

function exitReasonLabel(t: (k: string) => string, category: string) {
  return (EXIT_REASONS as readonly string[]).includes(category) ? t(category) : category;
}

function SummaryCard({ label, value, tone }: { label: string; value: string | number; tone: string }) {
  return (
    <div className="surface p-4">
      <p className="text-xs text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 inline-flex rounded-lg px-2 py-0.5 text-xl font-semibold ${tone}`}>{value}</p>
    </div>
  );
}

function OffboardingCard({ o, settlements, onChange }: { o: Offboarding; settlements: FinalSettlementListRow[] | null; onChange: () => void }) {
  const [busy, setBusy] = useState(false);
  const [editEi, setEditEi] = useState(false);
  const [error, setError] = useState('');
  const [showSettle, setShowSettle] = useState(false);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [showRescind, setShowRescind] = useState(false);
  // Logins the revoke left active because an Admin must switch them off (LinkedLoginDeactivationGate).
  const [heldLogins, setHeldLogins] = useState<{ email: string; message: string; messageAr: string }[]>([]);
  const { t, locale } = useLocale();
  const fx = useFormat();
  const [ei, setEi] = useState({ status: o.exitInterviewStatus, date: o.exitInterviewDate?.slice(0, 10) ?? '', reasonCategory: o.exitReasonCategory, rating: o.exitInterviewRating, notes: o.exitInterviewNotes });

  const settlement = settlements ? settlementFor(settlements, o.id) : null;
  // The settlement is cleared when Payroll says it was paid, or when the payment was recorded here.
  const settlementCleared = o.finalSettlementDone || isSettlementCleared(settlement);

  // "Access revoked" is handled separately below: it is an irreversible ACTION, not a note, so it must
  // not render as a togglable checkbox alongside the notes. The settlement is derived from Payroll
  // when the API lets this user read it, and only falls back to a manual tick when it does not.
  const checklist: [keyof Offboarding, string][] = [
    ['assetsReturned', t('Assets returned')],
    ['knowledgeHandover', t('Knowledge handover')],
    ...(settlements === null ? [['finalSettlementDone', t('Final settlement (ticked by hand)')] as [keyof Offboarding, string]] : []),
  ];
  const done = checklist.filter(([k]) => o[k] === true).length + (o.accessRevoked ? 1 : 0)
    + (settlements !== null && settlementCleared ? 1 : 0);
  const isProgress = o.status === 'InProgress';
  const daysLeft = isProgress ? daysUntilCalendarDate(o.lastWorkingDay) : null;

  const run = async (fn: () => Promise<unknown>, fallback: string) => {
    setBusy(true); setError('');
    try { await fn(); onChange(); }
    catch (e) { setError(apiMessage(e, fallback)); }
    finally { setBusy(false); }
  };

  const toggle = (k: string, v: boolean) =>
    run(() => offboardingApi.checklist(o.id, { [k]: v }), t('Could not update the checklist.'));
  const revokeAccess = () => run(async () => {
    const result = await offboardingApi.revokeAccess(o.id);
    setHeldLogins(result.linkedLoginHeld ?? []);
  }, t('Could not revoke access.'));
  const saveEi = () => run(async () => { await offboardingApi.exitInterview(o.id, ei); setEditEi(false); }, t('Could not save the exit interview.'));

  const askRevoke = () => setConfirm({
    title: t('Revoke system access now?'),
    message: t('{name}’s account is deactivated and every active session ends immediately. This cannot be undone from here — only rescinding the offboarding restores it.', { name: o.employeeName }),
    confirmLabel: t('Revoke access'),
    cancelLabel: t('Cancel'),
    busyLabel: t('Working…'),
    closeLabel: t('Close'),
    onConfirm: () => revokeAccess(),
  });
  const askComplete = () => setConfirm({
    title: t('Complete and archive this offboarding?'),
    message: (
      <>
        <p>{t('This archives {name}’s employee record, ends their logins and removes them from headcount.', { name: o.employeeName })}</p>
        {!settlementCleared && <p className="mt-2 font-medium text-danger-fg">{t('The final settlement has not been paid yet.')}</p>}
        <p className="mt-2">{t('Once archived, this cannot be rescinded.')}</p>
      </>
    ),
    confirmLabel: t('Complete & archive'),
    cancelLabel: t('Cancel'),
    busyLabel: t('Working…'),
    closeLabel: t('Close'),
    onConfirm: () => run(() => offboardingApi.complete(o.id), t('Could not complete the offboarding.')),
  });

  return (
    <div className="surface p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-semibold text-slate-800 dark:text-white">{o.employeeName}</span>
            <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-500 dark:bg-white/10 dark:text-slate-400">{o.employeeCode}</span>
            <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${o.status === 'InProgress' ? 'bg-amber-50 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400' : o.status === 'Completed' ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500'}`}>{o.status === 'InProgress' ? t('Serving notice') : <EnumLabel enum="Status" value={o.status} />}</span>
          </div>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            {o.separationType} · {o.designation || '—'} · {o.department || '—'} · {t('Last day {date}', { date: fx.date(o.lastWorkingDay) })}
            {daysLeft !== null && <span className={`ms-1 font-medium ${daysLeft < 0 ? 'text-rose-500' : daysLeft <= 7 ? 'text-amber-500' : 'text-slate-500'}`}>({daysLeft < 0 ? t('{days}d overdue', { days: -daysLeft }) : t('{days}d left', { days: daysLeft })})</span>}
            {!o.rehireEligible && <span className="ms-1 text-rose-500">· {t('Not rehire-eligible')}</span>}
          </p>
        </div>
        {isProgress && (
          <div className="flex items-center gap-2">
            <button type="button" className="btn-secondary flex items-center gap-1 text-xs" onClick={() => setShowRescind(true)} disabled={busy}><Undo2 className="h-3 w-3" aria-hidden="true" /> {t('Rescind')}</button>
            {!settlementCleared && (
              <button type="button" className="btn-secondary text-xs" onClick={() => setShowSettle(true)} disabled={busy}>{t('Record settlement payment')}</button>
            )}
            <button type="button" className="btn-primary flex items-center gap-1 text-xs" onClick={askComplete} disabled={busy}><CheckCircle2 className="h-3 w-3" aria-hidden="true" /> {t('Complete & archive')}</button>
          </div>
        )}
      </div>

      {o.reason && <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">“{o.reason}”</p>}
      {error && <p role="alert" className="mt-2 rounded-lg bg-rose-50 px-2.5 py-1.5 text-xs text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}
      {heldLogins.map(h => (
        <p key={h.email} role="status" data-testid="offboarding-login-held"
          className="mt-2 rounded-lg bg-amber-50 px-2.5 py-1.5 text-xs text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">
          <span className="font-semibold">{t('Account still active — an Admin must deactivate it')}</span>
          {' · '}{h.email}{' · '}{locale === 'ar' ? h.messageAr : h.message}
        </p>
      ))}

      <div className="mt-3 grid gap-4 lg:grid-cols-2">
        {/* Checklist */}
        <div>
          <p className="mb-1.5 text-xs font-semibold text-slate-700 dark:text-slate-300">{t('Clearance checklist')} <span className="text-slate-500">({done}/4)</span></p>
          <div className="grid grid-cols-2 gap-1.5">
            {checklist.map(([k, label]) => (
              <label key={k} className="flex cursor-pointer items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
                <input type="checkbox" className="h-3.5 w-3.5 rounded border-slate-300 dark:border-white/20 dark:bg-white/5" checked={o[k] === true} disabled={!isProgress || busy} onChange={e => toggle(k, e.target.checked)} />
                {label}
              </label>
            ))}
            {/* S2-B2 — an ACTION, not a note. Ticking a box used to revoke nothing at all: the real
                revocation only ran inside Complete, so the ex-employee kept a working login for the
                whole settlement window. It is one-way, so it is rendered as one-way. */}
            {o.accessRevoked ? (
              <span className="flex items-center gap-2 text-xs font-medium text-emerald-600 dark:text-emerald-400">
                <ShieldOff className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                {t('Access revoked')}{o.accessRevokedAtUtc ? ` · ${fx.date(o.accessRevokedAtUtc)}` : ''}
              </span>
            ) : (
              <button
                type="button"
                className="flex items-center gap-1.5 justify-self-start rounded-lg border border-rose-300 px-2 py-0.5 text-xs font-medium text-rose-600 hover:bg-rose-50 disabled:opacity-50 dark:border-rose-500/40 dark:text-rose-400 dark:hover:bg-rose-500/10"
                disabled={!isProgress || busy}
                onClick={askRevoke}
              >
                {busy ? <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> : <ShieldOff className="h-3 w-3" aria-hidden="true" />} {t('Revoke access')}
              </button>
            )}
          </div>

          {/* The real settlement, from Payroll — not a box someone ticks. */}
          <div className="mt-3 rounded-lg border border-slate-200 p-2.5 text-xs dark:border-white/10" data-testid="offboarding-settlement">
            <p className="mb-1 font-semibold text-slate-700 dark:text-slate-300">{t('Final settlement')}</p>
            {settlements === null ? (
              <p className="text-slate-500 dark:text-slate-400">{t('Settlement status is not available to your role. Use the tick above and ask Payroll to confirm.')}</p>
            ) : settlement ? (
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className={`inline-flex items-center gap-1 font-medium ${settlementCleared ? 'text-emerald-600 dark:text-emerald-400' : 'text-amber-700 dark:text-amber-400'}`}>
                  {settlementCleared ? <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" /> : <Clock className="h-3.5 w-3.5" aria-hidden="true" />}
                  <EnumLabel enum="Status" value={settlement.status} />
                </span>
                <span className="font-semibold text-slate-800 dark:text-white">{t('Net payable {amount}', { amount: fx.money(settlement.netPayable, settlement.currency) })}</span>
                {settlement.isOverdue && <span className="text-rose-600 dark:text-rose-400">{t('{days}d overdue', { days: settlement.overdueDays })}</span>}
                <Link href={FINAL_SETTLEMENT_HREF} className="inline-flex items-center gap-1 text-sapphire hover:underline dark:text-cyanAccent">
                  {t('Open in Payroll')} <ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden="true" />
                </Link>
              </div>
            ) : (
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <span className={o.finalSettlementDone ? 'text-emerald-600 dark:text-emerald-400' : 'text-amber-700 dark:text-amber-400'}>
                  {o.finalSettlementDone ? t('Settlement payment recorded.') : t('Not calculated yet.')}
                </span>
                {isProgress && !o.finalSettlementDone && (
                  <Link href={FINAL_SETTLEMENT_HREF} className="inline-flex items-center gap-1 text-sapphire hover:underline dark:text-cyanAccent">
                    {t('Calculate in Payroll')} <ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden="true" />
                  </Link>
                )}
              </div>
            )}
          </div>
        </div>

        {/* Exit interview */}
        <div>
          <div className="mb-1.5 flex items-center justify-between">
            <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">{t('Exit interview')}
              <span className={`ms-1.5 rounded-full px-1.5 py-0.5 text-xs ${o.exitInterviewStatus === 'Completed' ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10' : 'bg-slate-100 text-slate-500 dark:bg-white/10'}`}><EnumLabel enum="Status" value={o.exitInterviewStatus} /></span>
            </p>
            {isProgress && !editEi && <button type="button" className="text-xs text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setEditEi(true)}>{t('Record')}</button>}
          </div>
          {editEi ? (
            <div className="space-y-2 rounded-lg border border-slate-200 p-2 dark:border-white/10">
              <div className="grid grid-cols-2 gap-2">
                <select className="select text-xs" value={ei.status} onChange={e => setEi(v => ({ ...v, status: e.target.value }))} aria-label={t('Exit interview status')}>
                  {EXIT_INTERVIEW_STATUSES.map(s => <option key={s} value={s}>{t(s)}</option>)}
                </select>
                <input type="date" className="input text-xs" value={ei.date} onChange={e => setEi(v => ({ ...v, date: e.target.value }))} aria-label={t('Exit interview date')} />
              </div>
              <select className="select w-full text-xs" value={ei.reasonCategory} onChange={e => setEi(v => ({ ...v, reasonCategory: e.target.value }))} aria-label={t('Primary reason for leaving')}>
                <option value="">{t('— Primary reason for leaving —')}</option>
                {EXIT_REASONS.map(r => <option key={r} value={r}>{t(r)}</option>)}
              </select>
              <div className="flex items-center gap-1">
                <span className="text-xs text-slate-500">{t('Rating')}</span>
                {[1, 2, 3, 4, 5].map(n => (
                  <button key={n} type="button" aria-label={t('Rate {n}', { n })} aria-pressed={n <= ei.rating} onClick={() => setEi(v => ({ ...v, rating: n }))}>
                    <Star className={`h-4 w-4 ${n <= ei.rating ? 'fill-amber-400 text-amber-400' : 'text-slate-300'}`} aria-hidden="true" />
                  </button>
                ))}
              </div>
              <textarea className="input w-full resize-none text-xs" rows={2} placeholder={t('Notes…')} aria-label={t('Notes')} value={ei.notes} onChange={e => setEi(v => ({ ...v, notes: e.target.value }))} />
              <div className="flex justify-end gap-2">
                <button type="button" className="btn-secondary text-xs" onClick={() => setEditEi(false)}>{t('Cancel')}</button>
                <button type="button" className="btn-primary text-xs" onClick={saveEi} disabled={busy}>{t('Save')}</button>
              </div>
            </div>
          ) : (
            <div className="text-xs text-slate-500 dark:text-slate-400">
              {o.exitReasonCategory ? <p>{t('Reason')}: <span className="text-slate-700 dark:text-slate-200">{exitReasonLabel(t, o.exitReasonCategory)}</span>{o.exitInterviewRating > 0 && <span> · {o.exitInterviewRating}/5</span>}</p> : <p className="text-slate-500">{t('Not yet recorded.')}</p>}
              {o.exitInterviewNotes && <p className="mt-1 italic">“{o.exitInterviewNotes}”</p>}
            </div>
          )}
        </div>
      </div>

      {showSettle && (
        <RecordSettlementModal
          o={o}
          onClose={() => setShowSettle(false)}
          onDone={() => { setShowSettle(false); onChange(); }}
        />
      )}
      {showRescind && (
        <RescindModal
          o={o}
          onClose={() => setShowRescind(false)}
          onDone={() => { setShowRescind(false); onChange(); }}
        />
      )}
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </div>
  );
}

/** Rescinding is recorded against the separation, so it asks for a reason in a real dialog (not window.prompt). */
function RescindModal({ o, onClose, onDone }: { o: Offboarding; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const submit = async () => {
    setSaving(true); setError('');
    try { await offboardingApi.cancel(o.id, reason.trim() || undefined); onDone(); }
    catch (e) { setError(apiMessage(e, t('Could not rescind the offboarding.'))); setSaving(false); }
  };
  return (
    <Modal
      isOpen
      title={t('Rescind this separation?')}
      onClose={() => { if (!saving) onClose(); }}
      closeLabel={t('Close')}
      size="sm"
      footer={<>
        <button type="button" className="btn-secondary" onClick={onClose} disabled={saving}>{t('Cancel')}</button>
        <button type="button" className="btn-danger" onClick={submit} disabled={saving}>{saving ? t('Working…') : t('Rescind')}</button>
      </>}
    >
      <div className="space-y-3">
        <p className="text-sm text-slate-600 dark:text-slate-300">{t('{name} stays employed, and any access that was revoked is restored.', { name: o.employeeName })}</p>
        <div>
          <label htmlFor={`rescind-${o.id}`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Why is this separation being withdrawn? This is recorded against the rescind.')}</label>
          <textarea id={`rescind-${o.id}`} className="input w-full resize-none" rows={3} value={reason} onChange={e => setReason(e.target.value)} />
        </div>
        {error && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{error}</p>}
      </div>
    </Modal>
  );
}

/**
 * S2-B2 — record a final settlement paid by bank transfer / cheque / cash.
 *
 * Without this the offboarding could never be completed unless the settlement was disbursed through a
 * payroll run, which no client opens for one leaver mid-month. The API posts the real DR payable /
 * CR cash journal, so this is a payment record, not a tickbox.
 */
function RecordSettlementModal({ o, onClose, onDone }: { o: Offboarding; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const [form, setForm] = useState({ method: 'BankTransfer', reference: '', amount: '', paidOn: new Date().toISOString().slice(0, 10) });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async () => {
    if (!form.reference.trim()) { setError(t('A bank reference or cheque number is required.')); return; }
    const amount = Number(form.amount);
    if (!Number.isFinite(amount) || amount <= 0) { setError(t('Enter the amount that was paid.')); return; }
    setSaving(true); setError('');
    try {
      await offboardingApi.recordExternalSettlementPayment(o.id, { ...form, reference: form.reference.trim(), amount });
      onDone();
    } catch (e) {
      setError(apiMessage(e, t('Could not record the payment.')));
      setSaving(false);
    }
  };

  return (
    <Modal
      isOpen
      title={t('Settlement paid outside payroll')}
      onClose={() => { if (!saving) onClose(); }}
      closeLabel={t('Close')}
      footer={<>
        <button type="button" className="btn-secondary text-sm" onClick={onClose} disabled={saving}>{t('Cancel')}</button>
        <button type="button" className="btn-primary text-sm" onClick={submit} disabled={saving}>{saving ? t('Recording…') : t('Record payment')}</button>
      </>}
    >
      <div className="space-y-3">
        <p className="text-xs text-slate-500 dark:text-slate-400">
          {t('For {name} ({code}). The amount must match the approved settlement’s net payable — the server confirms it and posts the ledger entry that clears the payable.', { name: o.employeeName, code: o.employeeCode })}
        </p>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="settle-method" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Method')}</label>
            <select id="settle-method" className="select w-full" value={form.method} onChange={e => setForm(f => ({ ...f, method: e.target.value }))}>
              <option value="BankTransfer">{t('Bank transfer')}</option>
              <option value="Cheque">{t('Cheque')}</option>
              <option value="Cash">{t('Cash')}</option>
              <option value="Other">{t('Other')}</option>
            </select>
          </div>
          <div>
            <label htmlFor="settle-date" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Value date')}</label>
            <input id="settle-date" type="date" className="input w-full" value={form.paidOn} onChange={e => setForm(f => ({ ...f, paidOn: e.target.value }))} />
          </div>
        </div>
        <div>
          <label htmlFor="settle-ref" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Bank reference / cheque number')} *</label>
          <input id="settle-ref" className="input w-full" value={form.reference} onChange={e => setForm(f => ({ ...f, reference: e.target.value }))} placeholder={t('Example: TRF-8842193')} />
        </div>
        <div>
          <label htmlFor="settle-amount" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Amount paid')} *</label>
          <input id="settle-amount" type="number" step="0.01" min="0" className="input w-full" value={form.amount} onChange={e => setForm(f => ({ ...f, amount: e.target.value }))} />
        </div>
        {error && <p role="alert" className="text-xs text-rose-500 dark:text-rose-400">{error}</p>}
      </div>
    </Modal>
  );
}

type PickableEmployee = { id: number; fullName: string; employeeCode: string; status: string };

function InitiateModal({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const [types, setTypes] = useState<SeparationTypeInfo[]>(SEPARATION_TYPE_FALLBACK);
  const [employees, setEmployees] = useState<PickableEmployee[]>([]);
  const [empLoading, setEmpLoading] = useState(true);
  const [empError, setEmpError] = useState('');
  const [search, setSearch] = useState('');
  const [form, setForm] = useState({ employeeId: 0, separationType: 'Resignation', reason: '', noticeDate: new Date().toISOString().slice(0, 10), noticePeriodDays: 30, rehireEligible: true, raiseBackfill: true });
  // S2-F4 — the last working day is now ENTERED, not computed and locked. It seeds from
  // notice + notice period and the operator may override it: immediate termination with pay in lieu, a
  // negotiated early release and an LWD that avoids landing on a Friday were all unenterable before.
  const [lwd, setLwd] = useState('');
  const [lwdTouched, setLwdTouched] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    offboardingApi.separationTypes()
      .then(list => { if (list.length > 0) setTypes(list); })
      .catch(() => { /* keep the local mirror — the modal must still work */ });
  }, []);

  // Server-side search rather than a 300-row dump: for any client over 300 active staff most of the
  // workforce simply was not in the list.
  const searchRef = useRef(search);
  searchRef.current = search;
  const loadEmployees = useCallback((term: string) => {
    setEmpLoading(true); setEmpError('');
    employeesApi.list({ search: term || undefined, pageSize: 50 })
      .then(r => {
        if (searchRef.current !== term) return; // a newer keystroke owns the list
        setEmployees(r.items
          .filter(e => OFFBOARDABLE_STATUSES.has(e.status))
          .map(e => ({ id: e.id, fullName: e.fullName, employeeCode: e.employeeCode, status: e.status })));
      })
      .catch(() => { if (searchRef.current === term) setEmpError(t('Could not load employees. Retry, or refine the search.')); })
      .finally(() => { if (searchRef.current === term) setEmpLoading(false); });
  }, [t]);

  useEffect(() => {
    const timer = setTimeout(() => loadEmployees(search), search ? 300 : 0);
    return () => clearTimeout(timer);
  }, [search, loadEmployees]);

  const computedLwd = useMemo(
    () => (form.noticeDate ? addDays(form.noticeDate, Number(form.noticePeriodDays) || 0) : ''),
    [form.noticeDate, form.noticePeriodDays],
  );
  useEffect(() => { if (!lwdTouched) setLwd(computedLwd); }, [computedLwd, lwdTouched]);

  const selectedType = types.find(x => x.code === form.separationType) ?? types[0];
  const forfeits = selectedType?.forfeitsEndOfServiceAward === true;
  const weekend = weekendName(lwd);
  const servedDays = form.noticeDate && lwd ? calendarDaysBetween(form.noticeDate, lwd) ?? 0 : 0;
  const shortfall = Math.max(0, (Number(form.noticePeriodDays) || 0) - servedDays);

  const submit = async () => {
    if (!form.employeeId) { setError(t('Select an employee.')); return; }
    if (!lwd) { setError(t('Enter a last working day.')); return; }
    if (lwd < form.noticeDate) { setError(t('The last working day cannot be before the notice date.')); return; }
    if (selectedType?.requiresReason && !form.reason.trim()) {
      setError(t('{type} requires a recorded reason.', { type: selectedType.label })); return;
    }
    setSaving(true); setError('');
    try {
      await offboardingApi.initiate({ ...form, lastWorkingDay: lwd });
      onDone();
    } catch (e: unknown) {
      setError(apiMessage(e, t('Could not initiate offboarding.')));
      setSaving(false);
    }
  };

  return (
    <Modal
      isOpen
      title={t('Initiate Offboarding')}
      onClose={() => { if (!saving) onClose(); }}
      closeLabel={t('Close')}
      footer={<>
        <button type="button" className="btn-secondary text-sm" onClick={onClose} disabled={saving}>{t('Cancel')}</button>
        <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={submit} disabled={saving}><UserMinus className="h-3.5 w-3.5" aria-hidden="true" />{saving ? t('Initiating…') : t('Initiate')}</button>
      </>}
    >
      <div className="space-y-3">
        <div>
          <label htmlFor="off-employee" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Employee')} *</label>
          <div className="relative mb-1.5">
            <Search className="pointer-events-none absolute start-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" aria-hidden="true" />
            <input
              className="input w-full ps-8"
              placeholder={t('Search by name or code…')}
              value={search}
              onChange={e => setSearch(e.target.value)}
              aria-label={t('Search employees')}
            />
          </div>
          <select id="off-employee" className="select w-full" value={form.employeeId} onChange={e => setForm(f => ({ ...f, employeeId: Number(e.target.value) }))}>
            <option value={0}>{t('— Select —')}</option>
            {employees.map(e => (
              <option key={e.id} value={e.id}>
                {e.fullName} ({e.employeeCode}){e.status !== 'Active' ? ` · ${t(e.status)}` : ''}
              </option>
            ))}
          </select>
          {empLoading ? (
            <p className="mt-1 flex items-center gap-1.5 text-xs text-slate-500"><Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> {t('Loading employees…')}</p>
          ) : empError ? (
            <p role="alert" className="mt-1 text-xs text-rose-500 dark:text-rose-400">{empError}</p>
          ) : employees.length === 0 ? (
            <p className="mt-1 text-xs text-slate-500">
              {search ? t('No active or suspended employee matches that search.') : t('No employees available to offboard.')}
            </p>
          ) : (
            <p className="mt-1 text-xs text-slate-500">{t('Active and suspended staff. Search to narrow a large workforce.')}</p>
          )}
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="off-type" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Separation type')}</label>
            <select id="off-type" className="select w-full" value={form.separationType} onChange={e => setForm(f => ({ ...f, separationType: e.target.value }))}>
              {types.map(x => <option key={x.code} value={x.code}>{x.label}</option>)}
            </select>
          </div>
          <div>
            <label htmlFor="off-notice" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Notice date')}</label>
            <input id="off-notice" type="date" className="input w-full" value={form.noticeDate} onChange={e => setForm(f => ({ ...f, noticeDate: e.target.value }))} />
          </div>
        </div>
        {selectedType && (
          <p className={`text-xs ${forfeits ? 'text-rose-600 dark:text-rose-400' : 'text-slate-500 dark:text-slate-400'}`}>
            {selectedType.description}
          </p>
        )}
        {forfeits && (
          <div role="alert" className="flex items-start gap-2 rounded-lg border border-rose-300 bg-rose-50 p-2.5 text-xs text-rose-700 dark:border-rose-500/40 dark:bg-rose-500/10 dark:text-rose-300">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            <span>
              <strong>{t('The end-of-service award will be forfeited in full.')}</strong>{' '}
              {t('Record the specific Art. 80 (1)–(9) ground below. If this is not a summary dismissal for cause, choose another separation type — recording it as a plain termination pays the full Art. 84 award instead.')}
            </span>
          </div>
        )}
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="off-noticedays" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Contractual notice (days)')}</label>
            <input id="off-noticedays" type="number" min={0} className="input w-full" value={form.noticePeriodDays} onChange={e => setForm(f => ({ ...f, noticePeriodDays: Number(e.target.value) }))} />
          </div>
          <div>
            <label htmlFor="off-lwd" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Last working day')} *</label>
            <input id="off-lwd" type="date" className="input w-full" value={lwd} min={form.noticeDate || undefined} onChange={e => { setLwdTouched(true); setLwd(e.target.value); }} />
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
          {lwdTouched && lwd !== computedLwd && (
            <button type="button" className="text-sapphire hover:underline dark:text-cyanAccent" onClick={() => { setLwdTouched(false); setLwd(computedLwd); }}>
              {t('Reset to notice + {days} days ({date})', { days: Number(form.noticePeriodDays) || 0, date: computedLwd || '—' })}
            </button>
          )}
          {weekend && <span className="text-amber-700 dark:text-amber-400">{t('Last working day falls on a {day}.', { day: t(weekend) })}</span>}
          {shortfall > 0 && (
            <span className="text-amber-700 dark:text-amber-400">
              {t('{days} days of notice unserved — the settlement will raise pay in lieu.', { days: shortfall })}
            </span>
          )}
        </div>
        <div>
          <label htmlFor="off-reason" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">
            {t('Reason')}{selectedType?.requiresReason ? ' *' : ''}
          </label>
          <textarea
            id="off-reason"
            className="input w-full resize-none"
            rows={2}
            placeholder={forfeits ? t('Which Art. 80 ground is relied on, and the evidence…') : t('Reason for separation…')}
            value={form.reason}
            onChange={e => setForm(f => ({ ...f, reason: e.target.value }))}
          />
        </div>
        <div className="flex flex-wrap gap-5">
          <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={form.rehireEligible} onChange={e => setForm(f => ({ ...f, rehireEligible: e.target.checked }))} /> {t('Rehire eligible')}
          </label>
          <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={form.raiseBackfill} onChange={e => setForm(f => ({ ...f, raiseBackfill: e.target.checked }))} /> {t('Raise backfill requisition')}
          </label>
        </div>
        <div className="flex items-start gap-2 rounded-lg border border-amber-300/50 bg-amber-50 p-2.5 text-xs text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          {t('The employee stays in headcount (serving notice) until you Complete the offboarding after their last working day. The final settlement is calculated in Payroll; the offboarding card shows where it stands.')}
        </div>
        {error && <p role="alert" className="text-xs text-rose-500">{error}</p>}
      </div>
    </Modal>
  );
}
