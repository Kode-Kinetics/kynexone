/**
 * The "out of N" a leave balance can honestly show. The self-service balance carries `entitled` only, but a policy that accrues
 * month by month grants its days through `accrued` and leaves `entitled` at 0, and carried-forward days push the balance past `entitled`
 * too. "16.25 of 0 days available" (and "19 of 15") is wrong either way, so a total is shown only when it can contain the balance.
 */
export function leaveBalanceTotal(b: { available: number; entitled: number }): number | null {
  return b.entitled > 0 && b.available <= b.entitled ? b.entitled : null;
}

/** The days a balance grants this year, for the HR table: the server's `granted` (max of entitled and accrued), else the same rule here. */
export function grantedDays(b: { granted?: number; entitled: number; accrued: number }): number {
  return b.granted ?? Math.max(b.entitled, b.accrued);
}
