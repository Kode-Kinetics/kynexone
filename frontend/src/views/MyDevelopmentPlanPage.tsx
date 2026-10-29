'use client';

import { useCallback, useEffect, useState } from 'react';
import { successionApi, type MyDevelopment, type PlanAction } from '../api/succession';
import { StatusChip } from '../components/StatusChip';
import { EmptyBlock, ErrorBlock, ForbiddenBlock, LoadingRows, PersonName, ProgressBar, RawCode, SuccessionGate, useSuccessionAccess } from '../components/succession/SuccessionParts';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import {
  actionStatusLabel, actionTone, actionTypeLabel, completionMethodLabel, isLinkedAction, onTrackReason, planStatusLabel, planTone, progressParts,
  readinessLabel, successionError, successionStatus,
} from '../lib/succession';
import { useAppToast } from '../components/ui/AppToast';

/**
 * My development plan (SUC-04, SUC-07): the employee's own current plan — its actions, progress and next review — and
 * the plans before it. The employee writes progress notes and moves their own unlinked actions; learning and goal actions
 * follow their link. Talent-pool memberships appear only when the organisation switched that on; a slate, a 9-box cell
 * or another candidate never does.
 */
export function MyDevelopmentPlanPage() {
  return (
    <SuccessionGate>
      <MyPlanBody />
    </SuccessionGate>
  );
}

function MyPlanBody() {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const { self } = useSuccessionAccess();
  const [data, setData] = useState<MyDevelopment | null>(null);
  const [error, setError] = useState<{ message: string; status?: number } | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setData(await successionApi.myPlan()); } catch (e) { setError({ message: successionError(e, t), status: successionStatus(e) }); }
  }, [t]);
  useEffect(() => { if (self) void load(); }, [load, self]);

  if (!self || error?.status === 403) return <ForbiddenBlock message={t('suc.ess.forbidden')} />;
  if (error) return <ErrorBlock message={error.message} onRetry={() => void load()} />;
  if (!data) return <LoadingRows label={t('suc.loading')} />;
  const plan = data.current;

  return (
    <div className="space-y-4" data-testid="suc-my-plan">
      <header>
        <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{t('suc.ess.title')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.ess.subtitle')}</p>
      </header>

      {!plan && (
        <div data-testid="suc-my-empty"><EmptyBlock title={t('suc.ess.emptyTitle')} body={t('suc.ess.emptyBody')} /></div>
      )}

      {plan && (
        <section className="wg-card space-y-3 rounded-2xl p-4" aria-label={t('suc.ess.current')}>
          <div className="flex flex-wrap items-center gap-2">
            <StatusChip label={t(planStatusLabel[plan.status])} tone={planTone(plan.status, plan.progress.onTrack)} />
            <RawCode testId="suc-my-plan-status" value={plan.status} />
            <span className="text-xs text-slate-600 dark:text-slate-300">{t(onTrackReason(plan.progress))}</span>
          </div>
          <p className="text-sm text-slate-800 dark:text-slate-100">{plan.purpose}</p>
          <p className="text-xs text-slate-500">
            {t('suc.plan.nextReview', { date: fx.date(plan.nextReviewOn) })}
            {plan.owner && <> · {t('suc.plan.ownedBy')} <PersonName person={plan.owner} /></>}
          </p>
          <div className="space-y-1">
            <p className="text-xs text-slate-600 dark:text-slate-300">{t(progressParts(plan.progress).key, progressParts(plan.progress).params)}</p>
            <ProgressBar percent={plan.progress.percentComplete ?? 0} label={t('suc.plan.progressLabel')} />
          </div>
          {plan.actions.length === 0
            ? <p className="text-xs text-slate-500">{t('suc.action.none')}</p>
            : (
              <ul className="space-y-2">
                {[...plan.actions].sort((a, b) => a.sortOrder - b.sortOrder).map((a) => (
                  <MyActionRow key={a.id} action={a} editable={plan.status === 'Active' || plan.status === 'OnHold'} onSaved={setData} />
                ))}
              </ul>
            )}
        </section>
      )}

      {data.candidateVisibility && (
        <section className="wg-card space-y-2 rounded-2xl p-4" data-testid="suc-my-memberships" aria-label={t('suc.ess.memberships')}>
          <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('suc.ess.memberships')}</h2>
          <p className="text-xs text-slate-500">{t('suc.ess.membershipsHint')}</p>
          {data.memberships.length === 0
            ? <p className="text-xs text-slate-500">{t('suc.ess.noMemberships')}</p>
            : (
              <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {data.memberships.map((m, i) => (
                  <li key={`${m.kind}-${i}`} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
                    <span>{(locale === 'ar' && m.titleAr) || m.title}</span>
                    <span className="flex gap-1">
                      <StatusChip label={m.kind === 'pool' ? t('suc.ess.kindPool') : t('suc.ess.kindPosition')} tone="slate" />
                      <StatusChip label={m.stage === 'Proposed' ? t('suc.ess.stageProposed') : t('suc.ess.stageConsidered')} tone="blue" />
                      {m.readiness && <StatusChip label={t(readinessLabel[m.readiness])} tone="emerald" />}
                    </span>
                  </li>
                ))}
              </ul>
            )}
        </section>
      )}

      {data.history.length > 0 && (
        <details className="rounded-xl border border-slate-200 p-3 dark:border-white/10">
          <summary className="cursor-pointer text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.plan.history', { count: data.history.length })}</summary>
          <ul className="mt-2 space-y-2">
            {data.history.map((h) => (
              <li key={h.id} className="text-xs text-slate-600 dark:text-slate-300">
                <StatusChip label={t(planStatusLabel[h.status])} tone={planTone(h.status, h.progress.onTrack)} />
                <span className="ms-2">{h.purpose}</span>
              </li>
            ))}
          </ul>
        </details>
      )}
    </div>
  );
}

