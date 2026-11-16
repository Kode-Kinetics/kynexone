/**
 * Who a payroll run leaves out, and who it pays with a warning, as the approver reads it.
 *
 * WHY. The server names each person with a stable reason code (PayPopulationReasons). Payroll used to skip
 * Probation, Confirmed, On leave, Resigned and Notice period staff with no word at all; the run now lists everyone
 * it does not pay. These helpers turn the codes into translated words and group the list by reason without ever
 * dropping a name or a code the screen does not know yet.
 */
import { msg, type MessageParams } from '../i18n/translations';
import type { PayrollPopulationEntry } from '../api/payroll';

type Translator = (key: string, params?: MessageParams) => string;

const REASON_LABELS: Record<string, string> = {
  NOT_YET_EMPLOYED: msg('Not employed yet'),
  LEFT_BEFORE_PERIOD: msg('Left before this period'),
  FINAL_WAGE_ALREADY_PAID: msg('Final wage already paid'),
  SUSPENDED_PAY_HOLD: msg('Suspended — not paid (pay hold)'),
  INACTIVE_OR_ARCHIVED: msg('Inactive or archived'),
  EXIT_STATUS_NO_DATE: msg('Exit status with no last working day'),
  DELETED_NO_WAGES_OWED: msg('Deleted — no wages owed'),
  UNKNOWN_STATUS: msg('Status not recognised'),
  EXCLUDED_FROM_RUN: msg('Excluded from this run'),
  SELECTION_NOT_ELIGIBLE: msg('Selected but not eligible'),
  WARN_NO_LAST_WORKING_DAY: msg('Resigned or on notice with no last working day — paid in full'),
  WARN_STATUS_ON_LEAVE_NO_LEAVE_REQUEST: msg('Status On leave with no approved leave this period — paid in full'),
  DELETED_BUT_WAGES_OWED: msg('Record deleted but wages owed — paid to the last working day'),
  WARN_EMPLOYED_STATUS_NOT_PAID: msg('Status says employed, but not paid by this run'),
};

/** The translated words for a reason code; a code the screen does not know is shown as is. */
export function payPopulationReasonLabel(t: Translator, reasonCode: string): string {
  const key = REASON_LABELS[reasonCode];
  return key ? t(key) : reasonCode;
}

export interface LeftOutGroup {
  reasonCode: string;
  entries: PayrollPopulationEntry[];
}

/** Groups the left-out list by reason, largest group first, keeping every entry. */
export function leftOutGroups(entries: readonly PayrollPopulationEntry[]): LeftOutGroup[] {
  const byReason = new Map<string, PayrollPopulationEntry[]>();
  for (const e of entries) {
    const list = byReason.get(e.reasonCode);
    if (list) list.push(e);
    else byReason.set(e.reasonCode, [e]);
  }
  return [...byReason.entries()]
    .map(([reasonCode, list]) => ({ reasonCode, entries: list }))
    .sort((a, b) => b.entries.length - a.entries.length);
}
