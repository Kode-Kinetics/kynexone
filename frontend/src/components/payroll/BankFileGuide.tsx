'use client';

import { useEffect, useId, useRef, useState, type ReactNode, type Ref } from 'react';
import { CheckCircle2, Eye, Loader2 } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { PAY_OUT_STEPS, notPaidCount, settleBlockedNextLine, settleBlockedText, type PayOut, type PayOutNextKind, type PayOutReason, type PayOutStage, type PayOutStep, type PayOutStepKey } from '../../lib/payOut';
import { PAYMENT_ACTION_DENIED } from '../../lib/payrollPaymentAccess';
import { ActionBar } from '../ui/ActionBar';
import { Button } from '../ui/Button';
import { Skeleton } from '../ui/Skeleton';
import { StepStrip, type StepStripItem } from '../ui/StepStrip';

/**
 * Payroll → Bank / WPS Files as one guided flow (Screen Standard 3d), drawn from ONE derived object (lib/payOut.ts):
 *
 *   facts strip   what is being paid (the tab draws it)
 *   step strip    the five named steps, where you are, and the done ones you can open read-only
 *   one panel     the step being worked on: "STEP n OF 5", its name, one purpose sentence, the decision on the inline-start and
 *                 "what this means" on the inline-end
 *   action bar    Back, what comes next, and the ONE primary (sticky, so it is on screen without scrolling)
 *
 * The run bar's chip, the run picker and the pipeline read the same object, so they cannot tell a different story. This file only draws:
 * the content of each step comes from its owner through `slots` (the Bank / WPS tab: batch, response; the bank-file panel: setup, check,
 * create, send).
 */

export interface GuideAction {
  label: string;
  onClick?: () => void;
  /** Submit this form instead of calling onClick. */
  form?: string;
  disabled?: boolean;
  busy?: boolean;
  /** Why the button is off, in words (shown in the Next slot). */
  reason?: string | null;
}

export interface GuidePanel {
  /** Replaces the stage's own purpose sentence. */
  purpose?: ReactNode;
  /** The decision content (inline-start). */
  body?: ReactNode;
  /** "What this means" (inline-end, sunken). */
  aside?: ReactNode;
  /** The step opened read-only from the strip. */
  view?: ReactNode;
  /** Replaces the stage's own Next line. */
  next?: string;
}

export interface GuideSlots {
  panels?: Partial<Record<PayOutStage, GuidePanel>>;
  /** The handlers of the primary, by the kind derivePayOut names. */
  actions?: Partial<Record<PayOutNextKind, GuideAction>>;
  /** What just went wrong (the error summary takes focus once, role="alert"). */
  live?: ReactNode;
  liveRef?: Ref<HTMLDivElement>;
  /** Drawers and dialogs the steps open. */
  overlays?: ReactNode;
  /** Facts the tab shows in the facts strip and in steps 4 and 5. */
  data?: BankPanelData;
  /** Entries of the header's More menu that the panel owns. */
  menu?: { setup?: () => void; employeeBank?: () => void; about?: () => void; formats?: () => void; download?: () => void };
}

/** What the bank-file panel knows that the tab shows elsewhere (facts strip, step 5). */
export interface BankPanelData {
  bankName?: string | null;
  formatName?: string | null;
  accountTail?: string | null;
  company?: string | null;
  /** The employer setup asks the bank to upload the WPS file to Mudad (auto-WPS); null when not known. */
  autoWps?: boolean | null;
  file?: { reference: string; paymentDate: string; employeeCount: number; totalAmount: number; files: { name: string; sha256: string }[] } | null;
  /** The bank-file panel is mounted and ready (setup and file known). */
  ready?: boolean;
}

type T = ReturnType<typeof useT>;

export const stepName = (t: T, key: PayOutStepKey): string => {
  switch (key) {
    case 'batch': return t('Payment batch');
    case 'check': return t('Check employees');
    case 'create': return t('File details');
    case 'send': return t('Send to bank');
    case 'response': return t('Bank response');
  }
};

