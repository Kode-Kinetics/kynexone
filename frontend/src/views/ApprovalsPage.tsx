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

const PAGE_SIZE = 25;

type QueueFilter = 'mine' | 'team' | 'overdue' | '';
type ApprovalMetric = {
  key: QueueFilter;
  label: string;
  value: number;
  icon: typeof UserCheck;
  tone: string;
  hint: string;
};

const statusTone = (s: string): { label: string; tone: 'amber' | 'emerald' | 'rose' | 'slate' } => {
  switch (s) {
    case 'Pending': return { label: 'Pending', tone: 'amber' };
    case 'Approved': return { label: 'Approved', tone: 'emerald' };
    case 'Rejected': return { label: 'Rejected', tone: 'rose' };
    case 'Cancelled': return { label: 'Cancelled', tone: 'slate' };
    default: return { label: s, tone: 'slate' };
  }
};

const fmtDateTime = (s?: string | null) => s ? new Date(s).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) : '-';
const fmtFullDateTime = (s?: string | null) => s ? new Date(s).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '-';
const hoursUntil = (s?: string | null) => s ? Math.round((new Date(s).getTime() - Date.now()) / 360_000) / 10 : null;
const ageLabel = (hours: number) => hours < 1 ? '<1h old' : hours < 24 ? `${hours}h old` : `${Math.round(hours / 24)}d old`;
const ownerLabel = (r: ApprovalRequest) => r.currentApproverName || r.currentQueue || r.currentApproverRole || 'Unassigned';
const ownerMeta = (r: ApprovalRequest) => [r.currentApproverType, r.currentApproverRole].filter(Boolean).join(' · ') || 'No route metadata';

