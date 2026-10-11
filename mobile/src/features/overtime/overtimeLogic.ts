// ============================================================
// Overtime request logic — pure, unit-tested (tests/leavePay.test.ts). No react / react-native imports.
//
// Times are wall-clock 'HH:mm' on the company calendar. Crossing midnight is an EXPLICIT choice
// ("Ends the next day"): the old screen silently added 24h whenever end < start, so a 09:00 → 08:00
// typo became a 23-hour request.
// ============================================================

/** Hard ceiling for one request, whatever the policy says: anything longer is a typo, not overtime. */
export const MAX_OVERTIME_MINUTES = 12 * 60;

const WALL = /^(\d{1,2}):(\d{2})$/;

export function parseWallMinutes(value: string | null | undefined): number | null {
  const m = WALL.exec((value ?? '').trim());
  if (!m) return null;
  const h = Number(m[1]);
  const min = Number(m[2]);
  if (h > 23 || min > 59) return null;
  return h * 60 + min;
}

/** Minutes from start to end. null when the end is not after the start on the chosen day(s). */
export function overtimeDurationMinutes(start: string, end: string, endsNextDay: boolean): number | null {
  const s = parseWallMinutes(start);
  const e = parseWallMinutes(end);
  if (s === null || e === null) return null;
  const minutes = e + (endsNextDay ? 24 * 60 : 0) - s;
  return minutes > 0 ? minutes : null;
}

/** The policy limits the server applies (OvertimePolicyLimits). Non-positive = not configured. */
export interface OvertimePolicyLimits {
  minimumMinutes?: number | null;
  maximumMinutesPerDay?: number | null;
  roundingRule?: string | null;
}

/** Mirrors the server's rounding (OvertimePolicyLimits.ApplyRounding). Unknown rule = unchanged. */
export function applyRounding(minutes: number, rule: string | null | undefined): number {
  switch (rule ?? '') {
    case 'Nearest15':
      return Math.round(minutes / 15) * 15;
    case 'Up15':
      return Math.ceil(minutes / 15) * 15;
    case 'Down15':
      return Math.floor(minutes / 15) * 15;
    case 'Nearest30':
      return Math.round(minutes / 30) * 30;
    default:
      return minutes;
  }
}

export type OvertimeIssue =
  | 'missingTimes'
  | 'endNotAfterStart'
  | 'overHardCap'
  | 'belowPolicyMinimum'
  | 'abovePolicyDailyMax';

export interface OvertimeCheck {
  /** Raw worked minutes, when the times make sense. */
  minutes: number | null;
  /** Minutes after the policy's rounding (what payroll will count), when a policy is known. */
  roundedMinutes: number | null;
  issue: OvertimeIssue | null;
}

/**
 * Everything the form needs to decide whether the request can be sent. Policy limits are applied
 * only when the caller passes a policy it actually loaded; otherwise only the 12-hour ceiling holds.
 */
export function checkOvertime(input: {
  start: string | null;
  end: string | null;
  endsNextDay: boolean;
  policy?: OvertimePolicyLimits | null;
}): OvertimeCheck {
  if (!input.start || !input.end) return { minutes: null, roundedMinutes: null, issue: 'missingTimes' };
  const minutes = overtimeDurationMinutes(input.start, input.end, input.endsNextDay);
  if (minutes === null) return { minutes: null, roundedMinutes: null, issue: 'endNotAfterStart' };
  if (minutes > MAX_OVERTIME_MINUTES) return { minutes, roundedMinutes: null, issue: 'overHardCap' };
  const policy = input.policy;
  if (!policy) return { minutes, roundedMinutes: null, issue: null };
  const rounded = applyRounding(minutes, policy.roundingRule);
  const min = Number(policy.minimumMinutes ?? 0);
  const max = Number(policy.maximumMinutesPerDay ?? 0);
  if (min > 0 && rounded < min) return { minutes, roundedMinutes: rounded, issue: 'belowPolicyMinimum' };
  if (max > 0 && rounded > max) return { minutes, roundedMinutes: rounded, issue: 'abovePolicyDailyMax' };
  return { minutes, roundedMinutes: rounded, issue: null };
}

export function splitMinutes(total: number): { h: number; m: number } {
  const safe = Math.max(0, Math.round(total));
  return { h: Math.floor(safe / 60), m: safe % 60 };
}

/** 'YYYY-MM-DD' + 'HH:mm' on the wall clock of `timeZone` → UTC ISO instant. */
export function zonedWallTimeToUtcIso(dateIso: string, time: string, timeZone: string, addDays = 0): string {
  const d = /^(\d{4})-(\d{2})-(\d{2})$/.exec(dateIso);
  const t = parseWallMinutes(time);
  if (!d || t === null) throw new Error(`Invalid date/time: ${dateIso} ${time}`);
  const wallAsUtc = Date.UTC(Number(d[1]), Number(d[2]) - 1, Number(d[3]) + addDays, Math.floor(t / 60), t % 60);
  // The zone's offset at that moment; checked twice so a DST edge settles on the right side.
  let guess = wallAsUtc - zoneOffsetMs(wallAsUtc, timeZone);
  guess = wallAsUtc - zoneOffsetMs(guess, timeZone);
  return new Date(guess).toISOString();
}

function zoneOffsetMs(utcMs: number, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: 'numeric',
    day: 'numeric',
    hour: 'numeric',
    minute: 'numeric',
    second: 'numeric',
  }).formatToParts(new Date(utcMs));
  const get = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find((p) => p.type === type)?.value ?? 0);
  const asUtc = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour') % 24, get('minute'), get('second'));
  return asUtc - Math.floor(utcMs / 1000) * 1000;
}

// ---- Status -------------------------------------------------------------------------------

export type OvertimeStatusKind = 'waitingManager' | 'waitingHr' | 'pending' | 'approved' | 'rejected' | 'cancelled' | 'unknown';

/** Backend OvertimeRequest.Status: PendingManager → PendingHR → Approved | Rejected (Cancelled possible). */
export function overtimeStatusKind(raw: string | null | undefined): OvertimeStatusKind {
  const v = (raw ?? '').replace(/[\s_-]/g, '').toLowerCase();
  if (!v) return 'unknown';
  if (v === 'pendingmanager') return 'waitingManager';
  if (v === 'pendinghr') return 'waitingHr';
  if (v.startsWith('pending') || v === 'submitted') return 'pending';
  if (v.includes('approved')) return 'approved';
  if (v.includes('reject')) return 'rejected';
  if (v.includes('cancel')) return 'cancelled';
  return 'unknown';
}
