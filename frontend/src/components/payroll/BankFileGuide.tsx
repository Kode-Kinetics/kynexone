'use client';

import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { AlertTriangle, Check, CheckCircle2, Circle, CircleDot, Loader2 } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { bankChipFor, type BankStage, type BankStageKey } from '../../lib/bankFileGuide';
import type { BankStep, BankStepKey } from '../../lib/bankFileSteps';
import { Button } from '../ui/Button';
import { Skeleton } from '../ui/Skeleton';
import { bankReasonText, bankStateName, bankStepName } from './BankFileSteps';

/**
 * The Bank / WPS tab as one guided flow (Screen Standard 3d): a header that always shows progress (a segmented track, "Step 2 of 4"), says what
 * to do now in one sentence with the ONE primary button for it, and says what comes next. Then the four steps: only the current one is open; a
 * finished step folds to one line with Change or View; a step still ahead says what it waits for. The employer's bank setup is a gate in front
 * of step 2 while it is incomplete and a one-line row (Edit) once it is complete.
 *
 * When a step the user just finished hands over to the next, the next step takes focus and scroll and a brief confirmation says what was done.
 * When the last step is done the header turns into the end state, with Payment Tracking as the one next action.
 *
 * This file only draws. The stage comes from lib/bankFileGuide.ts (one derivation for the header, the track, the list, the button and the chip),
 * and the content of each step comes from its owner through `slots`: the Bank / WPS tab (batch, records, tracking) and the bank-file panel
 * (setup, check, create, download).
 */

export interface GuideStepSlot {
  /** One line for a finished step. */
  summary?: ReactNode;
  /** What the step shows while it is open (current, blocked, or opened with Change / View). */
  body?: ReactNode;
  /** The word on the button that reopens a finished step. Default: Change. */
  reopen?: 'change' | 'view';
}

export interface GuideAction {
  label: string;
  onClick?: () => void;
  /** Submit this form (the setup form lives further down the page) instead of calling onClick. */
  form?: string;
  disabled?: boolean;
  busy?: boolean;
  /** Why the button is disabled, in words, shown next to it. */
  reason?: string | null;
}

export interface GuideSlots {
  steps?: Partial<Record<BankStepKey | 'setup', GuideStepSlot>>;
  /** The one primary button, by stage. A stage with no entry has none. */
  actions?: Partial<Record<BankStageKey, GuideAction>>;
  /** Messages the page announces (saved, refused, validation result). Focusable so a refusal can take focus. */
  live?: ReactNode;
  liveRef?: React.Ref<HTMLDivElement>;
  /** Below the steps: the one-line setup row, the notice, the secondary links. */
  footer?: ReactNode;
  /** Drawers and dialogs the steps open. */
  overlays?: ReactNode;
}

export interface BankFileGuideProps {
  period: string;
  stage: BankStage;
  steps: BankStep[];
  /** A payment batch exists for the run (decides "create one" or "choose one" at step 1). */
  hasBatches: boolean;
  /** The run or its batches are still loading: say so instead of a stage that may change. */
  loading?: boolean;
  /** A more specific sentence than the stage's own (the run's actual status, where it is Locked). */
  sentenceOverride?: string;
  /** The end state's sentence ("Bank file submitted — waiting for the bank's confirmation (2 of 5)"). */
  finishedText?: string;
  /** From useBankHandOver: the brief "what was done" line, and the call that tells it the user acted. */
  confirmation?: string;
  onUserAction?: () => void;
  slots: GuideSlots;
}

type T = ReturnType<typeof useT>;

const STAGE_LABEL = (t: T, key: BankStageKey): string => (key === 'setup' ? t('Bank setup') : key === 'finished' ? t('All steps are done') : bankStepName(t, key));

/** Where a stage sits on the way through: setup before the steps, finished after them. */
const rank = (stage: BankStage): number => (stage.key === 'finished' ? 5 : stage.position);

