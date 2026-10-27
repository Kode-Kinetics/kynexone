'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Clock3, Inbox, ListChecks, RefreshCw, ShieldCheck, TimerReset, UserCheck, Users } from 'lucide-react';
import { approvalsApi } from '../api/approvals';
import type { ApprovalRequest } from '../api/approvals';
import { establishmentBlockFromError } from '../api/establishment';
import type { EstablishmentBlockedPayload } from '../api/establishment';
import { EstablishmentBlockedModal } from '../components/EstablishmentBlockedModal';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';
import { StatutoryLeaveHistory } from '../components/StatutoryLeaveHistory';
import { approvalDetailRenderers } from '../components/approvals/approvalDetailRenderers';
import { SlateApprovalLink } from '../components/succession/SlateApprovalLink';
import { leaveRequestsApi } from '../api/leave';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { EnumLabel, enumLabel } from '../components/EnumLabel';
import { msg } from '../i18n/translations';
import type { StatutoryLeaveContext } from '../api/leave';

const PAGE_SIZE = 25;

type QueueFilter = 'mine' | 'team' | 'overdue' | '';
type Translate = ReturnType<typeof useLocale>['t'];
type ApprovalMetric = {
  key: QueueFilter;
  value: number;
  icon: typeof UserCheck;
  tone: string;
};

/** The tile's title and one-line hint, translated where it renders (the metric itself carries only its key). */
const metricCopy = (key: QueueFilter, t: Translate): { label: string; hint: string } => {
  switch (key) {
    case 'mine': return { label: t('My Queue'), hint: t('Assigned directly or by role') };
    case 'team': return { label: t('Team Queue'), hint: t('Pending for direct reports') };
    case 'overdue': return { label: t('Overdue'), hint: t('Past SLA deadline') };
    default: return { label: t('All Pending'), hint: t('Tenant-wide pending work') };
  }
};

const statusTone = (s: string, t: Translate): { label: string; tone: 'amber' | 'emerald' | 'rose' | 'slate' } => {
  const label = enumLabel(t, 'ApprovalStatus', s);
  switch (s) {
    case 'Pending': return { label, tone: 'amber' };
    case 'Approved': return { label, tone: 'emerald' };
    case 'Rejected': return { label, tone: 'rose' };
    case 'Cancelled': return { label, tone: 'slate' };
    default: return { label, tone: 'slate' };
  }
};

const hoursUntil = (s?: string | null) => s ? Math.round((new Date(s).getTime() - Date.now()) / 360_000) / 10 : null;
const ageLabel = (hours: number, t: Translate) => hours < 1 ? t('<1h old') : hours < 24 ? t('{hours}h old', { hours }) : t('{days}d old', { days: Math.round(hours / 24) });
const ownerLabel = (r: ApprovalRequest, t: Translate) => r.currentApproverName || r.currentQueue || r.currentApproverRole || t('Unassigned');
const ownerMeta = (r: ApprovalRequest, t: Translate) => [r.currentApproverType, r.currentApproverRole].filter(Boolean).join(' · ') || t('No route metadata');

/** "Step 2 of 3" when the server says how many steps will run, else the bare step number. */
const stepLabel = (r: ApprovalRequest, t: Translate) =>
  r.stepPosition && r.stepCount && r.stepCount > 1 ? t('Step {step} of {count}', { step: r.stepPosition, count: r.stepCount }) : `${r.currentStepOrder}`;

/** True when approving this request only completes an intermediate step (the request stays pending). */
const approvalAdvancesToNextStep = (r: ApprovalRequest) =>
  r.stepPosition != null && r.stepCount != null && r.stepPosition < r.stepCount;

