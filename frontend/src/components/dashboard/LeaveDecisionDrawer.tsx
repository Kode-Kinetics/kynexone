'use client';

/**
 * Decide a leave request without leaving the dashboard. It is the approvals queue's own decision
 * (same endpoint, same server-side rules), not a second engine: the drawer loads the request,
 * shows who and when, the employee's earlier statutory leave, the routing, and takes the decision.
 *
 * Decisions are irreversible, so nothing is shown as done until the server confirms it, and there
 * is no Undo. A rejection needs a written reason (the same rule as the queue). If the server
 * refuses for a reason this drawer cannot explain (establishment guard, a step that moved on),
 * the request is left untouched and the way into the full queue is offered.
 */

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { ArrowRight } from 'lucide-react';
import { approvalsApi, type ApprovalRequest } from '../../api/approvals';
import { establishmentBlockFromError } from '../../api/establishment';
import { leaveRequestsApi, type StatutoryLeaveContext } from '../../api/leave';
import type { ApprovalQueueItem } from '../../api/dashboard';
import { Modal } from '../Modal';
import { StatutoryLeaveHistory } from '../StatutoryLeaveHistory';
import { useT } from '../../hooks/useT';
import { parseApprovalSubject } from './dashboardModel';
import { approvalTitleLabel } from '../../lib/approvalDisplay';

export type DecisionOutcome = 'approved' | 'advanced' | 'rejected';

