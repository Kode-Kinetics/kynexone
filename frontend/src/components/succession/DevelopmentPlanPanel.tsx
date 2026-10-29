'use client';

import { useCallback, useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import {
  successionApi, type ActionType, type DevelopmentPlan, type GoalOption, type LearningPathOption, type PersonRef, type PlanAction,
} from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  ACTION_TYPES, actionStatusLabel, actionTone, actionTypeLabel, addMonthsIso, completionMethodLabel, isLinkedAction, longEnough, onTrackReason,
  planStatusLabel, planTone, progressParts, successionError, todayIso,
} from '../../lib/succession';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import {
  EmptyBlock, ErrorBlock, Field, FormError, LoadingRows, PersonName, PersonPicker, ProgressBar, RawCode, ReasonDialog, type SuccessionAccess,
} from './SuccessionParts';

/**
 * One employee's development plan (SUC-04): the current plan with its actions, progress and on-track reason, and the
 * plans before it. Learning actions assign a learning path (the action completes itself when the learning does); Goal
 * actions follow one of the employee's goals. The nomination a plan supports is shown only to slate readers.
 */
export function DevelopmentPlanPanel({ employeeId, access }: { employeeId: number; access: SuccessionAccess }) {
  const { t } = useLocale();
  const [plans, setPlans] = useState<DevelopmentPlan[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try { setPlans(await successionApi.plans({ employeeId, status: 'all' })); } catch (e) { setError(successionError(e, t)); }
  }, [employeeId, t]);
  useEffect(() => { void load(); }, [load]);

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!plans) return <LoadingRows label={t('suc.loading')} rows={2} />;

  const sorted = [...plans].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
  const current = sorted.find((p) => p.status === 'Active' || p.status === 'OnHold') ?? sorted.find((p) => p.status === 'Draft') ?? null;
  const history = sorted.filter((p) => p !== current);
  const replace = (next: DevelopmentPlan) => setPlans((list) => (list ?? []).map((p) => (p.id === next.id ? next : p)));

  return (
    <div className="space-y-4" data-testid="suc-dev-panel">
      {current
        ? <PlanCard plan={current} access={access} onChange={replace} onReload={() => void load()} />
        : creating
          ? <CreatePlanForm employeeId={employeeId} onCancel={() => setCreating(false)} onDone={(p) => { setCreating(false); setPlans((l) => [p, ...(l ?? [])]); }} />
          : (
            <EmptyBlock title={t('suc.plan.noneTitle')} body={access.devplansManage ? t('suc.plan.noneBodyManage') : t('suc.plan.noneBody')}
              action={access.devplansManage ? (
                <button type="button" className="btn-primary gap-1 text-xs" data-testid="suc-plan-create" onClick={() => setCreating(true)}>
                  <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.plan.create')}
                </button>
              ) : undefined} />
          )}
      {current && access.devplansManage && !creating && current.status !== 'Draft' && (
        <p className="text-caption text-slate-500">{t('suc.plan.oneAtATime')}</p>
      )}
      {history.length > 0 && (
        <details className="rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-plan-history">
          <summary className="cursor-pointer text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.plan.history', { count: history.length })}</summary>
          <div className="mt-3 space-y-3">
            {history.map((p) => <PlanCard key={p.id} plan={p} access={access} onChange={replace} onReload={() => void load()} readOnly />)}
          </div>
        </details>
      )}
    </div>
  );
}

function CreatePlanForm({ employeeId, onCancel, onDone }: { employeeId: number; onCancel: () => void; onDone: (p: DevelopmentPlan) => void }) {
  const { t } = useLocale();
  const [purpose, setPurpose] = useState('');
  const [review, setReview] = useState(addMonthsIso(3));
  const [owner, setOwner] = useState<PersonRef | null>(null);
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problems = {
    purpose: longEnough(purpose, 10) ? null : t('suc.check.min10'),
    review: review && review > todayIso() ? null : t('suc.plan.reviewFuture'),
  };
  const submit = async () => {
    setTouched(true);
    if (problems.purpose || problems.review) return;
    setBusy(true);
    setError(null);
    try {
      onDone(await successionApi.createPlan({ employeeId, purpose: purpose.trim(), nextReviewOn: review, ownerEmployeeId: owner?.employeeId ?? null }));
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="wg-card space-y-3 rounded-2xl p-4" data-testid="suc-plan-form">
      <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('suc.plan.createTitle')}</h3>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.plan.createIntro')}</p>
      <Field id="suc-plan-purpose" label={t('suc.plan.purpose')} hint={t('suc.plan.purposeHint')} error={touched ? problems.purpose : null}>
        <textarea id="suc-plan-purpose" data-testid="suc-plan-purpose" className="input min-h-[72px] w-full" value={purpose} onChange={(e) => setPurpose(e.target.value)} />
      </Field>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field id="suc-plan-review-date" label={t('suc.plan.nextReviewLabel')} error={touched ? problems.review : null}>
          <input id="suc-plan-review-date" data-testid="suc-plan-review-date" type="date" className="input w-full" value={review} min={todayIso()} onChange={(e) => setReview(e.target.value)} />
        </Field>
        <PersonPicker id="suc-plan-owner" testId="suc-plan-owner" value={owner} onChange={setOwner} label={t('suc.plan.owner')} hint={t('suc.plan.ownerHint')} optional />
      </div>
      <FormError message={error} />
      <div className="flex flex-wrap justify-end gap-2">
        <button type="button" className="btn-secondary text-xs" onClick={onCancel}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-plan-submit" disabled={busy} onClick={() => void submit()}>{busy ? t('suc.saving') : t('suc.plan.createSubmit')}</button>
      </div>
    </div>
  );
}