export function ApprovalsPage() {
  const [requests, setRequests] = useState<ApprovalRequest[]>([]);
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
  // Stale-approval path: the establishment guard re-checks at apply time; a slot
  // consumed since submission returns a structured 409 rendered as the popup.
  const [establishmentBlock, setEstablishmentBlock] = useState<{ block: EstablishmentBlockedPayload; employeeName?: string } | null>(null);

  const loadMetrics = useCallback(async () => {
    const [mine, team, overdue, allPending] = await Promise.all([
      approvalsApi.list({ status: 'Pending', queue: 'mine', page: 1, pageSize: 1 }),
      approvalsApi.list({ status: 'Pending', queue: 'team', page: 1, pageSize: 1 }),
      approvalsApi.list({ status: 'Pending', queue: 'overdue', page: 1, pageSize: 1 }),
      approvalsApi.list({ status: 'Pending', page: 1, pageSize: 1 }),
    ]);

    setMetrics([
      { key: 'mine', label: 'My Queue', value: mine.total, icon: UserCheck, tone: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-300', hint: 'Assigned directly or by role' },
      { key: 'team', label: 'Team Queue', value: team.total, icon: Users, tone: 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300', hint: 'Pending for direct reports' },
      { key: 'overdue', label: 'Overdue', value: overdue.total, icon: AlertTriangle, tone: overdue.total > 0 ? 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300', hint: 'Past SLA deadline' },
      { key: '', label: 'All Pending', value: allPending.total, icon: Inbox, tone: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-300', hint: 'Tenant-wide pending work' },
    ]);
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const res = await approvalsApi.list({ status: statusFilter || undefined, queue: queueFilter || undefined, page, pageSize: PAGE_SIZE });
      setRequests(res.items);
      setTotal(res.total);
    } catch {
      setError('Could not load approval requests from the API.');
      setRequests([]);
      setTotal(0);
    }
    finally { setLoading(false); }
  }, [statusFilter, queueFilter, page]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => { loadMetrics().catch(() => setMetrics([])); }, [loadMetrics]);
  useEffect(() => { setPage(1); }, [statusFilter, queueFilter]);

  const handleDecide = async (decision: 'Approve' | 'Reject') => {
    if (!selected) return;
    if (decision === 'Reject' && comments.trim().length < 5) {
      alert('Please add a clear rejection reason before rejecting.');
      return;
    }
    setDeciding(true);
    try {
      await approvalsApi.decide(selected.id, decision, comments);
      setSelected(null);
      setComments('');
      await Promise.all([load(), loadMetrics()]);
    } catch (err: unknown) {
      const block = establishmentBlockFromError(err);
      if (block) {
        // The approval stays pending — it can be re-approved after a budget raise.
        setEstablishmentBlock({ block, employeeName: selected.entityName || undefined });
        await Promise.all([load(), loadMetrics()]);
      } else {
        const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
        alert(msg ?? 'Failed to submit decision. Please try again.');
      }
    }
    finally { setDeciding(false); }
  };

  const totalPages = Math.max(1, Math.ceil(total / 25));

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Approval Center</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">Accountable queues by owner, team, SLA, and overdue risk</p>
        </div>
        <button type="button" onClick={() => { load(); loadMetrics().catch(() => setMetrics([])); }} className="btn-secondary inline-flex h-9 items-center gap-2 px-3 text-sm">
          <RefreshCw className="h-4 w-4" />
          Refresh
        </button>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {metrics.map((m) => {
          const Icon = m.icon;
          return (
            <button
              key={m.label}
              type="button"
              onClick={() => { setStatusFilter('Pending'); setQueueFilter(m.key); }}
              className={`rounded-lg border p-4 text-left transition ${queueFilter === m.key && statusFilter === 'Pending' ? 'border-sapphire bg-blue-50/70 dark:border-blue-400 dark:bg-blue-500/10' : 'border-slate-200 bg-white hover:border-slate-300 dark:border-white/10 dark:bg-white/[0.03] dark:hover:border-white/20'}`}
            >
              <div className="flex items-center justify-between gap-3">
                <span className={`inline-flex h-9 w-9 items-center justify-center rounded-md ${m.tone}`}><Icon className="h-4 w-4" /></span>
                <span className="text-2xl font-extrabold text-slate-950 dark:text-white">{m.value}</span>
              </div>
              <p className="mt-3 text-sm font-bold text-slate-800 dark:text-slate-100">{m.label}</p>
              <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{m.hint}</p>
            </button>
          );
        })}
      </div>

      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-white/[0.03]">
        {([
          ['mine', 'My Queue', UserCheck],
          ['team', 'Team Queue', Users],
          ['overdue', 'Overdue', AlertTriangle],
          ['', 'All Queues', ListChecks],
        ] as const).map(([key, label, Icon]) => (
          <button
            key={key || 'all-queues'}
            type="button"
            onClick={() => setQueueFilter(key)}
            className={`inline-flex h-8 items-center gap-1.5 rounded-md px-3 text-sm font-semibold transition ${queueFilter === key ? 'bg-slate-900 text-white dark:bg-white dark:text-slate-950' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}`}
          >
            <Icon className="h-3.5 w-3.5" />
            {label}
          </button>
        ))}
        <div className="mx-1 h-6 w-px bg-slate-200 dark:bg-white/10" />
        {(['Pending', 'Approved', 'Rejected', 'Cancelled', ''] as const).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => setStatusFilter(s)}
            className={`h-8 rounded-md px-3 text-sm font-semibold transition ${statusFilter === s ? 'bg-sapphire text-white' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}`}
          >
            {s || 'All'}
          </button>
        ))}
        <span className="ml-auto text-sm text-slate-400">{total} request{total !== 1 ? 's' : ''}</span>
      </div>

      {error && (
        <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm font-medium text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
          {error}
        </div>
      )}

      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[900px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Request', 'Current owner', 'SLA accountability', 'Workflow', 'Status', ''].map((h) => (
                  <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {loading && <tr><td colSpan={6} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>}
              {!loading && requests.length === 0 && (
                <tr>
                  <td colSpan={6} className="py-16 text-center">
                    <ShieldCheck className="mx-auto mb-3 h-10 w-10 text-slate-200 dark:text-slate-700" />
                    <p className="text-sm text-slate-400 dark:text-slate-500">No approvals found</p>
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
                        <p className="mt-1 text-xs text-slate-400">{r.entityName} · {r.priority || 'Normal'} · Created {fmtDateTime(r.createdAtUtc)}</p>
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{ownerLabel(r)}</p>
                    <p className="text-xs text-slate-400">{ownerMeta(r)}</p>
                    {r.escalatedToRole && <p className="mt-1 text-xs font-semibold text-rose-500">Escalated to {r.escalatedToRole}</p>}
                  </td>
                  <td className="px-4 py-3">
                    <span className={`inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs font-semibold ${r.isOverdue ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-300'}`}>
                      {r.isOverdue ? <AlertTriangle className="h-3 w-3" /> : <Clock3 className="h-3 w-3" />}
                      {r.isOverdue ? 'Overdue' : `${r.slaHours}h SLA`}
                    </span>
                    <p className="mt-1 text-xs text-slate-400">
                      {remainingHours !== null ? `${remainingHours < 0 ? `${Math.abs(remainingHours).toFixed(1)}h late` : `${remainingHours.toFixed(1)}h left`}` : 'No due time'}
                    </p>
                    <p className="text-xs text-slate-400">Due {fmtDateTime(r.dueAtUtc)}</p>
                  </td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">
                    <p className="font-semibold text-slate-700 dark:text-slate-200">Step {r.currentStepOrder}</p>
                    <p className="text-xs">{ageLabel(Number(r.ageHours ?? 0))}</p>
                    {r.lastRoutedAtUtc && <p className="text-xs">Routed {fmtDateTime(r.lastRoutedAtUtc)}</p>}
                  </td>
                  <td className="px-4 py-3"><StatusChip {...statusTone(r.status)} /></td>
                  <td className="px-4 py-3">
                    {r.status === 'Pending' && r.canDecide && (
                      <button type="button" onClick={() => { setSelected(r); setComments(''); }}
                        className="btn-secondary h-8 px-3 text-xs">
                        Review
                      </button>
                    )}
                    {r.status === 'Pending' && !r.canDecide && (
                      <span className="text-xs font-medium text-slate-400">Watching</span>
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
            <p className="text-xs text-slate-400">Page {page} of {totalPages}</p>
            <div className="flex gap-1">
              <button type="button" disabled={page === 1} onClick={() => setPage((p) => p - 1)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Prev</button>
              <button type="button" disabled={page === totalPages} onClick={() => setPage((p) => p + 1)} className="btn-secondary h-7 px-2 text-xs disabled:opacity-40">Next</button>
            </div>
          </div>
        )}
      </div>

      {/* Review Modal */}
      <Modal isOpen={!!selected} title="Review Approval" onClose={() => setSelected(null)}
        footer={
          <>
            <button type="button" onClick={() => setSelected(null)} className="btn-secondary">Cancel</button>
            {selected?.canDecide && (
              <>
                <button type="button" onClick={() => handleDecide('Reject')} disabled={deciding} className="btn-secondary text-rose-500 hover:border-rose-300 disabled:opacity-60">Reject</button>
                <button type="button" onClick={() => handleDecide('Approve')} disabled={deciding} className="btn-primary disabled:opacity-60">{deciding ? 'Saving...' : 'Approve'}</button>
              </>
            )}
          </>
        }>
        {selected && (
          <div className="space-y-4">
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.03]">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <p className="text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">Request</p>
                  <p className="mt-1 text-sm font-semibold text-slate-900 dark:text-white">{selected.title}</p>
                  <p className="text-xs text-slate-400 dark:text-slate-500">{selected.entityName} · {selected.entityId}</p>
                </div>
                <StatusChip {...statusTone(selected.status)} />
              </div>
              <div className="mt-4 grid gap-3 sm:grid-cols-3">
                <div>
                  <p className="text-xs font-bold uppercase tracking-wide text-slate-400">Owner</p>
                  <p className="mt-1 text-sm font-semibold text-slate-800 dark:text-slate-100">{ownerLabel(selected)}</p>
                  <p className="text-xs text-slate-400">{ownerMeta(selected)}</p>
                </div>
                <div>
                  <p className="text-xs font-bold uppercase tracking-wide text-slate-400">SLA</p>
                  <p className={`mt-1 text-sm font-semibold ${selected.isOverdue ? 'text-rose-600 dark:text-rose-300' : 'text-slate-800 dark:text-slate-100'}`}>{selected.isOverdue ? 'Overdue' : `${selected.slaHours}h window`}</p>
                  <p className="text-xs text-slate-400">Due {fmtFullDateTime(selected.dueAtUtc)}</p>
                </div>
                <div>
                  <p className="text-xs font-bold uppercase tracking-wide text-slate-400">Workflow</p>
                  <p className="mt-1 text-sm font-semibold text-slate-800 dark:text-slate-100">Step {selected.currentStepOrder}</p>
                  <p className="text-xs text-slate-400">{ageLabel(Number(selected.ageHours ?? 0))}</p>
                </div>
              </div>
            </div>
            {selected.decisions.length > 0 && (
              <div>
                <p className="mb-1 text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">Decision History</p>
                <div className="space-y-1">
                  {selected.decisions.map((d) => (
                    <div key={d.id} className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                      <span className={`font-semibold ${d.decision === 'Approved' ? 'text-emerald-600 dark:text-emerald-300' : 'text-rose-500'}`}>{d.decision}</span>
                      <span>Step {d.stepOrder}</span>
                      {d.comments && <span>— {d.comments}</span>}
                    </div>
                  ))}
                </div>
              </div>
            )}
            {selected.decisions.length === 0 && (
              <div className="flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-medium text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
                <TimerReset className="h-4 w-4" />
                Waiting for first decision from the routed owner.
              </div>
            )}
            {selected.canDecide ? (
              <div>
                <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Comments (optional)</label>
                <textarea value={comments} onChange={(e) => setComments(e.target.value)} className="input w-full resize-none" rows={3} placeholder="Add a comment..." />
                <p className="mt-1 text-xs text-slate-400">A rejection requires a clear reason for auditability.</p>
              </div>
            ) : (
              <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-xs font-medium text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400">
                This item is visible for accountability, but the current workflow step is assigned to another owner.
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
