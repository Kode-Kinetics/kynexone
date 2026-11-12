/**
 * Creating a payment batch, moving its WPS status, settling net pay and remitting statutory amounts all need the
 * `payroll.export` permission, which only Admin holds. The server answers a bare 403; this turns it into the real
 * reason so the screen never tells a user to "lock the run first" when it is already locked.
 */

export const PAYMENT_EXPORT_PERMISSION = 'payroll.export';

export type PaymentAction = 'create' | 'wps' | 'settle' | 'remit';

/** The English keys; every one has an Arabic entry in i18n/payrollGlUi.ts. */
export const PAYMENT_ACTION_DENIED: Record<PaymentAction, string> = {
  create: "You don't have permission to create payment batches — ask an administrator.",
  wps: "You don't have permission to generate or update WPS files — ask an administrator.",
  settle: "You don't have permission to settle net pay to the general ledger — ask an administrator.",
  remit: "You don't have permission to remit statutory amounts — ask an administrator.",
};

export function paymentActionsAllowed(hasPermission: (permission: string) => boolean): boolean {
  return hasPermission(PAYMENT_EXPORT_PERMISSION);
}

/**
 * The permission message for a 403, or null when the failure is something else. A 403 that carries the server's own
 * sentence (a separation-of-duties refusal, say) keeps it: that one explains itself.
 */
export function paymentActionRefusal(err: unknown, action: PaymentAction): string | null {
  const response = (err as { response?: { status?: number; data?: unknown } } | null)?.response;
  if (response?.status !== 403) return null;
  const data = response.data as { message?: unknown } | string | null;
  const own = typeof data === 'object' && data !== null && typeof data.message === 'string' ? data.message.trim() : '';
  return own || PAYMENT_ACTION_DENIED[action];
}