const noteText = (t: T, step: PayOutStep): string => {
  switch (step.note) {
    case 'done': return t('Done');
    case 'passed': return t('Passed');
    case 'file_created': return t('File created');
    case 'recorded': return t('Recorded as submitted');
    case 'confirmed': return t('Bank confirmed');
    case 'settled': return t('Paid — settled');
    case 'here': return t('You are here');
    case 'waiting': return t('Waiting for the bank');
    case 'needs_fix': return t('Needs a fix');
    case 'rejected': return t('Rejected by the bank');
    // Not "To do": its Arabic elsewhere is المطلوب ("required"), which misreads a step not reached yet.
    case 'todo': return t('Not started');
    case 'waits_setup': return t('Waits for the bank setup.');
  }
};

/** Why a step or the primary cannot move: a sentence that also says what to do. */
export const payOutReasonText = (t: T, reason: PayOutReason, p: PayOut): string => {
  switch (reason) {
    case 'run_not_locked': return t('Lock the run first: a payment batch can only be created once the run is Locked.');
    case 'no_permission_create': return t(PAYMENT_ACTION_DENIED.create);
    case 'choose_batch': return t('Choose the payment batch, then open it.');
    case 'checking': return t('Checking whether the bank file can be prepared…');
    case 'gate_off': return t('Bank file export is switched off for this company. Ask the platform team.');
    case 'gate_no_permission': return t('Needs the Payroll export permission. Ask an administrator.');
    case 'gate_failed': return t('The availability check failed. Use Retry below.');
    case 'validation_blocked': return t('{count, plural, one {# problem stops the file.} other {# problems stop the file.}} Fix each one, then check again.', { count: 1 });
    case 'stale': return t('Something changed since the last check. Check again.');
    case 'create_refused': return t('Fix the highlighted fields.');
    case 'no_permission_setup': return t('Bank setup is saved by someone with payroll setup permission. Ask your HR or payroll administrator.');
    case 'no_permission_wps': return t(PAYMENT_ACTION_DENIED.wps);
    case 'submit_not_allowed': return t('This batch can’t be recorded as submitted from its current status.');
    case 'rejected_ask_admin': return t('Ask your payroll administrator.');
    case 'tie_mismatch': return t('The file total differs from the approved run.');
    case 'payments_not_paid': return settleBlockedText(t, p);
  }
};

/** What comes after this step. The end has none. */
function nextLine(t: T, p: PayOut): string | null {
  switch (p.stage) {
    case 'batch':
      if (p.steps[0].reason === 'run_not_locked') return t('Next: Check employees, once the run is locked.');
      if (p.steps[0].reason === 'no_permission_create') return t('Payroll creates the batch next.');
      return t('Next: Check employees.');
    case 'setup': return t('Next: Check employees — then choose the credit date.');
    case 'check':
      // A blocked check: the next thing to do is the fix, not step 3.
      if (p.steps[1].reason === 'validation_blocked') return t('Next: fix each problem, then check again.');
      return p.steps[1].state === 'blocked'
        ? t('Next: Check employees, once this is cleared.')
        : t('Next: File details — choose the salary credit date and create the file.');
    case 'create': return t('Next: Send to bank — download the file and hand it to your bank.');
    case 'send': return p.status === 'downloaded' ? t('Next: Bank response — the bank confirms each payment.') : t('Next: Record it as submitted once the bank has it.');
    case 'response':
      if (p.status === 'rejected') return t('Next: Check employees again on the corrected batch.');
      return p.status === 'confirmed' && notPaidCount(p) > 0 ? settleBlockedNextLine(t) : t('Next: Finance settles the payments after the bank confirms.');
    case 'finished': return p.status === 'confirmed' && !p.next ? t('Finance settles the payments next.') : null;
  }
}

/** The step name in bold inside its own Next sentence (found in the translated sentence, so Arabic keeps its order). */
function withBoldStep(text: string, name: string | null): ReactNode {
  if (!name) return text;
  const at = text.indexOf(name);
  if (at < 0) return text;
  return <>{text.slice(0, at)}<strong className="font-semibold text-fg">{name}</strong>{text.slice(at + name.length)}</>;
}