type Dialog = 'hold' | 'cancel' | 'review' | null;

function PlanCard({ plan, access, onChange, onReload, readOnly }: {
  plan: DevelopmentPlan; access: SuccessionAccess; onChange: (p: DevelopmentPlan) => void; onReload: () => void; readOnly?: boolean;
}) {
  const { t } = useLocale();
  const fx = useFormat();
  const toast = useAppToast();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState(false);
  const manage = access.devplansManage && !readOnly;
  const open = plan.status === 'Draft' || plan.status === 'Active' || plan.status === 'OnHold';
  const progress = progressParts(plan.progress);

  const run = async (fn: () => Promise<DevelopmentPlan>) => {
    setBusy(true);
    try { onChange(await fn()); } catch (e) { toast.error(successionError(e, t)); onReload(); } finally { setBusy(false); }
  };

  return (
    <article className="wg-card space-y-3 rounded-2xl p-4" data-testid={readOnly ? 'suc-plan-past' : 'suc-plan'} data-plan-id={plan.id}>
      <header className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="flex flex-wrap items-center gap-2">
            <StatusChip label={t(planStatusLabel[plan.status])} tone={planTone(plan.status, plan.progress.onTrack)} />
            <RawCode testId={readOnly ? 'suc-plan-past-status' : 'suc-plan-status'} value={plan.status} />
            {(plan.status === 'Active' || plan.status === 'OnHold') && (
              <span className={`text-xs font-semibold ${plan.progress.onTrack ? 'text-emerald-700 dark:text-emerald-300' : 'text-rose-700 dark:text-rose-300'}`} data-testid="suc-plan-track">
                {plan.progress.onTrack ? t('suc.track.onTrack') : t('suc.track.offTrack')} — {t(onTrackReason(plan.progress))}
              </span>
            )}
          </p>
          <p className="mt-2 text-sm text-slate-800 dark:text-slate-100">{plan.purpose}</p>
          <p className="mt-1 text-xs text-slate-500">
            {t('suc.plan.nextReview', { date: fx.date(plan.nextReviewOn) })}
            {plan.owner && <> · {t('suc.plan.ownedBy')} <PersonName person={plan.owner} /></>}
          </p>
          {plan.lastReviewedAtUtc && (
            <p className="text-xs text-slate-500">{t('suc.plan.lastReview', { date: fx.date(plan.lastReviewedAtUtc), note: plan.lastReviewNote ?? '' })}</p>
          )}
          {plan.cancelReason && <p className="text-xs text-slate-500">{t('suc.plan.cancelledBecause', { reason: plan.cancelReason })}</p>}
          {access.slatesRead && plan.nominationId && <p className="text-xs text-slate-500" data-testid="suc-plan-nomination">{t('suc.plan.supportsNomination')}</p>}
        </div>
        {manage && open && (
          <div className="flex flex-wrap gap-2">
            {(plan.status === 'Draft' || plan.status === 'OnHold') && (
              <button type="button" className="btn-primary text-xs" data-testid="suc-plan-activate" disabled={busy} onClick={() => void run(() => successionApi.activatePlan(plan.id))}>
                {plan.status === 'Draft' ? t('suc.plan.activate') : t('suc.plan.resume')}
              </button>
            )}
            {plan.status !== 'Draft' && (
              <button type="button" className="btn-secondary text-xs" data-testid="suc-plan-review-open" onClick={() => setDialog('review')}>{t('suc.plan.review')}</button>
            )}
            {plan.status === 'Active' && (
              <button type="button" className="btn-secondary text-xs" data-testid="suc-plan-hold" onClick={() => setDialog('hold')}>{t('suc.plan.hold')}</button>
            )}
            {plan.status !== 'Draft' && (
              <button type="button" className="btn-secondary text-xs" data-testid="suc-plan-complete" disabled={busy} onClick={() => void run(() => successionApi.completePlan(plan.id))}>{t('suc.plan.complete')}</button>
            )}
            <button type="button" className="btn-secondary text-xs" data-testid="suc-plan-cancel" onClick={() => setDialog('cancel')}>{t('suc.plan.cancel')}</button>
          </div>
        )}
      </header>

      <div className="space-y-1">
        <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="suc-plan-progress">{t(progress.key, progress.params)}</p>
        <ProgressBar percent={plan.progress.percentComplete ?? 0} label={t('suc.plan.progressLabel')} />
        {plan.status === 'Draft' && manage && <p className="text-caption text-slate-500">{t('suc.plan.draftHint')}</p>}
      </div>

      <div className="space-y-2">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h4 className="text-xs font-semibold uppercase tracking-wide text-slate-500">{t('suc.plan.actions')}</h4>
          {manage && open && !adding && (
            <button type="button" className="btn-secondary gap-1 text-xs" data-testid="suc-action-add" onClick={() => setAdding(true)}>
              <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.action.add')}
            </button>
          )}
        </div>
        {adding && <AddActionForm plan={plan} onCancel={() => setAdding(false)} onDone={(p) => { setAdding(false); onChange(p); }} />}
        {plan.actions.length === 0 && !adding && <p className="text-xs text-slate-500">{t('suc.action.none')}</p>}
        {plan.actions.length > 0 && (
          <ul className="space-y-2">
            {[...plan.actions].sort((a, b) => a.sortOrder - b.sortOrder).map((a) => (
              <ActionRow key={a.id} action={a} canManage={manage && open} onUpdate={(input) => run(() => successionApi.updateAction(a.id, { ...input, version: a.version }))}
                onConfirm={() => run(() => successionApi.confirmCompletion(a.id, a.version))} busy={busy} />
            ))}
          </ul>
        )}
      </div>

      {dialog === 'hold' && (
        <ReasonDialog title={t('suc.plan.holdTitle')} confirmLabel={t('suc.plan.hold')} minLength={3} testId="suc-plan-hold" onClose={() => setDialog(null)}
          onConfirm={async (reason) => { onChange(await successionApi.holdPlan(plan.id, reason)); setDialog(null); }} />
      )}
      {dialog === 'cancel' && (
        <ReasonDialog title={t('suc.plan.cancelTitle')} intro={t('suc.plan.cancelIntro')} confirmLabel={t('suc.plan.cancel')} minLength={3} testId="suc-plan-cancel"
          onClose={() => setDialog(null)} onConfirm={async (reason) => { onChange(await successionApi.cancelPlan(plan.id, reason)); setDialog(null); }} />
      )}
      {dialog === 'review' && <ReviewDialog plan={plan} onClose={() => setDialog(null)} onDone={(p) => { setDialog(null); onChange(p); }} />}
    </article>
  );
}

