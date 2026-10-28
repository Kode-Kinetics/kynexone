/** Pure rules of the pay approval view (no React), so they can be pinned by a unit test. */

export const PAY_GAP_KEY = 'pay:compensationUnapproved';
export const PAY_APPROVE_PERMISSION = 'payroll.approve';
export const REASON_MAX = 500;

export interface PayApprovalFailure {
  /** English sentence; the screen renders it through t(). */
  message: string;
  /** True when the right next step is to reload the package (it moved, or it is gone from the queue). */
  reload: boolean;
}

type ErrorLike = { response?: { status?: number; data?: { code?: string; error?: string } } };

/** Maps the server's refusal to one sentence and says whether reloading is the way forward. */
export function payApprovalFailure(error: unknown): PayApprovalFailure {
  const response = (error as ErrorLike | null)?.response;
  if (!response) return { message: 'The pay approval service could not be reached. Try again.', reload: false };
  const code = response.data?.code ?? response.data?.error;
  switch (code) {
    case 'package_changed': return { message: 'The pay package changed after you opened it. Reload it and review the new figures.', reload: true };
    case 'second_person_required': return { message: 'You cannot approve this package: you entered it, you are the employee, or you issued the employee\'s sign-in code.', reload: false };
    case 'not_pending': return { message: 'This package is no longer waiting for approval.', reload: true };
    case 'reason_required': return { message: 'Add a reason saying what you checked.', reload: false };
    case 'reason_too_long': return { message: 'Keep the reason under 500 characters.', reload: false };
    case 'reason_contains_account_number': return { message: 'Do not write the account number in the reason.', reload: false };
    default: break;
  }
  if (response.status === 404) return { message: 'This package is not available to you.', reload: true };
  if (response.status === 403) return { message: 'You do not have permission to approve pay packages.', reload: false };
  if (response.status === 429) return { message: 'Too many attempts. Wait a minute and try again.', reload: false };
  return { message: 'The decision could not be saved. Try again.', reload: false };
}

/** Null when the reason is acceptable; the sentence to show otherwise. */
export function reasonError(reason: string): string | null {
  const text = reason.trim();
  if (!text) return 'Add a reason saying what you checked.';
  if (text.length > REASON_MAX) return 'Keep the reason under 500 characters.';
  return null;
}

/** The account as shown to the approver: masked to its last four characters. */
export function maskedAccount(last4: string | null | undefined): string {
  return last4 ? `•••• ${last4}` : '';
}
