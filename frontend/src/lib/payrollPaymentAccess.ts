/**
 * Creating a payment batch, moving its WPS status, settling net pay and remitting statutory amounts need `payroll.export` (Admin) or the
 * narrow key of their own family (the seeded Payroll Manager prepares, the Finance Approver pays; see PaymentPermissions on the server). The server answers a bare 403; this turns it into the real
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

/** The narrow key of each action family; the server accepts it as well as payroll.export. */
export const PAYMENT_ACTION_KEYS: Record<PaymentAction, string> = {
  create: 'payroll.payment_batch.manage',
  wps: 'payroll.wps_status.update',
  settle: 'payroll.payment.settle',
  remit: 'payroll.remit.manage',
};

/** True when the user may reach the named actions (every family when none is named). The server still decides each action. */
export function paymentActionsAllowed(
  hasPermission: (permission: string) => boolean,
  actions: readonly PaymentAction[] = ['create', 'wps', 'settle', 'remit'],
): boolean {
  return hasPermission(PAYMENT_EXPORT_PERMISSION) || actions.some(a => hasPermission(PAYMENT_ACTION_KEYS[a]));
}

/**
 * The Saudi bank-file panel (availability, context, validate, preview, generate, download): the Payroll Manager prepares the file with
 * payroll.payment_batch.manage, and payroll.export keeps working. Mirrors SaudiBankExportsController.FileActor on the server.
 */
export function bankFileProductionAllowed(hasPermission: (permission: string) => boolean): boolean {
  return paymentActionsAllowed(hasPermission, ['create']);
}

/** Custom bank layout templates stay with payroll.export holders (the server refuses everyone else). */
export function bankTemplatesAllowed(hasPermission: (permission: string) => boolean): boolean {
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
