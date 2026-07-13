'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Clock3, ShieldCheck, UserCheck } from 'lucide-react';
import { approvalsApi } from '../api/approvals';
import type { ApprovalRequest } from '../api/approvals';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';

const statusTone = (s: string): { label: string; tone: 'amber' | 'emerald' | 'rose' | 'slate' } => {
  switch (s) {
    case 'Pending': return { label: 'Pending', tone: 'amber' };
    case 'Approved': return { label: 'Approved', tone: 'emerald' };
    case 'Rejected': return { label: 'Rejected', tone: 'rose' };
    default: return { label: s, tone: 'slate' };
  }
};

const fmtDateTime = (s?: string | null) => s ? new Date(s).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) : '-';

export function ApprovalsPage() {
  const [requests, setRequests] = useState<ApprovalRequest[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState('Pending');
  const [queueFilter, setQueueFilter] = useState<'mine' | 'team' | 'overdue' | ''>('mine');
  const [loading, setLoading] = useState(true);
  const [selected, setSelected] = useState<ApprovalRequest | null>(null);
  const [comments, setComments] = useState('');
  const [deciding, setDeciding] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await approvalsApi.list({ status: statusFilter || undefined, queue: queueFilter || undefined, page, pageSize: 25 });
      setRequests(res.items);
      setTotal(res.total);
    } catch { /**/ }
    finally { setLoading(false); }
  }, [statusFilter, queueFilter, page]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => { setPage(1); }, [statusFilter, queueFilter]);

  const handleDecide = async (decision: 'Approve' | 'Reject') => {
    if (!selected) return;
    setDeciding(true);
    try {
      await approvalsApi.decide(selected.id, decision, comments);
      setSelected(null);
      setComments('');
      load();
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      alert(msg ?? 'Failed to submit decision. Please try again.');
    }
    finally { setDeciding(false); }
  };

  const totalPages = Math.max(1, Math.ceil(total / 25));

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Approval Center</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">Accountable queues by owner, team, SLA, and overdue risk</p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {([
          ['mine', 'My Queue'],
          ['team', 'Team Queue'],
          ['overdue', 'Overdue'],
          ['', 'All'],
        ] as const).map(([key, label]) => (
          <button
            key={key || 'all-queues'}
            type="button"
            onClick={() => setQueueFilter(key)}
            className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-semibold transition ${queueFilter === key ? 'bg-slate-900 text-white dark:bg-white dark:text-slate-950' : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/10 dark:text-slate-300 dark:hover:bg-white/20'}`}
          >
            {key === 'mine' && <UserCheck className="h-3.5 w-3.5" />}
            {key === 'overdue' && <AlertTriangle className="h-3.5 w-3.5" />}
            {label}
          </button>
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {(['Pending', 'Approved', 'Rejected', ''] as const).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => setStatusFilter(s)}
            className={`rounded-full px-3 py-1 text-sm font-semibold transition ${statusFilter === s ? 'bg-sapphire text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/10 dark:text-slate-300 dark:hover:bg-white/20'}`}
          >
            {s || 'All'}
          </button>
        ))}
        <span className="ml-auto text-sm text-slate-400">{total} request{total !== 1 ? 's' : ''}</span>
      </div>

      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[640px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Title', 'Owner', 'SLA', 'Step', 'Status', ''].map((h) => (
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
              {!loading && requests.map((r) => (
                <tr key={r.id} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900 dark:text-white">{r.title}</p>
                    <p className="mt-1 text-xs text-slate-400">{r.entityName} · {r.priority}</p>
                  </td>
                  <td className="px-4 py-3">
                    <p className="text-sm font-medium text-slate-700 dark:text-slate-200">{r.currentApproverName || r.currentQueue || '-'}</p>
                    <p className="text-xs text-slate-400">{r.currentApproverRole || r.currentApproverType || '-'}</p>
                  </td>
                  <td className="px-4 py-3">
                    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-semibold ${r.isOverdue ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-300'}`}>
                      {r.isOverdue ? <AlertTriangle className="h-3 w-3" /> : <Clock3 className="h-3 w-3" />}
                      {r.isOverdue ? 'Overdue' : `${r.slaHours}h SLA`}
                    </span>
                    <p className="mt-1 text-xs text-slate-400">Due {fmtDateTime(r.dueAtUtc)}</p>
                  </td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">Step {r.currentStepOrder}<br /><span className="text-xs">{r.ageHours}h old</span></td>
                  <td className="px-4 py-3"><StatusChip {...statusTone(r.status)} /></td>
                  <td className="px-4 py-3">
                    {r.status === 'Pending' && (
                      <button type="button" onClick={() => { setSelected(r); setComments(''); }}
                        className="btn-secondary h-7 px-2 text-xs opacity-0 group-hover:opacity-100">
                        Review
                      </button>
                    )}
                  </td>
                </tr>
              ))}
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
            <button type="button" onClick={() => handleDecide('Reject')} disabled={deciding} className="btn-secondary text-rose-500 hover:border-rose-300 disabled:opacity-60">Reject</button>
            <button type="button" onClick={() => handleDecide('Approve')} disabled={deciding} className="btn-primary disabled:opacity-60">{deciding ? 'Saving…' : 'Approve'}</button>
          </>
        }>
        {selected && (
          <div className="space-y-4">
            <div>
              <p className="text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">Request</p>
              <p className="mt-1 text-sm font-medium text-slate-900 dark:text-white">{selected.title}</p>
              <p className="text-xs text-slate-400 dark:text-slate-500">{selected.entityName} · {selected.entityId}</p>
              <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
                Owner: <strong>{selected.currentApproverName || selected.currentQueue || '-'}</strong> · Due {fmtDateTime(selected.dueAtUtc)}
              </p>
            </div>
            {selected.decisions.length > 0 && (
              <div>
                <p className="mb-1 text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">Decision History</p>
                <div className="space-y-1">
                  {selected.decisions.map((d) => (
                    <div key={d.id} className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                      <span className={`font-semibold ${d.decision === 'Approved' ? 'text-emeraldZ' : 'text-rose-500'}`}>{d.decision}</span>
                      <span>Step {d.stepOrder}</span>
                      {d.comments && <span>— {d.comments}</span>}
                    </div>
                  ))}
                </div>
              </div>
            )}
            <div>
              <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Comments (optional)</label>
              <textarea value={comments} onChange={(e) => setComments(e.target.value)} className="input w-full resize-none" rows={3} placeholder="Add a comment…" />
            </div>
          </div>
        )}
      </Modal>
    </div>
  );
}
