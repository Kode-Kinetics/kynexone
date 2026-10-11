// ============================================================
// Leave balance mapping — ONE definition for every mobile screen (Leave form, Home tile).
// Pure, unit-tested against real responses (tests/leavePay.test.ts). No imports.
//
// Every number is a server field; nothing is recomputed:
//   available — the server's EmployeeLeaveBalance.Available ("THE definition of a leave balance"):
//               Granted + CarriedForward + ManualAdjustment − Used − Pending − Encashed − Expired,
//               where Granted = max(Entitled, Accrued). Pending days are ALREADY taken out.
//   used      — days taken on approved leave this leave year (including any opening balance HR imported).
//   pending   — days held by requests still waiting for approval.
//   granted   — the year's grant (server `granted`; `entitled` on GET /ess/leave/balance, which omits it).
// Both GET /leave/balances/employee/{id} and GET /ess/leave/balance return these same fields.
// ============================================================

export interface LeaveBalanceRow {
  leaveTypeId: string;
  leaveTypeName: string;
  /** Leave year, when the endpoint says (the ESS endpoint does not). */
  year: number | null;
  available: number;
  used: number;
  pending: number;
  granted: number | null;
  carriedForward: number;
  manualAdjustment: number;
  encashed: number;
  expired: number;
  /** KSA statutory event leave (maternity, Hajj…): days per event; such leave has no running balance. */
  statutoryDays: number | null;
}

const num = (v: unknown): number => (v === null || v === undefined || !Number.isFinite(Number(v)) ? 0 : Number(v));
const numOrNull = (v: unknown): number | null =>
  v === null || v === undefined || v === '' || !Number.isFinite(Number(v)) ? null : Number(v);

export function mapLeaveBalanceRow(raw: any): LeaveBalanceRow {
  const year = numOrNull(raw?.year);
  return {
    leaveTypeId: String(raw?.leaveTypeId ?? ''),
    leaveTypeName: String(raw?.leaveTypeName ?? ''),
    year: year && year > 2000 ? year : null,
    available: num(raw?.available),
    used: num(raw?.used),
    pending: num(raw?.pending),
    granted: numOrNull(raw?.granted) ?? numOrNull(raw?.entitled),
    carriedForward: num(raw?.carriedForward),
    manualAdjustment: num(raw?.manualAdjustment),
    encashed: num(raw?.encashed),
    expired: num(raw?.expired),
    statutoryDays: numOrNull(raw?.statutoryEntitlementDays),
  };
}

/**
 * The plain "granted − taken − awaiting approval = left" line, ONLY when those three server fields
 * fully explain `available` (no carry-forward, adjustment, encashment or expiry in play). Otherwise
 * null: the screen shows the server's figures without a sum that would not add up.
 */
export function simpleBalanceEquation(b: LeaveBalanceRow): { granted: number; used: number; pending: number; available: number } | null {
  if (b.granted === null) return null;
  if (b.carriedForward || b.manualAdjustment || b.encashed || b.expired) return null;
  const sum = Math.round((b.granted - b.used - b.pending) * 100) / 100;
  if (Math.abs(sum - b.available) >= 0.005) return null;
  return { granted: b.granted, used: b.used, pending: b.pending, available: b.available };
}
