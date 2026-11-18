/**
 * The words of the four bank-file steps: a step's name, its state, and the reason a step cannot move (a sentence that also says what to do).
 * The steps themselves are drawn by BankFileGuide, which reads the one stage derived in lib/bankFileGuide.ts.
 */

import { useT } from '../../hooks/useT';
import { PAYMENT_ACTION_DENIED } from '../../lib/payrollPaymentAccess';
import type { BankStep, BankStepKey, BankStepReason } from '../../lib/bankFileSteps';

type T = ReturnType<typeof useT>;

export const bankStepName = (t: T, key: BankStepKey) => {
  switch (key) {
    case 'batch': return t('Choose the payment batch');
    case 'check': return t('Check the batch');
    case 'create': return t('Create the bank file');
    case 'download': return t('Download and mark submitted');
  }
};

export const bankStateName = (t: T, state: BankStep['state']) => {
  switch (state) {
    case 'done': return t('Done');
    case 'current': return t('Current');
    case 'blocked': return t('Blocked');
    case 'upcoming': return t('Upcoming');
  }
};

/** Why a step cannot move, in a sentence that also says what to do. */
export const bankReasonText = (t: T, reason: BankStepReason): string => {
  switch (reason) {
    case 'run_not_locked': return t('Lock the run first: a payment batch can only be created once the run is Locked.');
    case 'no_permission_create': return t(PAYMENT_ACTION_DENIED.create);
    case 'choose_batch': return t('Create a payment batch for this run, or open one from the list.');
    case 'checking': return t('Checking whether the bank file can be prepared…');
    case 'no_permission': return t('Needs the Payroll export permission. Ask an administrator.');
    case 'off': return t('Bank file export is switched off for this company. Ask the platform team.');
    case 'check_failed': return t('The availability check failed. Use Retry below.');
    case 'validation_blocked': return t('The check found blockers. Fix each one below, then check again.');
    case 'needs_validation': return t('Waits for a successful check.');
    case 'needs_file': return t('Waits for the bank file.');
    case 'needs_setup': return t('Waits for the bank setup.');
  }
};