export function ApprovalsPage() {
  const { t } = useLocale();
  const fx = useFormat();
  const fmtDateTime = (s?: string | null) => s ? fx.dateTime(s, 'dayMonth') : '-';
  const fmtFullDateTime = (s?: string | null) => s ? fx.dateTime(s, 'medium') : '-';
  const [requests, setRequests] = useState<ApprovalRequest[]>([]);
  // Optimistic "Step N of M approved" labels for multi-step requests whose intermediate step was just approved:
  // such a request is still Pending (it moved to its next step), so labelling it "Approved" was false.
  const [stepProgress, setStepProgress] = useState<Record<string, string>>({});
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState('Pending');
  const [queueFilter, setQueueFilter] = useState<QueueFilter>('mine');
  const [metrics, setMetrics] = useState<ApprovalMetric[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<ApprovalRequest | null>(null);
  const [comments, setComments] = useState('');
  const [deciding, setDeciding] = useState(false);
  const [withdrawing, setWithdrawing] = useState(false);
  const [decisionError, setDecisionError] = useState('');
  // Stale-approval path: the establishment guard re-checks at apply time; a slot
  // consumed since submission returns a structured 409 rendered as the popup.
  const [establishmentBlock, setEstablishmentBlock] = useState<{ block: EstablishmentBlockedPayload; employeeName?: string } | null>(null);
  // A Saudi statutory leave (maternity, Hajj, bereavement…) is decided with the employee's earlier
  // leave of the same kind in view.
  const [statutoryContext, setStatutoryContext] = useState<StatutoryLeaveContext | null>(null);
  useEffect(() => {
    setStatutoryContext(null);
    if (!selected || selected.entityName !== 'LeaveRequest') return;
    let live = true;
    leaveRequestsApi.statutoryHistory([selected.entityId])
      .then(h => { if (live) setStatutoryContext(h[selected.entityId] ?? null); })
      .catch(() => { if (live) setStatutoryContext(null); });
    return () => { live = false; };
  }, [selected]);

  const loadMetrics = useCallback(async () => {
    const [mine, team, overdue, allPending] = await Promise.all([
      approvalsApi.list({ status: 'Pending', queue: 'mine', page: 1, pageSize: 1 }),
      approvalsApi.list({ status: 'Pending', queue: 'team', page: 1, pageSize: 1 }),
      approvalsApi.list({ status: 'Pending', queue: 'overdue', page: 1, pageSize: 1 }),
      approvalsApi.list({ status: 'Pending', page: 1, pageSize: 1 }),
    ]);

    setMetrics([
      { key: 'mine', value: mine.total, icon: UserCheck, tone: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-300' },
      { key: 'team', value: team.total, icon: Users, tone: 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300' },
      { key: 'overdue', value: overdue.total, icon: AlertTriangle, tone: overdue.total > 0 ? 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300' },
      { key: '', value: allPending.total, icon: Inbox, tone: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-300' },
    ]);
  }, []);

  /**
   * Fetches the queue. `silent` reconciles in the background WITHOUT raising the spinner: the
   * table body swaps its rows for a spinner whenever `loading` is true, so a non-silent reload
   * after an optimistic update would flash the very rows the update just settled. A silent
   * reload also leaves existing rows in place if it fails, rather than blanking the table on a
   * transient error — the optimistic state stays until an explicit reload contradicts it.
   */
  const load = useCallback(async ({ silent = false }: { silent?: boolean } = {}) => {
    if (!silent) {
      setLoading(true);
      setError('');
    }
    try {
      const res = await approvalsApi.list({ status: statusFilter || undefined, queue: queueFilter || undefined, page, pageSize: PAGE_SIZE });
      setRequests(res.items);
      setTotal(res.total);
      if (!silent) setStepProgress({});
    } catch {
      if (!silent) {
        setError(t('Could not load approval requests from the API.'));
        setRequests([]);
        setTotal(0);
      }
    }
    finally { if (!silent) setLoading(false); }
  }, [statusFilter, queueFilter, page, t]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { loadMetrics().catch(() => setMetrics([])); }, [loadMetrics]);
  useEffect(() => { setPage(1); }, [statusFilter, queueFilter]);

  /**
   * Applies a decision optimistically.
   *
   * The server round-trip used to gate the whole UI: decide -> reload list -> reload four metric
   * queries, so a row only left the queue after three sequential network waves. The decided row is
   * now removed and the counters decremented immediately, and the authoritative reload runs in the
   * background purely to reconcile. Any failure restores the exact pre-decision snapshot.
   */
  const handleDecide = async (decision: 'Approve' | 'Reject') => {
    if (!selected) return;
    if (decision === 'Reject' && comments.trim().length < 5) {
      setDecisionError(t('Add a clear rejection reason of at least 5 characters before rejecting.'));
      return;
    }
    setDecisionError('');

    // Snapshot everything the optimistic update touches so a failure can rewind exactly.
    const decided = selected;
    const prevRequests = requests;
    const prevTotal = total;
    const prevMetrics = metrics;
    const prevComments = comments;

    // Approving an intermediate step of a multi-step request completes THAT step only: the request stays
    // Pending at its next step, so it is labelled "Step N of M approved", never "Approved".
    const advances = decision === 'Approve' && approvalAdvancesToNextStep(decided);
    // The decided row only leaves the visible list while the list is showing pending work; on a
    // "Approved"/"Rejected"/all-status view the row legitimately stays, with its new status. A request that
    // only advanced is still pending, so it stays in the all-queues pending view too.
    const leavesCurrentList = statusFilter === 'Pending' && !(advances && queueFilter === '');
    const decidedStatus = advances ? 'Pending' : decision === 'Approve' ? 'Approved' : 'Rejected';
    const prevStepProgress = stepProgress;
    if (advances) {
      setStepProgress(p => ({ ...p, [decided.id]: t('Step {step} of {count} approved', { step: decided.stepPosition ?? decided.currentStepOrder, count: decided.stepCount ?? 0 }) }));
    }

    setDeciding(true);
    setSelected(null);
    setComments('');
    setRequests(rs => leavesCurrentList
      ? rs.filter(r => r.id !== decided.id)
      : rs.map(r => (r.id === decided.id ? { ...r, status: decidedStatus } : r)));
    if (leavesCurrentList) setTotal(t => Math.max(0, t - 1));
    // Every counter is a count of PENDING work, so each one this request belonged to drops by one.
    // "All Pending" always contained it; the queue-specific tiles only if it sat in that queue.
    // A request that only advanced is still pending, so "All Pending" keeps it.
    setMetrics(ms => ms.map(m => (
      (m.key === '' && !advances) || m.key === decided.currentQueue
        ? { ...m, value: Math.max(0, m.value - 1) }
        : m
    )));

    try {
      await approvalsApi.decide(decided.id, decision, comments);
      // Reconcile against the server; the user is already looking at the post-decision state.
      await Promise.all([load({ silent: true }), loadMetrics()]);
    } catch (err: unknown) {
      // Roll back to the pre-decision snapshot before surfacing anything.
      setRequests(prevRequests);
      setTotal(prevTotal);
      setMetrics(prevMetrics);
      setStepProgress(prevStepProgress);

      const block = establishmentBlockFromError(err);
      if (block) {
        // The approval stays pending — it can be re-approved after a budget raise.
        setEstablishmentBlock({ block, employeeName: decided.entityName || undefined });
        await Promise.all([load(), loadMetrics()]);
      } else {
        // Reopen the decision so the reviewer keeps their typed justification and can retry.
        setSelected(decided);
        setComments(prevComments);
        const apiMsg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
        setDecisionError(apiMsg ?? t('Failed to submit decision. Please try again.'));
      }
    }
    finally { setDeciding(false); }
  };

  // The requester takes back their own pending employee change. Not optimistic: it is rare, and the
  // server is the one that knows whether an approver decided it a moment earlier.
  const handleWithdraw = async () => {
    if (!selected) return;
    setWithdrawing(true);
    setDecisionError('');
    try {
      await approvalsApi.withdraw(selected.id, comments.trim());
      setSelected(null);
      setComments('');
      await Promise.all([load({ silent: true }), loadMetrics()]);
    } catch (err: unknown) {
      const apiMsg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setDecisionError(apiMsg ?? t('Could not withdraw this request. Please try again.'));
    } finally {
      setWithdrawing(false);
    }
  };

  const totalPages = Math.max(1, Math.ceil(total / 25));

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Approval Center')}</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{t('Accountable queues by owner, team, SLA, and overdue risk')}</p>
        </div>
        <button type="button" onClick={() => { load(); loadMetrics().catch(() => setMetrics([])); }} className="btn-secondary inline-flex h-9 items-center gap-2 px-3 text-sm">
          <RefreshCw className="h-4 w-4" />
          {t('Refresh')}
        </button>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {metrics.map((m) => {
          const Icon = m.icon;
          const copy = metricCopy(m.key, t);
          return (
            <button
              key={m.key || 'all-pending'}
              type="button"
              onClick={() => { setStatusFilter('Pending'); setQueueFilter(m.key); }}
              aria-pressed={queueFilter === m.key && statusFilter === 'Pending'}
              aria-label={t('Show {queue}: {count} pending', { queue: copy.label, count: m.value })}
              className={`rounded-lg border p-4 text-start transition ${queueFilter === m.key && statusFilter === 'Pending' ? 'border-sapphire bg-blue-50/70 dark:border-blue-400 dark:bg-blue-500/10' : 'border-slate-200 bg-white hover:border-slate-300 dark:border-white/10 dark:bg-white/[0.03] dark:hover:border-white/20'}`}
            >
              <div className="flex items-center justify-between gap-3">
                <span className={`inline-flex h-9 w-9 items-center justify-center rounded-lg ${m.tone}`}><Icon className="h-4 w-4" /></span>
                <span className="text-2xl font-semibold text-slate-950 dark:text-white">{m.value}</span>
              </div>
              <p className="mt-3 text-sm font-semibold text-slate-800 dark:text-slate-100">{copy.label}</p>
              <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{copy.hint}</p>
            </button>
          );
        })}
      </div>

      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-white/[0.03]" role="group" aria-label={t('Approval filters')}>
        {([
          ['mine', msg('My Queue'), UserCheck],
          ['team', msg('Team Queue'), Users],
          ['overdue', msg('Overdue'), AlertTriangle],
          ['', msg('All Queues'), ListChecks],
        ] as const).map(([key, label, Icon]) => (
          <button
            key={key || 'all-queues'}
            type="button"
            onClick={() => setQueueFilter(key)}
            aria-pressed={queueFilter === key}
            aria-label={t('Filter by {filter}', { filter: t(label) })}
            className={`inline-flex h-8 items-center gap-1.5 rounded-lg px-3 text-sm font-semibold transition ${queueFilter === key ? 'bg-slate-900 text-white dark:bg-white dark:text-slate-950' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}`}
          >
            <Icon className="h-3.5 w-3.5" />
            {t(label)}
          </button>
        ))}
        <div className="mx-1 h-6 w-px bg-slate-200 dark:bg-white/10" />
        {(['Pending', 'Approved', 'Rejected', 'Cancelled', ''] as const).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => setStatusFilter(s)}
            aria-pressed={statusFilter === s}
            aria-label={t('Filter by status {status}', { status: s ? enumLabel(t, 'ApprovalStatus', s) : t('All') })}
            className={`h-8 rounded-lg px-3 text-sm font-semibold transition ${statusFilter === s ? 'bg-sapphire text-white' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}`}
          >
            {s ? <EnumLabel enum="ApprovalStatus" value={s} /> : t('All')}
          </button>
        ))}
        <span className="ms-auto text-sm text-slate-400">{t('{count, plural, one {# request} other {# requests}}', { count: total })}</span>
      </div>

      {error && (
        <div role="alert" aria-live="assertive" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm font-medium text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
          {error}
        </div>
      )}

      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[900px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Request'), msg('Current owner'), msg('SLA accountability'), msg('Workflow'), msg('Status'), ''].map((h, i) => (
                  <th key={h || `col-${i}`} className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h ? t(h) : <span className="sr-only">{t('Actions')}</span>}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {loading && <tr><td colSpan={6} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>}
              {/* A failed load shows the error above, never "No approvals found": an empty queue it is not. */}
              {!loading && !error && requests.length === 0 && (
                <tr>
                  <td colSpan={6} className="py-16 text-center">
                    <ShieldCheck className="mx-auto mb-3 h-10 w-10 text-slate-200 dark:text-slate-700" />
                    <p className="text-sm text-slate-400 dark:text-slate-500">{t('No approvals found')}</p>
                  </td>
                </tr>
              )}
              {!loading && requests.map((r) => {
                const remainingHours = hoursUntil(r.dueAtUtc);
                return (
                <tr key={r.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3">
                    <div className="flex items-start gap-3">
                      <span className={`mt-0.5 h-2.5 w-2.5 rounded-full ${r.isOverdue ? 'bg-rose-500' : r.status === 'Pending' ? 'bg-amber-400' : 'bg-slate-300'}`} />
                      <div>
                        <p className="font-medium text-slate-900 dark:text-white">{r.title}</p>
                        {r.changeSummary && <p className="mt-0.5 text-xs font-medium text-slate-600 dark:text-slate-300">{t('Changes: {summary}', { summary: r.changeSummary })}</p>}
                        <p className="mt-1 text-xs text-slate-400">{t('{entity} · {priority} · Created {date}', { entity: r.entityName, priority: enumLabel(t, 'Status', r.priority || 'Normal'), date: fmtDateTime(r.createdAtUtc) })}</p>
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{ownerLabel(r, t)}</p>
                    <p className="text-xs text-slate-400">{ownerMeta(r, t)}</p>
                    {r.escalatedToRole && <p className="mt-1 text-xs font-semibold text-rose-500">{t('Escalated to {role}', { role: r.escalatedToRole })}</p>}
                  </td>
                  <td className="px-4 py-3">
                    <span className={`inline-flex items-center gap-1 rounded-lg px-2 py-0.5 text-xs font-semibold ${r.isOverdue ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-300'}`}>
                      {r.isOverdue ? <AlertTriangle className="h-3 w-3" /> : <Clock3 className="h-3 w-3" />}
                      {r.isOverdue ? t('Overdue') : t('{hours}h SLA', { hours: r.slaHours })}
                    </span>
                    <p className="mt-1 text-xs text-slate-400">
                      {remainingHours !== null ? (remainingHours < 0 ? t('{hours}h late', { hours: fx.number(Math.abs(remainingHours), { minimumFractionDigits: 1, maximumFractionDigits: 1 }) }) : t('{hours}h left', { hours: fx.number(remainingHours, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })) : t('No due time')}
                    </p>
                    <p className="text-xs text-slate-400">{t('Due {date}', { date: fmtDateTime(r.dueAtUtc) })}</p>
                  </td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">
                    <p className="font-semibold text-slate-700 dark:text-slate-200">{r.stepCount && r.stepCount > 1 ? stepLabel(r, t) : t('Step {step}', { step: r.currentStepOrder })}</p>
                    <p className="text-xs">{ageLabel(Number(r.ageHours ?? 0), t)}</p>
                    {r.lastRoutedAtUtc && <p className="text-xs">{t('Routed {date}', { date: fmtDateTime(r.lastRoutedAtUtc) })}</p>}
                  </td>
                  <td className="px-4 py-3">{stepProgress[r.id] ? <StatusChip label={stepProgress[r.id]} tone="emerald" /> : <StatusChip {...statusTone(r.status, t)} />}</td>
                  <td className="px-4 py-3">
                    <SlateApprovalLink request={r} />
                    {r.status === 'Pending' && r.canDecide && (
                      <button type="button" onClick={() => { setSelected(r); setComments(''); setDecisionError(''); }}
                        className="btn-secondary h-8 px-3 text-xs">
                        {t('Review')}
                      </button>
                    )}
                    {r.status === 'Pending' && !r.canDecide && (
                      <div className="flex flex-col items-start gap-1">
                        <button type="button" onClick={() => { setSelected(r); setComments(''); setDecisionError(''); }}
                          title={r.decisionBlockedReason ?? undefined}
                          className="btn-secondary h-8 px-3 text-xs">
                          {t('Details')}
                        </button>
                        <span className="text-xs font-medium text-slate-400">{r.canWithdraw ? t('Your request') : t('Another approver')}</span>
                      </div>
                    )}
                  </td>
                </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {totalPages > 1 && (
          <div className="flex items-center justify-between border-t border-slate-100 px-4 py-3 dark:border-white/[0.07]">
            <p className="text-xs text-slate-400">{t('Page {page} of {totalPages}', { page, totalPages })}</p>
            <div className="flex gap-1">
              <button type="button" disabled={page === 1} onClick={() => setPage((p) => p - 1)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">{t('Prev')}</button>
              <button type="button" disabled={page === totalPages} onClick={() => setPage((p) => p + 1)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">{t('Next')}</button>
            </div>
          </div>
        )}
      </div>

      {/* Review Modal */}
      <Modal isOpen={!!selected} title={t('Review Approval')} onClose={() => { setSelected(null); setDecisionError(''); }}
        footer={
          <>
            <button type="button" onClick={() => { setSelected(null); setDecisionError(''); }} className="btn-secondary">{t('Close')}</button>
            {selected?.canWithdraw && (
              <button type="button" onClick={handleWithdraw} disabled={withdrawing} className="btn-secondary text-rose-500 hover:border-rose-300 disabled:opacity-60">
                {withdrawing ? t('Withdrawing...') : t('Withdraw request')}
              </button>
            )}
            {selected?.canDecide && (
              <>
                <button type="button" onClick={() => handleDecide('Reject')} disabled={deciding} className="btn-secondary text-rose-500 hover:border-rose-300 disabled:opacity-60">{t('Reject')}</button>
                <button type="button" onClick={() => handleDecide('Approve')} disabled={deciding} className="btn-primary disabled:opacity-60">{deciding ? t('Saving...') : t('Approve')}</button>
              </>
            )}
          </>
        }>
        {selected && (
          <div className="space-y-4">
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.03]">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{t('Request')}</p>
                  <p className="mt-1 text-sm font-semibold text-slate-900 dark:text-white">{selected.title}</p>
                  {selected.changeSummary && <p className="mt-0.5 text-sm text-slate-700 dark:text-slate-200">{t('Changes: {summary}', { summary: selected.changeSummary })}</p>}
                  <p className="text-xs text-slate-400 dark:text-slate-500">{selected.entityName} · {selected.entityId}</p>
                </div>
                <StatusChip {...statusTone(selected.status, t)} />
              </div>
              <div className="mt-4 grid gap-3 sm:grid-cols-3">
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Owner')}</p>
                  <p className="mt-1 text-sm font-semibold text-slate-800 dark:text-slate-100">{ownerLabel(selected, t)}</p>
                  <p className="text-xs text-slate-400">{ownerMeta(selected, t)}</p>
                </div>
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('SLA')}</p>
                  <p className={`mt-1 text-sm font-semibold ${selected.isOverdue ? 'text-rose-600 dark:text-rose-300' : 'text-slate-800 dark:text-slate-100'}`}>{selected.isOverdue ? t('Overdue') : t('{hours}h window', { hours: selected.slaHours })}</p>
                  <p className="text-xs text-slate-400">{t('Due {date}', { date: fmtFullDateTime(selected.dueAtUtc) })}</p>
                </div>
                <div>
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Workflow')}</p>
                  <p className="mt-1 text-sm font-semibold text-slate-800 dark:text-slate-100">{selected.stepCount && selected.stepCount > 1 ? stepLabel(selected, t) : t('Step {step}', { step: selected.currentStepOrder })}</p>
                  <p className="text-xs text-slate-400">{ageLabel(Number(selected.ageHours ?? 0), t)}</p>
                </div>
              </div>
            </div>
            <StatutoryLeaveHistory context={statutoryContext} />
            {(() => {
              // Entity-specific detail (e.g. a renewal offer's before → after), registered per entityName.
              const Detail = approvalDetailRenderers[selected.entityName];
              return Detail ? <Detail request={selected} /> : null;
            })()}
            {selected.decisions.length > 0 && (
              <div>
                <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{t('Decision History')}</p>
                <div className="space-y-1">
                  {selected.decisions.map((d) => (
                    <div key={d.id} className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                      <span className={`font-semibold ${d.decision === 'Approved' ? 'text-emerald-600 dark:text-emerald-300' : 'text-rose-500'}`}><EnumLabel enum="ApprovalStatus" value={d.decision} /></span>
                      <span>{t('Step {step}', { step: d.stepOrder })}</span>
                      {d.comments && <span>— {d.comments}</span>}
                    </div>
                  ))}
                </div>
              </div>
            )}
            {selected.decisions.length === 0 && (
              <div className="flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-medium text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
                <TimerReset className="h-4 w-4" />
                {t('Waiting for first decision from the routed owner.')}
              </div>
            )}
            {selected.canDecide ? (
              <div>
                <label htmlFor="approval-comments" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Comments (required for rejection)')}</label>
                <textarea id="approval-comments" value={comments} onChange={(e) => { setComments(e.target.value); if (decisionError) setDecisionError(''); }} className="input w-full resize-none" rows={3} placeholder={t('Add a comment...')} aria-describedby="approval-comments-help approval-decision-error" />
                <p id="approval-comments-help" className="mt-1 text-xs text-slate-400">{t('A rejection requires a clear reason for auditability.')}</p>
                {decisionError && <p id="approval-decision-error" role="alert" className="mt-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">{decisionError}</p>}
              </div>
            ) : (
              <div className="space-y-2">
                <div role="note" className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
                  {selected.decisionBlockedReason ?? t('This item is visible for accountability, but the current workflow step is assigned to another owner.')}
                </div>
                {selected.canWithdraw && (
                  <div>
                    <label htmlFor="approval-withdraw-reason" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Reason for withdrawing (optional)')}</label>
                    <textarea id="approval-withdraw-reason" value={comments} onChange={(e) => { setComments(e.target.value); if (decisionError) setDecisionError(''); }} className="input w-full resize-none" rows={2} placeholder={t('Example: submitted twice by mistake')} />
                  </div>
                )}
                {decisionError && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">{decisionError}</p>}
              </div>
            )}
          </div>
        )}
      </Modal>

      {/* Blocked-assignment popup: apply-time establishment guard 409 (stale approval). */}
      <EstablishmentBlockedModal
        block={establishmentBlock?.block ?? null}
        employeeName={establishmentBlock?.employeeName}
        onClose={() => setEstablishmentBlock(null)}
      />
    </div>
  );
}