const STAGE_RANK: Record<PayOutStage, number> = { batch: 1, setup: 1.5, check: 2, create: 3, send: 4, response: 5, finished: 6 };

/** A hand-over counts only when the user caused it (not when the page opened the run's only batch by itself). */
const RECENT_MS = 20_000;

/**
 * The hand-over between two steps: when the user finishes one, focus moves to the next step's heading and a confirmation says what was
 * done. The confirmation stays until the user's next action (WCAG 2.2.1), in a status region that is always on the page.
 */
export function useBankHandOver(stage: PayOutStage, loading: boolean, describe: (from: PayOutStage, to: PayOutStage) => string): { confirmation: string; confirmationSuccess: boolean; onUserAction: () => void; announce: (text: string) => void } {
  const lastActed = useRef(0);
  const settled = useRef<PayOutStage | null>(null);
  const describeRef = useRef(describe);
  describeRef.current = describe;
  const [confirmation, setConfirmation] = useState('');
  // Green is for a check that passed (and money the bank confirmed); every other confirmation is neutral.
  const [confirmationSuccess, setConfirmationSuccess] = useState(false);
  useEffect(() => {
    if (loading) return;
    const before = settled.current;
    settled.current = stage;
    if (!before || STAGE_RANK[stage] <= STAGE_RANK[before] || Date.now() - lastActed.current > RECENT_MS) return;
    setConfirmation(describeRef.current(before, stage));
    setConfirmationSuccess(before === 'check');
    const frame = requestAnimationFrame(() => {
      const target = document.querySelector<HTMLElement>('[data-testid="bank-guide"] [data-step-heading]');
      target?.focus({ preventScroll: true });
      const reduce = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      target?.scrollIntoView({ block: 'nearest', behavior: reduce ? 'auto' : 'smooth' });
    });
    return () => cancelAnimationFrame(frame);
  }, [stage, loading]);
  return {
    confirmation,
    confirmationSuccess,
    onUserAction: () => { lastActed.current = Date.now(); setConfirmation(''); },
    // A confirmation that is not a stage change (a file downloaded).
    announce: (text: string) => { setConfirmation(text); setConfirmationSuccess(false); },
  };
}

export interface BankFileGuideProps {
  payOut: PayOut;
  /** The run or its batches are still loading: say so instead of a stage that may change. */
  loading?: boolean;
  confirmation?: string;
  /** The confirmation is a passed check (green); otherwise it is neutral. */
  confirmationSuccess?: boolean;
  onUserAction?: () => void;
  /** A standing warning above the step card (the file was recorded as submitted). */
  banner?: ReactNode;
  /** The done step opened read-only, if any. */
  viewing?: PayOutStepKey | null;
  onView?: (key: PayOutStepKey | null) => void;
  /** The facts strip (up to 1679px). */
  facts?: ReactNode;
  /** The same facts as a rail beside the steps, from 1680px. */
  rail?: ReactNode;
  slots: GuideSlots;
}