function ActionRow({ action, canManage, onUpdate, onConfirm, busy }: {
  action: PlanAction; canManage: boolean; busy: boolean; onUpdate: (input: { status?: PlanAction['status']; progressNote?: string }) => Promise<void>;
  onConfirm: () => Promise<void>;
}) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const linked = isLinkedAction(action.actionType);
  const closed = action.status === 'Completed' || action.status === 'Cancelled';
  return (
    <li className="space-y-1 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-action-row" data-action-id={action.id}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{action.title}</p>
          <p className="text-xs text-slate-500">
            {t(actionTypeLabel[action.actionType])}
            {action.actionType === 'Learning' && action.learningPathName && <> · {(locale === 'ar' && action.learningPathNameAr) || action.learningPathName}</>}
            {action.actionType === 'Goal' && action.goalTitle && <> · {action.goalTitle}</>}
            {action.mentor && <> · {t('suc.action.mentorIs')} <PersonName person={action.mentor} /></>}
            {action.dueOn && <> · {t('suc.action.due', { date: fx.date(action.dueOn) })}</>}
          </p>
        </div>
        <span className="flex flex-wrap items-center gap-1">
          <StatusChip label={t(actionStatusLabel[action.status])} tone={actionTone(action.status, action.isOverdue)} />
          <RawCode testId="suc-action-status" value={action.status} />
          {action.isOverdue && !closed && <StatusChip label={t('suc.action.overdue')} tone="rose" />}
        </span>
      </div>
      <p className="text-caption text-slate-500" data-testid="suc-action-method">
        {action.completionMethod
          ? t(completionMethodLabel[action.completionMethod])
          : linked
            ? (action.actionType === 'Learning' ? t('suc.method.willLearning') : t('suc.method.willGoal'))
            : t('suc.method.willManual')}
      </p>
      {action.progressNote && <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.action.note', { note: action.progressNote })}</p>}
      {action.completionProposedAtUtc && !closed && (
        <p className="text-xs font-semibold text-amber-700 dark:text-amber-300" data-testid="suc-action-proposed">{t('suc.action.proposed')}</p>
      )}
      {canManage && !closed && (
        <div className="flex flex-wrap gap-2 pt-1">
          {!linked && action.status === 'NotStarted' && (
            <button type="button" className="btn-secondary text-xs" disabled={busy} onClick={() => void onUpdate({ status: 'InProgress' })}>{t('suc.action.start')}</button>
          )}
          {!linked && action.completionProposedAtUtc && (
            <button type="button" className="btn-primary text-xs" data-testid="suc-action-confirm" disabled={busy} onClick={() => void onConfirm()}>{t('suc.action.confirmDone')}</button>
          )}
          {!linked && !action.completionProposedAtUtc && (
            <button type="button" className="btn-secondary text-xs" data-testid="suc-action-complete" disabled={busy} onClick={() => void onUpdate({ status: 'Completed' })}>{t('suc.action.markDone')}</button>
          )}
          <button type="button" className="btn-secondary text-xs" disabled={busy} onClick={() => void onUpdate({ status: 'Cancelled' })}>{t('suc.action.cancel')}</button>
        </div>
      )}
    </li>
  );
}