function sentenceFor(t: T, stage: BankStage, hasBatches: boolean, finishedText?: string): string {
  if (stage.state === 'blocked' && stage.reason) return bankReasonText(t, stage.reason);
  switch (stage.key) {
    case 'batch': return hasBatches ? t('Choose the payment batch you want to prepare.') : t('Create the payment batch for this run. It lists who is paid and how much.');
    case 'setup': return t('Enter your bank account details once. The bank file cannot be made without them.');
    case 'check': return stage.reason === 'checking' ? bankReasonText(t, 'checking') : t('Set the credit value date, then check the batch. Nothing is created yet.');
    case 'create': return t('The check passed. Create the bank file: once made, it cannot be changed.');
    case 'download': return t('Download the files, send them to your bank, then record the batch as submitted in Payment Tracking.');
    case 'finished': return finishedText ?? t('This batch has been submitted. Follow the bank’s confirmation in Payment Tracking.');
  }
}

/** What happens after this step, so the user always knows what is coming. The end state has no "next": it has its one action. */
function nextFor(t: T, stage: BankStage): string | null {
  if (stage.state === 'blocked' && stage.reason === 'validation_blocked') return t('Next: check again, then create the bank file.');
  if (stage.state === 'blocked') return t('Next: check the batch, once this is cleared.');
  switch (stage.key) {
    case 'batch': return t('Next: check the batch. The check needs a credit value date.');
    case 'setup': return t('Next: check the batch.');
    case 'check': return t('Next: create the bank file. It can only be made after a check with no blockers.');
    case 'create': return t('Next: download the files and send them to your bank.');
    case 'download': return t('Next: record the batch as submitted in Payment Tracking.');
    case 'finished': return null;
  }
}

/** The brief confirmation of the step the user has just finished. */
function doneText(t: T, from: BankStage): string {
  switch (from.key) {
    case 'setup': return t('Bank setup saved.');
    case 'batch': return t('Payment batch chosen.');
    case 'check': return t('Batch checked — no blockers.');
    case 'create': return t('Bank file created.');
    case 'download': return t('Recorded as submitted.');
    case 'finished': return '';
  }
}

/** A hand-over only counts when the user caused it (not when the page opened the run's only batch by itself). */
const RECENT_MS = 20_000;
const CONFIRMATION_MS = 8_000;

/**
 * The hand-over between two steps: when the user finishes one and the next becomes current, the next takes focus and scroll and a brief
 * confirmation says what was done. Lives in the tab (not in the guide) so it survives the guide being drawn again while the bank-file panel loads.
 * `onUserAction` is called for every click or key inside the guide; a forward move more than a few seconds after one is not the user's doing.
 */
export function useBankHandOver(stage: BankStage, loading: boolean): { confirmation: string; onUserAction: () => void } {
  const t = useT();
  const lastActed = useRef(0);
  const settled = useRef<BankStage | null>(null);
  const [confirmation, setConfirmation] = useState('');
  useEffect(() => {
    if (loading) return;
    const before = settled.current;
    settled.current = stage;
    if (!before || rank(stage) <= rank(before) || Date.now() - lastActed.current > RECENT_MS) return;
    setConfirmation(doneText(t, before));
    const frame = requestAnimationFrame(() => {
      const root = document.querySelector<HTMLElement>('[data-testid="bank-guide"]');
      const target = stage.key === 'finished'
        ? root?.querySelector<HTMLElement>('[data-guide-position]')
        : root?.querySelector<HTMLElement>(`[data-step="${stage.key}"] [data-step-heading]`);
      target?.focus({ preventScroll: true });
      const reduce = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      (target?.closest('li') ?? target)?.scrollIntoView({ block: 'nearest', behavior: reduce ? 'auto' : 'smooth' });
    });
    const timer = window.setTimeout(() => setConfirmation(''), CONFIRMATION_MS);
    return () => { cancelAnimationFrame(frame); window.clearTimeout(timer); };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stage.key, loading]);
  return { confirmation, onUserAction: () => { lastActed.current = Date.now(); } };
}

