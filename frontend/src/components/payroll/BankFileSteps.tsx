'use client';

import { AlertTriangle, CheckCircle2, Circle, CircleDot } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { PAYMENT_ACTION_DENIED } from '../../lib/payrollPaymentAccess';
import type { BankStep, BankStepKey, BankStepReason } from '../../lib/bankFileSteps';

type T = ReturnType<typeof useT>;

const STEP_NAME = (t: T, key: BankStepKey) => {
  switch (key) {
    case 'batch': return t('Choose the payment batch');
    case 'check': return t('Check the batch');
    case 'create': return t('Create the bank file');
    case 'download': return t('Download and mark submitted');
  }
};

const STATE_NAME = (t: T, state: BankStep['state']) => {
  switch (state) {
    case 'done': return t('Done');
    case 'current': return t('Current');
    case 'blocked': return t('Blocked');
    case 'upcoming': return t('Upcoming');
  }
};

/** Why a step cannot move, in a sentence that also says what to do. */
const REASON = (t: T, reason: BankStepReason): string => {
  switch (reason) {
    case 'run_not_locked': return t('Lock the run first: a payment batch can only be created once the run is Locked.');
    case 'no_permission_create': return t(PAYMENT_ACTION_DENIED.create);
    case 'choose_batch': return t('Create a payment batch for this run, or open one from the list.');
    case 'checking': return t('Checking whether the bank file can be prepared…');
    case 'no_permission': return t('Needs the Payroll export permission. Ask an administrator.');
    case 'off': return t('Bank file export is switched off for this company. Ask the platform team.');
    case 'check_failed': return t('The availability check failed. Use Retry in the bank file section.');
    case 'validation_blocked': return t('The check found blockers. Fix them in the bank file section, then check again.');
    case 'needs_validation': return t('Waits for a successful check.');
    case 'needs_file': return t('Waits for the bank file.');
  }
};

/**
 * Four steps from a locked run to a file the bank can take (Screen Standard 3d): each is done, current, blocked or upcoming, with the
 * reason when it cannot move. State icon plus text, never colour alone. The step name jumps to the part of the tab where it happens.
 */
export function BankFileSteps({ steps, onJump, onOpenTracking }: {
  steps: BankStep[];
  onJump: (key: BankStepKey) => void;
  onOpenTracking?: () => void;
}) {
  const t = useT();
  return (
    <ol aria-label={t('Steps to a bank file')} data-step-strip="bank-file" data-testid="bank-file-steps" className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
      {steps.map((step, index) => {
        const tone = step.state === 'done' ? 'border-line bg-success-soft text-success-fg'
          : step.state === 'blocked' ? 'border-danger bg-danger-soft text-danger-fg'
          : step.state === 'current' ? 'border-primary bg-primary-soft text-fg'
          : 'border-line bg-surface text-fg-muted';
        const Icon = step.state === 'done' ? CheckCircle2 : step.state === 'blocked' ? AlertTriangle : step.state === 'current' ? CircleDot : Circle;
        return (
          <li key={step.key} data-step={step.key} data-state={step.state} aria-current={step.state === 'current' ? 'step' : undefined}
            className={`flex min-w-0 flex-col gap-1 rounded-lg border p-3 ${tone}`}>
            <div className="flex items-start gap-1.5">
              <Icon className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
              <button type="button" onClick={() => onJump(step.key)}
                className="min-h-7 min-w-0 text-start text-body-sm font-semibold hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
                <span className="tabular-nums">{index + 1}.</span> {STEP_NAME(t, step.key)}
              </button>
            </div>
            <p className="text-meta font-medium">{STATE_NAME(t, step.state)}</p>
            {step.reason && step.state !== 'done' && <p className="text-meta" data-testid={`bank-step-reason-${step.key}`}>{REASON(t, step.reason)}</p>}
            {step.key === 'download' && step.state === 'current' && (
              <>
                <p className="text-meta">{t('Download the files, send them to your bank, then record the batch as submitted in Payment Tracking.')}</p>
                {onOpenTracking && <button type="button" className="btn-secondary btn-sm self-start" onClick={onOpenTracking}>{t('Open Payment Tracking')}</button>}
              </>
            )}
          </li>
        );
      })}
    </ol>
  );
}