function AddActionForm({ plan, onCancel, onDone }: { plan: DevelopmentPlan; onCancel: () => void; onDone: (p: DevelopmentPlan) => void }) {
  const { t, locale } = useLocale();
  const [type, setType] = useState<ActionType>('Learning');
  const [title, setTitle] = useState('');
  const [pathId, setPathId] = useState('');
  const [goalId, setGoalId] = useState('');
  const [mentor, setMentor] = useState<PersonRef | null>(null);
  const [due, setDue] = useState('');
  const [paths, setPaths] = useState<LearningPathOption[] | null>(null);
  const [goals, setGoals] = useState<GoalOption[] | null>(null);
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (type === 'Learning' && !paths) successionApi.learningPaths().then(setPaths).catch((e) => { setPaths([]); setError(successionError(e, t)); });
    if (type === 'Goal' && !goals) successionApi.goals(plan.employee.employeeId).then(setGoals).catch((e) => { setGoals([]); setError(successionError(e, t)); });
  }, [type, paths, goals, plan.employee.employeeId, t]);

  const problems = {
    title: title.trim().length >= 3 ? null : t('suc.action.titleRule'),
    link: type === 'Learning' && !pathId ? t('suc.action.choosePath') : type === 'Goal' && !goalId ? t('suc.action.chooseGoal') : type === 'Mentoring' && !mentor ? t('suc.action.chooseMentor') : null,
    due: due && due < todayIso() ? t('suc.action.dueFuture') : null,
  };

  const submit = async () => {
    setTouched(true);
    if (problems.title || problems.link || problems.due) return;
    setBusy(true);
    setError(null);
    try {
      onDone(await successionApi.addAction(plan.id, {
        actionType: type, title: title.trim(), dueOn: due || null,
        learningPathId: type === 'Learning' ? pathId : null,
        employeeGoalId: type === 'Goal' ? goalId : null,
        mentorEmployeeId: type === 'Mentoring' ? mentor?.employeeId ?? null : null,
      }));
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-3 rounded-xl border border-sapphire/30 bg-sapphire/[0.03] p-3" data-testid="suc-action-form">
      <div className="grid gap-3 sm:grid-cols-2">
        <Field id="suc-action-type" label={t('suc.action.type')}>
          <select id="suc-action-type" data-testid="suc-action-type" className="select w-full" value={type} onChange={(e) => setType(e.target.value as ActionType)}>
            {ACTION_TYPES.map((x) => <option key={x} value={x}>{t(actionTypeLabel[x])}</option>)}
          </select>
        </Field>
        <Field id="suc-action-title" label={t('suc.action.title')} error={touched ? problems.title : null}>
          <input id="suc-action-title" data-testid="suc-action-title" className="input w-full" value={title} onChange={(e) => setTitle(e.target.value)} />
        </Field>
      </div>
      {type === 'Learning' && (
        <Field id="suc-action-path" label={t('suc.action.path')} hint={paths?.length === 0 ? t('suc.action.noPaths') : t('suc.action.pathHint')} error={touched ? problems.link : null}>
          <select id="suc-action-path" data-testid="suc-action-path" className="select w-full" value={pathId} onChange={(e) => setPathId(e.target.value)} disabled={!paths}>
            <option value="">{paths ? t('suc.action.pickPath') : t('suc.loading')}</option>
            {paths?.map((p) => <option key={p.id} value={p.id}>{(locale === 'ar' && p.nameAr) || p.name}</option>)}
          </select>
        </Field>
      )}
      {type === 'Goal' && (
        <Field id="suc-action-goal" label={t('suc.action.goal')} hint={goals?.length === 0 ? t('suc.action.noGoals') : t('suc.action.goalHint')} error={touched ? problems.link : null}>
          <select id="suc-action-goal" data-testid="suc-action-goal" className="select w-full" value={goalId} onChange={(e) => setGoalId(e.target.value)} disabled={!goals}>
            <option value="">{goals ? t('suc.action.pickGoal') : t('suc.loading')}</option>
            {goals?.map((g) => <option key={g.id} value={g.id}>{g.title}</option>)}
          </select>
        </Field>
      )}
      {type === 'Mentoring' && (
        <PersonPicker id="suc-action-mentor" testId="suc-action-mentor" value={mentor} onChange={setMentor} label={t('suc.action.mentor')}
          exclude={[plan.employee.employeeId]} error={touched ? problems.link : null} />
      )}
      <Field id="suc-action-due" label={t('suc.action.dueLabel')} optional error={touched ? problems.due : null}>
        <input id="suc-action-due" data-testid="suc-action-due" type="date" className="input w-full sm:w-56" value={due} min={todayIso()} onChange={(e) => setDue(e.target.value)} />
      </Field>
      <FormError message={error} />
      <div className="flex flex-wrap justify-end gap-2">
        <button type="button" className="btn-secondary text-xs" onClick={onCancel}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-action-submit" disabled={busy} onClick={() => void submit()}>{busy ? t('suc.saving') : t('suc.action.addSubmit')}</button>
      </div>
    </div>
  );
}