export function LeaveDecisionDrawer({ item, onClose, onDecided }: {
  /** The queue row being decided; null keeps the drawer closed. */
  item: ApprovalQueueItem | null;
  onClose: () => void;
  /** Called only after the server has confirmed the decision. */
  onDecided: (outcome: DecisionOutcome, item: ApprovalQueueItem) => void;
}) {
  const t = useT();
  const id = item?.id ?? null;
  const [req, setReq] = useState<ApprovalRequest | null>(null);
  const [state, setState] = useState<'idle' | 'loading' | 'failed'>('idle');
  const [statutory, setStatutory] = useState<StatutoryLeaveContext | null>(null);
  const [comments, setComments] = useState('');
  const [saving, setSaving] = useState<'Approve' | 'Reject' | null>(null);
  const [problem, setProblem] = useState('');
  const [needsQueue, setNeedsQueue] = useState(false);

  const load = useCallback(async (rid: string) => {
    setState('loading');
    try {
      const r = await approvalsApi.get(rid);
      setReq(r);
      setState('idle');
      if (r.entityName === 'LeaveRequest') {
        leaveRequestsApi.statutoryHistory([r.entityId]).then((h) => setStatutory(h[r.entityId] ?? null)).catch(() => setStatutory(null));
      }
    } catch {
      setState('failed');
    }
  }, []);

  useEffect(() => {
    setReq(null); setStatutory(null); setComments(''); setProblem(''); setNeedsQueue(false); setSaving(null);
    if (id) void load(id);
  }, [id, load]);

  const decide = async (decision: 'Approve' | 'Reject') => {
    if (!req || !item || saving) return;
    if (decision === 'Reject' && comments.trim().length < 5) {
      setProblem(t('Add a clear rejection reason of at least 5 characters before rejecting.'));
      return;
    }
    setProblem(''); setNeedsQueue(false); setSaving(decision);
    try {
      await approvalsApi.decide(req.id, decision, comments.trim());
      const advances = decision === 'Approve' && req.stepPosition != null && req.stepCount != null && req.stepPosition < req.stepCount;
      onDecided(decision === 'Reject' ? 'rejected' : advances ? 'advanced' : 'approved', item);
    } catch (err) {
      // Nothing was saved. Say so, keep what the reviewer typed, and offer the full queue when the
      // refusal is one only the queue explains (the establishment guard shows its own dialog there).
      setNeedsQueue(!!establishmentBlockFromError(err));
      setProblem(t('The decision could not be saved. Nothing was changed.'));
      setSaving(null);
    }
  };

  const open = item != null;
  const name = item ? (item.employeeName ?? parseApprovalSubject(item.title).name ?? '') : '';
  const last = req && req.stepPosition != null && req.stepCount != null ? req.stepPosition >= req.stepCount : true;

  return (
    <Modal isOpen={open} onClose={onClose} title={t('Review leave request')} placement="end" size="md" closeDisabled={saving != null}
      footer={req?.canDecide ? (
        <>
          <button type="button" onClick={() => void decide('Reject')} disabled={saving != null} className="btn-secondary text-rose-600 hover:border-rose-300 disabled:opacity-60">
            {saving === 'Reject' ? t('Saving...') : t('Reject')}
          </button>
          <button type="button" onClick={() => void decide('Approve')} disabled={saving != null} className="btn-primary disabled:opacity-60">
            {saving === 'Approve' ? t('Saving...') : t('Approve')}
          </button>
        </>
      ) : undefined}>
      {item && (
        <div className="space-y-4">
          <div>
            <p dir="auto" className="text-h4 font-semibold text-slate-900 dark:text-white"><bdi>{name || approvalTitleLabel(t, item.title)}</bdi></p>
            <p className="mt-0.5 text-body-sm text-slate-700 dark:text-slate-300">
              {item.detail && <bdi>{item.detail}</bdi>}
              {item.department && <> · <bdi>{item.department}</bdi></>}
            </p>
          </div>

          {state === 'loading' && <div className="h-24 rounded-lg bg-slate-100 dark:bg-white/[0.05]" aria-hidden />}
          {state === 'failed' && (
            <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-body-sm text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
              {t('This request could not be loaded.')}{' '}
              <button type="button" onClick={() => id && void load(id)} className="font-semibold underline">{t('Retry')}</button>
            </div>
          )}

          {req && (
            <>
              <dl className="grid grid-cols-2 gap-3 rounded-lg border border-[color:var(--wg-line)] p-3 text-body-sm">
                <div>
                  <dt className="text-meta text-slate-600 dark:text-slate-400">{t('SLA')}</dt>
                  <dd className={`font-semibold ${req.isOverdue ? 'text-rose-700 dark:text-rose-300' : 'text-slate-900 dark:text-white'}`}>
                    {req.isOverdue ? t('Overdue') : t('{hours}h window', { hours: req.slaHours })}
                  </dd>
                </div>
                <div>
                  <dt className="text-meta text-slate-600 dark:text-slate-400">{t('Workflow')}</dt>
                  <dd className="font-semibold text-slate-900 dark:text-white">
                    {req.stepCount && req.stepCount > 1
                      ? t('Step {step} of {count}', { step: req.stepPosition ?? req.currentStepOrder, count: req.stepCount })
                      : t('Step {step}', { step: req.currentStepOrder })}
                  </dd>
                </div>
              </dl>

              <StatutoryLeaveHistory context={statutory} />

              {req.decisions.length > 0 ? (
                <div>
                  <p className="mb-1 text-meta font-semibold text-slate-700 dark:text-slate-300">{t('Decision History')}</p>
                  <ul className="space-y-1 text-body-sm text-slate-700 dark:text-slate-300">
                    {req.decisions.map((d) => <li key={d.id}>{t('Step {step}', { step: d.stepOrder })}: <bdi>{d.decision}</bdi>{d.comments ? <> – <bdi>{d.comments}</bdi></> : null}</li>)}
                  </ul>
                </div>
              ) : (
                <p className="text-body-sm text-slate-700 dark:text-slate-300">{t('Waiting for first decision from the routed owner.')}</p>
              )}

              {req.canDecide ? (
                <div>
                  <label htmlFor="drawer-comments" className="mb-1.5 block text-body-sm font-medium text-slate-800 dark:text-slate-200">{t('Comments (required for rejection)')}</label>
                  <textarea id="drawer-comments" value={comments} rows={3} className="input w-full resize-none" placeholder={t('Add a comment...')}
                    onChange={(e) => { setComments(e.target.value); if (problem) setProblem(''); }} aria-describedby="drawer-problem" />
                  <p className="mt-1 text-meta text-slate-600 dark:text-slate-400">
                    {last ? t('This decision is final and cannot be undone.') : t('This decides your step; the request then moves on. It cannot be undone.')}
                  </p>
                </div>
              ) : (
                <p role="note" className="rounded-lg border border-[color:var(--wg-line)] px-3 py-2 text-body-sm text-slate-700 dark:text-slate-300">
                  {req.decisionBlockedReason ?? t('This item is visible for accountability, but the current workflow step is assigned to another owner.')}
                </p>
              )}
            </>
          )}

          <div id="drawer-problem" aria-live="polite">
            {problem && (
              <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-body-sm text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">{problem}</p>
            )}
          </div>

          <Link href="/approvals" className="inline-flex items-center gap-1 text-body-sm font-semibold text-sapphire hover:underline dark:text-blue-300">
            {needsQueue ? t('Open in approvals to resolve') : t('Open in approvals')} <ArrowRight className="h-3.5 w-3.5 rtl:-scale-x-100" aria-hidden />
          </Link>
        </div>
      )}
    </Modal>
  );
}