function MyActionRow({ action, editable, onSaved }: { action: PlanAction; editable: boolean; onSaved: (d: MyDevelopment) => void }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [note, setNote] = useState(action.progressNote ?? '');
  const [busy, setBusy] = useState(false);
  const linked = isLinkedAction(action.actionType);
  const closed = action.status === 'Completed' || action.status === 'Cancelled';

  const save = async (input: { status?: PlanAction['status']; progressNote?: string; proposeCompletion?: boolean }) => {
    setBusy(true);
    try {
      onSaved(await successionApi.updateMyAction(action.id, { ...input, version: action.version }));
      toast.success(t('suc.ess.saved'));
    } catch (e) {
      toast.error(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="space-y-2 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-my-action-row" data-action-id={action.id}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{action.title}</p>
          <p className="text-xs text-slate-500">
            {t(actionTypeLabel[action.actionType])}
            {action.learningPathName && <> · {(locale === 'ar' && action.learningPathNameAr) || action.learningPathName}</>}
            {action.goalTitle && <> · {action.goalTitle}</>}
            {action.dueOn && <> · {t('suc.action.due', { date: fx.date(action.dueOn) })}</>}
          </p>
        </div>
        <span className="flex items-center gap-1">
          <StatusChip label={t(actionStatusLabel[action.status])} tone={actionTone(action.status, action.isOverdue)} />
          <RawCode testId="suc-my-action-status" value={action.status} />
          {action.isOverdue && !closed && <StatusChip label={t('suc.action.overdue')} tone="rose" />}
        </span>
      </div>
      <p className="text-caption text-slate-500">
        {action.completionMethod
          ? t(completionMethodLabel[action.completionMethod])
          : linked ? (action.actionType === 'Learning' ? t('suc.method.willLearning') : t('suc.method.willGoal')) : t('suc.method.willManual')}
      </p>
      {action.completionProposedAtUtc && !closed && (
        <p className="text-xs font-semibold text-amber-700 dark:text-amber-300" data-testid="suc-my-action-proposed">{t('suc.ess.proposed')}</p>
      )}
      {editable && !closed && !linked && (
        <div className="space-y-2">
          <label htmlFor={`suc-note-${action.id}`} className="block text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.ess.note')}</label>
          <textarea id={`suc-note-${action.id}`} data-testid="suc-my-action-note" className="input min-h-[56px] w-full" value={note} onChange={(e) => setNote(e.target.value)} />
          <div className="flex flex-wrap gap-2">
            <button type="button" className="btn-secondary text-xs" disabled={busy || note === (action.progressNote ?? '')} onClick={() => void save({ progressNote: note })}>{t('suc.ess.saveNote')}</button>
            {!linked && action.status === 'NotStarted' && (
              <button type="button" className="btn-secondary text-xs" data-testid="suc-my-action-start" disabled={busy} onClick={() => void save({ status: 'InProgress', progressNote: note || undefined })}>{t('suc.action.start')}</button>
            )}
            {!linked && !action.completionProposedAtUtc && (
              <button type="button" className="btn-primary text-xs" data-testid="suc-my-action-complete" disabled={busy} onClick={() => void save({ proposeCompletion: true, progressNote: note || undefined })}>{t('suc.ess.proposeDone')}</button>
            )}
          </div>
        </div>
      )}
      {(!editable || closed) && action.progressNote && <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.action.note', { note: action.progressNote })}</p>}
    </li>
  );
}