export function BankFileGuide({ payOut, loading = false, confirmation = '', confirmationSuccess = false, onUserAction, banner, viewing = null, onView, facts, rail, slots }: BankFileGuideProps) {
  const t = useT();
  const uid = useId();
  const stage = payOut.stage;
  const viewingStep = viewing && payOut.steps.find(s => s.key === viewing && s.state === 'done') ? viewing : null;
  const panelKey: PayOutStage = viewingStep ?? stage;
  const panel = slots.panels?.[panelKey];
  const stageStep = stage === 'setup' || stage === 'finished' ? null : stage;

  const titleFor = (key: PayOutStage): string => (key === 'setup' ? t('Add your bank details once') : key === 'finished' ? stepName(t, 'response') : stepName(t, key));
  const eyebrow = viewingStep
    ? t('Step {n} of 5', { n: PAY_OUT_STEPS.indexOf(viewingStep) + 1 })
    : stage === 'setup' ? t('Before step 2: Bank setup') : t('Step {n} of 5', { n: Math.min(5, payOut.position) });

  const action = payOut.next ? slots.actions?.[payOut.next.kind] : undefined;
  const disabled = !!payOut.next?.disabled || !!action?.disabled;
  // The tie-out reason carries its amount (the tab formats it in the batch's currency); every other reason is the stage's own sentence.
  const reason = payOut.next?.reason === 'tie_mismatch' && action?.reason ? action.reason
    : payOut.next?.reason ? payOutReasonText(t, payOut.next.reason, payOut) : action?.reason ?? null;
  const next = viewingStep ? null : panel?.next ?? nextLine(t, payOut);
  const nextName = stage === 'batch' || stage === 'setup' ? stepName(t, 'check') : stage === 'check' ? stepName(t, 'create') : stage === 'create' ? stepName(t, 'send') : stage === 'send' && payOut.status === 'downloaded' ? stepName(t, 'response') : null;

  // Done steps open read-only from the strip; the stage itself is never set from there.
  const items: StepStripItem[] = [
    ...payOut.steps.slice(0, 1).map((s, i) => item(s, i)),
    ...(stage === 'setup' ? [{ key: 'setup', label: t('Bank setup'), state: 'current' as const, note: t('You are here'), isStage: true }] : []),
    ...payOut.steps.slice(1).map((s, i) => item(s, i + 1)),
  ];
  function item(s: PayOutStep, i: number): StepStripItem {
    return {
      key: s.key, label: stepName(t, s.key), index: i + 1, state: s.state, note: noteText(t, s), isStage: s.key === stage,
      success: s.state === 'done' && (s.note === 'passed' || s.note === 'confirmed' || s.note === 'settled'),
      onView: s.state === 'done' && onView ? () => onView(s.key === viewingStep ? null : s.key) : undefined,
    };
  }
  const compactLabel = stage === 'setup' ? t('Before step 2: Bank setup')
    : stage === 'finished' ? stepName(t, 'response')
    : t('Step {n} of 5: {step}', { n: payOut.position, step: stepName(t, stage) });

  // Back: the step before this one, opened read-only.
  const backKey: PayOutStepKey | null = viewingStep ? null : stageStep ? PAY_OUT_STEPS[PAY_OUT_STEPS.indexOf(stageStep) - 1] ?? null : stage === 'setup' ? 'batch' : stage === 'finished' ? 'send' : null;
  const back = backKey && onView && payOut.steps.find(s => s.key === backKey)?.state === 'done'
    ? { label: t('Back: {step}', { step: stepName(t, backKey) }), onClick: () => onView(backKey) } : null;

  const nextId = `${uid}-next`;
  const primary = viewingStep ? (
    <button type="button" className="btn-secondary" onClick={() => onView?.(null)}>{t('Back to step {n}', { n: Math.max(1, Math.min(5, payOut.position)) })}</button>
  ) : action ? (
    <span data-primary-action className="inline-flex">
      <Button
        variant="primary"
        type={action.form ? 'submit' : 'button'}
        form={action.form}
        onClick={action.form ? undefined : action.onClick}
        disabled={disabled}
        busy={action.busy}
        aria-describedby={disabled && reason ? nextId : undefined}
        data-testid="bank-guide-primary"
        // An off primary must look off (the reason sits beside it); the shared button only changes the cursor.
        className="disabled:opacity-50"
      >
        {action.busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
        {action.label}
      </Button>
    </span>
  ) : null;

  return (
    <div
      className={`max-sm:pb-[calc(var(--action-bar-cover,8rem)+16px)] ${rail ? 'wide:grid wide:grid-cols-[minmax(0,1fr)_360px] wide:items-start wide:gap-6' : ''}`}
      data-testid="bank-guide"
      data-stage={stage}
      aria-busy={loading || undefined}
      onPointerDownCapture={onUserAction}
      onKeyDownCapture={e => { if (e.key === 'Enter' || e.key === ' ') onUserAction?.(); }}
    >
      <div className="min-w-0 space-y-3">
      {facts && <div className={rail ? 'wide:hidden' : undefined}>{facts}</div>}

      {loading ? (
        <div role="status" data-testid="bank-guide-loading" className="surface space-y-3 p-5">
          <span className="sr-only">{t('Loading the payment batches…')}</span>
          <Skeleton className="h-2 w-full" />
          <Skeleton className="h-5 w-48" />
          <Skeleton className="h-4 w-2/3" />
        </div>
      ) : (
        <StepStrip
          items={items}
          ariaLabel={t('Steps to pay out this run')}
          compactLabel={compactLabel}
          testId="bank-guide-progress"
          listTestId="bank-file-steps"
          stripId="bank-file"
          viewingKey={viewingStep}
        />
      )}

      {!loading && banner}

      {!loading && (
        <section className="surface relative" data-step-panel={panelKey} aria-labelledby={`${uid}-title`}>
          {viewingStep && (
            <div className="flex flex-wrap items-center justify-between gap-2 rounded-t-xl border-b border-line bg-surface-sunken px-5 py-2 text-body-sm text-fg-muted" data-testid="bank-guide-viewing">
              <span className="inline-flex items-center gap-1.5"><Eye className="h-4 w-4" aria-hidden="true" />{t('You are viewing step {n}. Nothing here can be changed.', { n: PAY_OUT_STEPS.indexOf(viewingStep) + 1 })}</span>
            </div>
          )}
          <div className="p-5 pb-6 sm:px-6 sm:pt-5">
            <div className="flex min-h-6 flex-wrap items-center justify-between gap-2">
              <p className="text-meta font-semibold uppercase tracking-wide text-primary max-sm:sr-only rtl:normal-case rtl:tracking-normal" data-testid="bank-guide-position" data-guide-position>{eyebrow}</p>
              {/* The persistent status region: the hand-over confirmation lives here until the user's next action. */}
              <div role="status" aria-live="polite" data-testid="bank-guide-confirmation" className="empty:hidden">
                {confirmation && (
                  <p className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-meta font-medium ${confirmationSuccess ? 'bg-success-soft text-success-fg' : 'bg-info-soft text-info-fg'}`}>
                    <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />{confirmation}
                  </p>
                )}
              </div>
            </div>
            <h2 id={`${uid}-title`} tabIndex={-1} data-step-heading className="mt-1 text-h2 font-semibold text-fg outline-none">{titleFor(panelKey)}</h2>
            {!viewingStep && panel?.purpose && <p className="mt-1 max-w-[80ch] text-body text-fg-muted" data-testid="bank-guide-sentence">{panel.purpose}</p>}
            {!viewingStep && slots.live !== undefined && (
              <div ref={slots.liveRef} tabIndex={-1} className="mt-4 space-y-3 outline-none empty:hidden" data-testid="bank-guide-live">{slots.live}</div>
            )}
            <div
              key={panelKey}
              className="mt-5 grid animate-slide-in-right gap-6 rtl:animate-slide-in-left md:grid-cols-[minmax(0,1.35fr)_minmax(0,1fr)]"
              data-testid={`bank-step-body-${panelKey}`}
            >
              <div className="min-w-0 space-y-4">{viewingStep ? panel?.view : panel?.body}</div>
              {!viewingStep && panel?.aside && <aside className="min-w-0 self-start rounded-xl border border-line bg-surface-sunken p-4 text-body-sm text-fg-muted">{panel.aside}</aside>}
            </div>
          </div>
          <ActionBar
            back={back}
            next={next ? (disabled && reason
              ? <span data-testid="bank-guide-primary-reason">{reason}</span>
              : withBoldStep(next, nextName)) : (disabled && reason ? <span data-testid="bank-guide-primary-reason">{reason}</span> : null)}
            nextId={nextId}
            nextTestId="bank-guide-next"
            primary={primary}
          />
        </section>
      )}
      </div>
      {rail && <div className="hidden min-w-0 wide:block">{rail}</div>}
      {slots.overlays}
    </div>
  );
}