function ReviewDialog({ plan, onClose, onDone }: { plan: DevelopmentPlan; onClose: () => void; onDone: (p: DevelopmentPlan) => void }) {
  const { t } = useLocale();
  const [note, setNote] = useState('');
  const [next, setNext] = useState(addMonthsIso(3));
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problems = {
    note: longEnough(note, 5) ? null : t('suc.check.minN', { count: 5 }),
    next: next && next > todayIso() ? null : t('suc.plan.reviewFuture'),
  };
  return (
    <ModalShell title={t('suc.plan.reviewTitle')} onClose={onClose} confirmLabel={busy ? t('suc.saving') : t('suc.plan.reviewSubmit')} confirmTestId="suc-plan-review-submit"
      busy={busy} onConfirm={async () => {
        setTouched(true);
        if (problems.note || problems.next) return;
        setBusy(true);
        setError(null);
        try { onDone(await successionApi.reviewPlan(plan.id, note.trim(), next, plan.version)); } catch (e) { setError(successionError(e, t)); } finally { setBusy(false); }
      }}>
      <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.plan.reviewIntro')}</p>
      <Field id="suc-plan-review-note" label={t('suc.plan.reviewNote')} error={touched ? problems.note : null}>
        <textarea id="suc-plan-review-note" data-testid="suc-plan-review-note" className="input min-h-[72px] w-full" value={note} onChange={(e) => setNote(e.target.value)} />
      </Field>
      <Field id="suc-plan-review-next" label={t('suc.plan.nextReviewLabel')} error={touched ? problems.next : null}>
        <input id="suc-plan-review-next" data-testid="suc-plan-review-next" type="date" className="input w-full" value={next} min={todayIso()} onChange={(e) => setNext(e.target.value)} />
      </Field>
      <FormError message={error} />
    </ModalShell>
  );
}

function ModalShell({ title, onClose, onConfirm, confirmLabel, confirmTestId, busy, children }: {
  title: string; onClose: () => void; onConfirm: () => void; confirmLabel: string; confirmTestId: string; busy: boolean; children: React.ReactNode;
}) {
  const { t } = useLocale();
  return (
    <Modal isOpen title={title} onClose={onClose} footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid={confirmTestId} disabled={busy} onClick={onConfirm}>{confirmLabel}</button>
      </>
    )}>
      <div className="space-y-3">{children}</div>
    </Modal>
  );
}
