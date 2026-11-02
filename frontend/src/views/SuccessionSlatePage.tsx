'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { approvalsApi, type ApprovalRequest } from '../api/approvals';
import { successionApi, type SlateVersion } from '../api/succession';
import { StatusChip } from '../components/StatusChip';
import { HashBadge, SnapshotView } from '../components/succession/SlatePanel';
import { ErrorBlock, FormError, ForbiddenBlock, LoadingRows, RawCode, SuccessionGate } from '../components/succession/SuccessionParts';
import { useAppToast } from '../components/ui/AppToast';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { longEnough, slateStatusLabel, slateTone, successionError, successionStatus } from '../lib/succession';

/**
 * Review one slate version (SUC-06): the frozen snapshot with every 9-box input, and — while it is Submitted and routed
 * to you — Approve or Return for revision (a reason is required). The decision goes through the shared approvals API,
 * so the Approval Center and this page are one decision, recorded once.
 */
export function SuccessionSlatePage({ id }: { id: string }) {
  return (
    <SuccessionGate>
      <SlateBody id={id} />
    </SuccessionGate>
  );
}

function SlateBody({ id }: { id: string }) {
  const { t } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [slate, setSlate] = useState<SlateVersion | null>(null);
  const [approval, setApproval] = useState<ApprovalRequest | null>(null);
  const [error, setError] = useState<{ message: string; status?: number } | null>(null);
  const [returning, setReturning] = useState(false);
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [decisionError, setDecisionError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const s = await successionApi.slate(id);
      setSlate(s);
      // The approval tells us whether THIS person may decide (and why not). If it cannot be read, the server still decides.
      if (s.status === 'Submitted' && s.approvalRequestId) {
        approvalsApi.get(s.approvalRequestId).then(setApproval).catch(() => setApproval(null));
      } else {
        setApproval(null);
      }
    } catch (e) {
      setError({ message: successionError(e, t), status: successionStatus(e) });
    }
  }, [id, t]);
  useEffect(() => { void load(); }, [load]);

  const decide = async (decision: 'Approve' | 'Reject') => {
    if (!slate?.approvalRequestId) return;
    if (decision === 'Reject') {
      setTouched(true);
      if (!longEnough(reason, 5)) return;
    }
    setBusy(true);
    setDecisionError(null);
    try {
      await successionApi.decideSlate(slate.approvalRequestId, decision, decision === 'Reject' ? reason.trim() : '');
      toast.success(decision === 'Approve' ? t('suc.review.approved') : t('suc.review.returned'));
      setReturning(false);
      await load();
    } catch (e) {
      setDecisionError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  if (error?.status === 403) return <ForbiddenBlock />;
  if (error) return <ErrorBlock message={error.message} onRetry={() => void load()} />;
  if (!slate) return <LoadingRows label={t('suc.loading')} />;
  const pending = slate.status === 'Submitted' && !!slate.approvalRequestId;
  const blocked = approval && approval.status === 'Pending' && !approval.canDecide;
  const canAct = pending && !blocked;
  const reasonProblem = touched && !longEnough(reason, 5) ? t('suc.check.minN', { count: 5 }) : null;

  return (
    <div className="space-y-4" data-testid="suc-slate-review" data-slate-id={slate.id} data-status={slate.status}>
      <nav aria-label={t('suc.breadcrumb')} className="text-xs">
        <Link href={`/performance/succession/positions/${slate.criticalPositionId}`} className="text-sapphire hover:underline dark:text-cyanAccent">{t('suc.review.backToPosition')}</Link>
      </nav>
      <header className="wg-card space-y-2 rounded-2xl p-4">
        <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">
          {t('suc.review.title', { position: slate.snapshot?.positionTitle ?? '—', version: slate.versionNo })}
        </h1>
        <p className="flex flex-wrap items-center gap-2">
          <StatusChip label={t(slateStatusLabel[slate.status])} tone={slateTone(slate.status)} />
          <RawCode testId="suc-slate-status" value={slate.status} />
          <HashBadge slate={slate} />
        </p>
        <p className="text-xs text-slate-500">
          {[
            slate.submittedAtUtc ? t('suc.slate.submittedOn', { date: fx.dateTime(slate.submittedAtUtc) }) : null,
            slate.approvedAtUtc ? t('suc.slate.approvedOn', { date: fx.dateTime(slate.approvedAtUtc) }) : null,
          ].filter(Boolean).join(' · ')}
        </p>
        {slate.returnReason && <p className="text-xs text-rose-700 dark:text-rose-300">{t('suc.slate.returnedBecause', { reason: slate.returnReason })}</p>}
      </header>

      {pending && (
        <section className="wg-card space-y-3 rounded-2xl p-4" aria-label={t('suc.review.decision')} data-testid="suc-slate-decision">
          <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('suc.review.decision')}</h2>
          {blocked
            ? <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="suc-slate-blocked">{approval?.decisionBlockedReason ?? t('suc.review.notYours')}</p>
            : <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.review.decisionIntro')}</p>}
          {canAct && !returning && (
            <div className="flex flex-wrap gap-2">
              <button type="button" className="btn-primary text-xs" data-testid="suc-slate-approve" disabled={busy} onClick={() => void decide('Approve')}>{t('suc.review.approve')}</button>
              <button type="button" className="btn-secondary text-xs" data-testid="suc-slate-return" disabled={busy} onClick={() => setReturning(true)}>{t('suc.review.return')}</button>
            </div>
          )}
          {canAct && returning && (
            <div className="space-y-2">
              <label htmlFor="suc-slate-return-reason" className="block text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.review.returnReason')}</label>
              <textarea id="suc-slate-return-reason" data-testid="suc-slate-return-reason" className="input min-h-[80px] w-full" value={reason}
                aria-describedby={reasonProblem ? 'suc-slate-return-reason-error' : undefined} onChange={(e) => setReason(e.target.value)} />
              {reasonProblem && <p id="suc-slate-return-reason-error" role="alert" className="text-caption font-medium text-rose-600">{reasonProblem}</p>}
              <div className="flex flex-wrap gap-2">
                <button type="button" className="btn-secondary text-xs" onClick={() => setReturning(false)}>{t('suc.cancel')}</button>
                <button type="button" className="btn-primary text-xs" data-testid="suc-slate-return-confirm" disabled={busy} onClick={() => void decide('Reject')}>{t('suc.review.returnConfirm')}</button>
              </div>
            </div>
          )}
          <FormError message={decisionError} />
        </section>
      )}

      <section className="wg-card space-y-3 rounded-2xl p-4" aria-label={t('suc.review.snapshot')}>
        <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('suc.review.snapshot')}</h2>
        {slate.anonymised && <p className="text-xs text-slate-500">{t('suc.review.anonymised')}</p>}
        {slate.snapshot ? <SnapshotView snapshot={slate.snapshot} /> : <p className="text-xs text-slate-500">{t('suc.snapshot.noCandidates')}</p>}
      </section>
    </div>
  );
}