export function BankFileGuide({ period, stage, steps, hasBatches, loading = false, sentenceOverride, finishedText, confirmation = '', onUserAction, slots }: BankFileGuideProps) {
  const t = useT();
  const uid = useId();
  const sentence = sentenceOverride ?? sentenceFor(t, stage, hasBatches, finishedText);
  const next = nextFor(t, stage);
  const chip = bankChipFor(stage);
  const action = slots.actions?.[stage.key];
  const [opened, setOpened] = useState<BankStepKey | 'setup' | null>(null);
  // A step reopened with Change / View stays open only while it is still the same stage; a new stage starts clean.
  useEffect(() => { setOpened(null); }, [stage.key]);

  const chipText = chip === 'setup' ? t('Setup needed') : chip === 'attention' ? t('Needs a fix') : chip === 'ready' ? t('Bank file ready') : chip === 'submitted' ? t('Submitted to the bank') : t('Waiting for the bank file');
  const chipTone = chip === 'submitted' || chip === 'ready' ? 'badge-success' : chip === 'attention' ? 'badge-danger' : chip === 'setup' ? 'badge-warning' : 'badge-neutral';
  const reasonId = `${uid}-reason`;
  const sentenceId = `${uid}-sentence`;
  const list: { key: BankStepKey | 'setup'; state: BankStep['state']; reason?: BankStep['reason']; index: number }[] = [
    ...(stage.key === 'setup' ? [{ key: 'setup' as const, state: 'current' as const, index: 0 }] : []),
    ...steps.map((s, i) => ({ key: s.key, state: s.state, reason: s.reason, index: i + 1 })),
  ];
  const doneCount = list.filter(i => i.state === 'done').length;

  return (
    <section
      className="surface"
      data-testid="bank-guide"
      data-stage={stage.key}
      aria-labelledby={`${uid}-title`}
      aria-busy={loading || undefined}
      onPointerDownCapture={onUserAction}
      onKeyDownCapture={onUserAction}
    >
      <header className="space-y-2 border-b border-line p-4" data-testid="bank-guide-header">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <h2 id={`${uid}-title`} className="text-h4 font-semibold text-fg">{t('Bank file · {period}', { period })}</h2>
          {!loading && <span className={`badge ${chipTone}`} data-testid="bank-guide-chip" data-chip={chip}>{chipText}</span>}
        </div>
        {loading ? (
          <div role="status" data-testid="bank-guide-loading" className="space-y-2">
            <span className="sr-only">{t('Loading the payment batches…')}</span>
            <Skeleton className="h-4 w-40" />
            <Skeleton className="h-4 w-2/3" />
          </div>
        ) : (
          <>
            {/* Progress: the segments are the steps (the setup gate first while it applies); done ones are ticked, the current one is marked. */}
            <div
              role="progressbar"
              aria-label={t('Progress through the bank file steps')}
              aria-valuemin={0}
              aria-valuemax={list.length}
              aria-valuenow={doneCount}
              aria-valuetext={stage.key === 'finished' ? t('All steps are done') : stage.key === 'setup' ? t('Before you start') : t('Step {n} of {total}', { n: stage.position, total: stage.total })}
              data-testid="bank-guide-progress"
              className="grid gap-1.5"
              style={{ gridTemplateColumns: `repeat(${list.length}, minmax(0, 1fr))` }}
            >
              {list.map(item => {
                const isStage = stage.key === item.key;
                const bar = item.state === 'done' ? 'bg-success' : item.state === 'blocked' ? 'bg-danger' : item.state === 'current' ? 'bg-primary' : 'bg-line';
                return (
                  <div key={item.key} data-track={item.key} data-state={item.state} className="min-w-0">
                    <div className={`h-1.5 rounded-full ${bar}`} />
                    <p className={`mt-1 hidden items-center gap-1 text-meta sm:flex ${isStage ? 'font-semibold text-fg' : 'text-fg-muted'}`}>
                      {item.state === 'done' && <Check className="h-3 w-3 shrink-0 text-success-fg" aria-hidden="true" />}
                      <span className="min-w-0 truncate">{item.key === 'setup' ? t('Bank setup') : bankStepName(t, item.key)}</span>
                    </p>
                  </div>
                );
              })}
            </div>
            <p className="text-body-sm font-medium text-fg-muted outline-none" tabIndex={-1} data-guide-position data-testid="bank-guide-position">
              {stage.key === 'finished' ? t('All steps are done')
                : stage.key === 'setup' ? `${t('Before you start')} · ${STAGE_LABEL(t, 'setup')}`
                : `${t('Step {n} of {total}', { n: stage.position, total: stage.total })} · ${STAGE_LABEL(t, stage.key)}`}
            </p>
            <div className="flex flex-wrap items-center justify-between gap-3">
              <p id={sentenceId} className="min-w-0 flex-1 text-body text-fg" data-testid="bank-guide-sentence">
                {stage.key === 'finished' && <CheckCircle2 className="me-1.5 inline h-4 w-4 align-text-bottom text-success-fg" aria-hidden="true" />}
                {sentence}
              </p>
              {action && (
                <Button
                  variant="primary"
                  type={action.form ? 'submit' : 'button'}
                  form={action.form}
                  onClick={action.form ? undefined : action.onClick}
                  disabled={action.disabled}
                  busy={action.busy}
                  aria-describedby={action.reason ? reasonId : action.disabled ? sentenceId : undefined}
                  data-testid="bank-guide-primary"
                >
                  {action.busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                  {action.label}
                </Button>
              )}
            </div>
            {action?.reason && <p id={reasonId} className="text-meta text-fg-muted" data-testid="bank-guide-primary-reason">{action.reason}</p>}
            {next && <p className="text-body-sm text-fg-muted" data-testid="bank-guide-next">{next}</p>}
          </>
        )}
        <div role="status" aria-live="polite" data-testid="bank-guide-confirmation" className="empty:hidden">
          {confirmation && (
            <p className="inline-flex items-center gap-1.5 rounded-md bg-success-soft px-2.5 py-1 text-body-sm font-medium text-success-fg">
              <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />{confirmation}
            </p>
          )}
        </div>
      </header>

      {slots.live !== undefined && (
        <div ref={slots.liveRef} tabIndex={-1} aria-live="polite" className="space-y-3 px-4 pt-4 outline-none empty:hidden" data-testid="bank-guide-live">{slots.live}</div>
      )}

      {!loading && (
        <ol aria-label={t('Steps to a bank file')} data-step-strip="bank-file" data-testid="bank-file-steps" className="divide-y divide-line">
          {list.map(item => {
            const slot = slots.steps?.[item.key];
            const isStage = stage.key === item.key;
            const open = item.state === 'current' || item.state === 'blocked' || opened === item.key;
            const Icon = item.state === 'done' ? CheckCircle2 : item.state === 'blocked' ? AlertTriangle : item.state === 'current' ? CircleDot : Circle;
            const tone = item.state === 'done' ? 'text-success-fg' : item.state === 'blocked' ? 'text-danger-fg' : item.state === 'current' ? 'text-primary' : 'text-fg-muted';
            const bodyId = `${uid}-${item.key}-body`;
            return (
              <li key={item.key} data-step={item.key} data-state={item.state} aria-current={isStage ? 'step' : undefined} className={`scroll-mt-4 p-4 ${isStage ? 'bg-primary-soft/40' : ''}`}>
                <div className="flex items-start gap-2">
                  <Icon className={`mt-0.5 h-4 w-4 shrink-0 ${tone}`} aria-hidden="true" />
                  <div className="min-w-0 flex-1">
                    <p className="flex flex-wrap items-baseline gap-x-2 text-body-sm font-semibold text-fg">
                      <span tabIndex={-1} data-step-heading className="outline-none">{item.key === 'setup' ? STAGE_LABEL(t, 'setup') : <><span className="tabular-nums">{item.index}.</span> {bankStepName(t, item.key)}</>}</span>
                      <span className="text-meta font-medium text-fg-muted">{bankStateName(t, item.state)}</span>
                    </p>
                    {item.state === 'done' && slot?.summary && <div className="mt-0.5 text-body-sm text-fg-muted">{slot.summary}</div>}
                    {item.state === 'upcoming' && item.reason && <p className="mt-0.5 text-meta text-fg-muted" data-testid={`bank-step-reason-${item.key}`}>{bankReasonText(t, item.reason)}</p>}
                  </div>
                  {item.state === 'done' && slot?.body && (
                    <button type="button" className="btn-ghost btn-sm shrink-0" aria-expanded={open} aria-controls={bodyId} onClick={() => setOpened(opened === item.key ? null : item.key)}>
                      {slot.reopen === 'view' ? t('View') : t('Change')}
                    </button>
                  )}
                </div>
                {open && slot?.body && <div id={bodyId} className="mt-3 space-y-3 ps-6" data-testid={`bank-step-body-${item.key}`}>{slot.body}</div>}
              </li>
            );
          })}
        </ol>
      )}

      {slots.footer && <div className="space-y-3 border-t border-line p-4" data-testid="bank-guide-footer">{slots.footer}</div>}
      {slots.overlays}
    </section>
  );
}
