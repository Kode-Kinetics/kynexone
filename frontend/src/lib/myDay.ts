/**
 * "My day": the pure derivations behind the employee home (views/EmployeeSelfServicePage.tsx).
 * Nothing here reads the network or the clock on its own: the page passes what the API returned
 * and the current time, so every figure on screen can be traced to a field and tested without a browser.
 */

import type { EssDashboard, EssHrRequest } from '../api/ess';
import type { MessageParams } from '../i18n/translations';
import type { LeaveRequest } from '../api/leave';
import type { SalaryAdvance } from '../api/loans';
import { enumLabel, normaliseEnumValue } from '../i18n/enumLabel';
import { ESS_PAYSLIPS_PATH } from './essPayslip';
import { ESS_LEAVE_PATH, ESS_REQUESTS_PATH, canCancelLeave, hrRequestStatus, splitMinutes, type StatusTone } from './essSelfService';

export type Translate = (key: string, params?: MessageParams) => string;
/** The slice of the formatter these derivations use. */
export interface MyDayFormat {
  date: (d: string | Date, style?: 'dayMonth' | 'medium') => string;
  dateTime: (d: string | Date, style?: 'dayMonth' | 'medium') => string;
  time: (d: string | Date) => string;
  number: (n: number) => string;
}

const DAY_MS = 86_400_000;
/** A punch still open after this long is yesterday's, not a live clock. */
const MAX_OPEN_SECONDS = 16 * 3600;
/** A decided request stays in the tracker this long, so the employee sees the answer once. */
const RECENT_DECISION_DAYS = 7;
/** ...and it is the employee's "next step" for this long. */
const FRESH_DECISION_DAYS = 2;

// ── dates ─────────────────────────────────────────────────────────────────────

/** "2026-11-15" in the viewer's own calendar day. */
export function dayKey(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** The calendar date of an API value ("2026-11-15" or "2026-11-15T09:00:00Z"), without a timezone shift. */
export function dateOnly(value: string | null | undefined): string | null {
  const m = value ? /^(\d{4}-\d{2}-\d{2})/.exec(value) : null;
  return m ? m[1] : null;
}

/** Whole calendar days from `from` to `to` (both "YYYY-MM-DD"); negative when `to` is earlier. */
export function daysBetween(from: string, to: string): number {
  const a = Date.UTC(+from.slice(0, 4), +from.slice(5, 7) - 1, +from.slice(8, 10));
  const b = Date.UTC(+to.slice(0, 4), +to.slice(5, 7) - 1, +to.slice(8, 10));
  return Math.round((b - a) / DAY_MS);
}

/** "today", "tomorrow", "in 11 days", "3 days ago" as whole sentences, so Arabic orders its own plurals. */
export function whenLabel(t: Translate, days: number): string {
  if (days === 0) return t('Today');
  if (days === 1) return t('Tomorrow');
  if (days > 1) return t('{count, plural, one {in # day} other {in # days}}', { count: days });
  return t('{count, plural, one {# day ago} other {# days ago}}', { count: -days });
}

// ── today's hours ─────────────────────────────────────────────────────────────

type Attendance = NonNullable<EssDashboard['attendanceToday']>;

/**
 * Seconds worked so far, ticking, ONLY when it can be derived honestly. The API sums closed
 * in/out pairs, so an open punch adds nothing to `totalWorkedMinutes`: with no closed time, the
 * open period began at the first punch and `now - firstIn` is the day's hours. Once there are closed
 * periods the start of the current one is not in the payload, so a live figure would be a guess:
 * the caller shows the closed total instead and says so.
 */
export function liveWorkedSeconds(a: Attendance | null, nowMs: number): number | null {
  if (!a || !a.firstInUtc || a.lastOutUtc || a.missingPunch || a.totalWorkedMinutes > 0) return null;
  const start = Date.parse(a.firstInUtc);
  if (Number.isNaN(start)) return null;
  const seconds = Math.floor((nowMs - start) / 1000);
  // A clock a little behind the server reads zero, never negative; a punch from yesterday is not live.
  if (seconds > MAX_OPEN_SECONDS) return null;
  return Math.max(0, seconds);
}

/** True while a punch is open and the day has not been flagged as missing one. */
export function isClockedIn(a: Attendance | null): boolean {
  return !!a && !!a.firstInUtc && !a.lastOutUtc && !a.missingPunch;
}

/** h:mm and the seconds apart, for the big figure. */
export function clockParts(totalSeconds: number): { hm: string; ss: string } {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const p = (n: number) => String(n).padStart(2, '0');
  return { hm: `${h}:${p(m)}`, ss: p(s % 60) };
}

// ── request tracker ───────────────────────────────────────────────────────────

export type StepState = 'done' | 'current' | 'todo' | 'problem';
export interface TrackerStep { key: string; label: string; detail?: string; state: StepState }
export interface TrackerItem {
  id: string;
  kind: 'hr' | 'leave' | 'advance';
  title: string;
  meta: string;
  statusLabel: string;
  tone: StatusTone;
  /** Something is waiting for the employee (an unread HR reply, a fresh decision). */
  needsYou: boolean;
  steps: TrackerStep[];
  /** Plain-text line under the steps (HR's reply is read on the requests page, not here). */
  note?: string;
  action?: { label: string; href: string };
  /** Newest first within a group. */
  sortAt: number;
}

interface TrackerCtx { t: Translate; fx: MyDayFormat; nowMs: number }

const ts = (iso: string | null | undefined): number => { const n = iso ? Date.parse(iso) : NaN; return Number.isNaN(n) ? 0 : n; };

export function hrRequestItem(r: EssHrRequest, seen: ReadonlySet<string>, { t, fx }: TrackerCtx): TrackerItem {
  const status = hrRequestStatus(r.responseStatus);
  const closed = r.responseStatus === 'Closed' || r.status === 'Closed';
  const replied = r.responseStatus === 'Responded';
  const overdue = r.isOverdue || r.responseStatus.startsWith('Overdue');
  const needsYou = replied && !closed && !seen.has(r.id);
  const withHr: StepState = closed || replied ? 'done' : overdue ? 'problem' : 'current';
  const steps: TrackerStep[] = [
    { key: 'sent', label: t('Sent'), detail: fx.dateTime(r.createdAtUtc, 'dayMonth'), state: 'done' },
    {
      key: 'hr', label: t('With HR'), state: withHr,
      detail: r.dueAtUtc ? (overdue && !replied ? t('Overdue since {date}', { date: fx.date(r.dueAtUtc, 'dayMonth') }) : t('Due on {date}', { date: fx.date(r.dueAtUtc, 'dayMonth') })) : undefined,
    },
    { key: 'reply', label: t('HR replied'), state: closed ? 'done' : replied ? 'current' : 'todo', detail: replied && !closed ? t('Waiting for you') : undefined },
    { key: 'closed', label: t('Closed'), state: closed ? 'done' : 'todo' },
  ];
  return {
    id: `hr-${r.id}`, kind: 'hr', title: r.subject,
    meta: t('{category}, raised on {date}', { category: t(r.categoryName), date: fx.date(r.createdAtUtc, 'dayMonth') }),
    statusLabel: t(status.label), tone: status.tone, needsYou, steps,
    action: { label: needsYou ? t('Read the reply') : t('Open request'), href: `${ESS_REQUESTS_PATH}?open=${encodeURIComponent(r.id)}` },
    sortAt: ts(r.createdAtUtc),
  };
}

const roleLabel = (t: Translate, role: string): string => {
  const r = role.toLowerCase();
  if (r.includes('manager')) return t('Manager approval');
  if (r.includes('hr')) return t('HR approval');
  return role;
};

function leaveSteps(l: LeaveRequest, { t, fx }: TrackerCtx): TrackerStep[] {
  const sent: TrackerStep = { key: 'sent', label: t('Sent'), detail: fx.date(l.submittedAtUtc ?? l.createdAtUtc, 'dayMonth'), state: 'done' };
  const approved = l.status === 'Approved';
  const rejected = l.status === 'Rejected';
  const decided = approved || rejected;
  const approvals = [...(l.approvals ?? [])].sort((a, b) => a.stepNumber - b.stepNumber);
  let middle: TrackerStep[];
  if (approvals.length > 0) {
    let seenPending = false;
    middle = approvals.map((a) => {
      const acted = /approv/i.test(a.decision) || /reject/i.test(a.decision);
      const state: StepState = /reject/i.test(a.decision) ? 'problem' : acted ? 'done' : (!seenPending && !decided ? 'current' : 'todo');
      if (!acted) seenPending = true;
      const who = a.approverName?.trim();
      const when = a.actedAtUtc ? fx.date(a.actedAtUtc, 'dayMonth') : '';
      return { key: `a${a.stepNumber}`, label: roleLabel(t, a.approverRole), state, detail: [who, when].filter(Boolean).join(', ') || undefined };
    });
    // The rows that exist are the stages reached so far: a stage still to come is named from the status.
    const waitingOn = l.status === 'PendingHRApproval' ? t('HR approval') : l.status === 'PendingManagerApproval' ? t('Manager approval') : null;
    if (!decided && waitingOn && !middle.some((m) => m.label === waitingOn)) {
      middle = [...middle.map((m) => (m.state === 'current' ? { ...m, state: 'done' as const } : m)), { key: 'waiting', label: waitingOn, state: 'current' }];
    }
  } else {
    // The list read carries no approval rows: name the two stages by the status the server reports.
    const atHr = l.status === 'PendingHRApproval';
    middle = [
      { key: 'mgr', label: t('Manager approval'), state: atHr || decided ? 'done' : 'current' },
      { key: 'hr', label: t('HR approval'), state: decided ? 'done' : atHr ? 'current' : 'todo' },
    ];
  }
  const final: TrackerStep = approved
    ? { key: 'final', label: t('Approved'), state: 'done' }
    : rejected
      ? { key: 'final', label: t('Not approved'), state: 'problem', detail: l.rejectionReason || undefined }
      : { key: 'final', label: t('Approved'), state: 'todo' };
  return [sent, ...middle, final];
}

/** Leave that is waiting, or was decided in the last week. Anything else lives on the leave page. */
export function leaveItem(l: LeaveRequest, leaveName: (id: string | null, stored: string) => string, ctx: TrackerCtx): TrackerItem | null {
  const pending = canCancelLeave(l.status);
  const decidedAt = ts(l.decidedAtUtc);
  const recent = (l.status === 'Approved' || l.status === 'Rejected') && decidedAt > 0 && ctx.nowMs - decidedAt <= RECENT_DECISION_DAYS * DAY_MS;
  if (!pending && !recent) return null;
  const { t, fx } = ctx;
  const rejected = l.status === 'Rejected';
  return {
    id: `leave-${l.id}`, kind: 'leave',
    title: t('{type}, {start} to {end}', { type: leaveName(l.leaveTypeId, l.leaveTypeName), start: fx.date(l.startDate, 'dayMonth'), end: fx.date(l.endDate, 'dayMonth') }),
    meta: t('{count, plural, one {# day} other {# days}}, raised on {date}', { count: l.totalDays, date: fx.date(l.submittedAtUtc ?? l.createdAtUtc, 'dayMonth') }),
    statusLabel: pending ? t('Waiting for approval') : rejected ? t('Not approved') : t('Approved'),
    tone: pending ? 'amber' : rejected ? 'rose' : 'emerald',
    needsYou: recent && ctx.nowMs - decidedAt <= FRESH_DECISION_DAYS * DAY_MS, steps: leaveSteps(l, ctx),
    action: { label: t('Open my leave'), href: ESS_LEAVE_PATH },
    sortAt: ts(l.submittedAtUtc ?? l.createdAtUtc),
  };
}

/** A salary advance that is waiting for approval or for Finance to pay it out. */
export function advanceItem(a: SalaryAdvance, { t, fx, nowMs }: TrackerCtx, loansPath: string | null): TrackerItem | null {
  const paid = !!a.paidAtUtc || ['Active', 'Settled', 'Paid', 'Completed', 'Closed'].includes(a.status);
  const pending = a.status === 'Pending';
  const awaitingPayout = a.status === 'Approved' && !paid;
  const rejected = a.status === 'Rejected' && nowMs - ts(a.createdAtUtc) <= 14 * DAY_MS;
  if (!pending && !awaitingPayout && !rejected) return null;
  const steps: TrackerStep[] = [
    { key: 'sent', label: t('Sent'), detail: fx.date(a.createdAtUtc, 'dayMonth'), state: 'done' },
    { key: 'approval', label: t('Approval'), state: rejected ? 'problem' : pending ? 'current' : 'done', detail: rejected ? (a.rejectionReason || t('Not approved')) : undefined },
    { key: 'payout', label: t('Payout'), state: awaitingPayout ? 'current' : 'todo', detail: awaitingPayout ? t('With Finance') : undefined },
    { key: 'repay', label: t('Repayment'), state: 'todo' },
  ];
  return {
    id: `adv-${a.id}`, kind: 'advance', title: t('Salary advance'),
    meta: t('{reference}, raised on {date}', { reference: a.advanceNumber, date: fx.date(a.createdAtUtc, 'dayMonth') }),
    statusLabel: rejected ? t('Not approved') : pending ? t('Waiting for approval') : t('Approved, waiting for payout'),
    tone: rejected ? 'rose' : pending ? 'amber' : 'blue',
    needsYou: false, steps,
    action: loansPath ? { label: t('Open my loans'), href: loansPath } : undefined,
    sortAt: ts(a.createdAtUtc),
  };
}

/** Needs-you first, then newest. */
export function orderTracker(items: TrackerItem[]): TrackerItem[] {
  return [...items].sort((a, b) => Number(b.needsYou) - Number(a.needsYou) || b.sortAt - a.sortAt);
}

// ── coming up ─────────────────────────────────────────────────────────────────

export interface UpcomingEntry {
  id: string;
  /** "YYYY-MM-DD"; null when the source gave no date. */
  date: string | null;
  label: string;
  sub?: string;
  tone: 'normal' | 'warn' | 'pending';
  href?: string;
}

/** Dated entries in date order (earliest, including overdue, first), undated ones last. */
export function orderUpcoming(entries: UpcomingEntry[]): UpcomingEntry[] {
  return [...entries].sort((a, b) => {
    if (a.date && b.date) return a.date < b.date ? -1 : a.date > b.date ? 1 : 0;
    if (a.date) return -1;
    if (b.date) return 1;
    return 0;
  });
}

/**
 * The `max` entries to show, in date order. When there are more than fit, what needs the employee
 * (an expiring document, an HR task) stays and the furthest plain dates are the ones left out.
 */
export function pickUpcoming(entries: UpcomingEntry[], max: number): { shown: UpcomingEntry[]; hidden: number } {
  const ordered = orderUpcoming(entries);
  if (ordered.length <= max) return { shown: ordered, hidden: 0 };
  const needs = ordered.filter((e) => e.tone === 'warn');
  const room = Math.max(0, max - needs.length);
  const keep = new Set([...needs.slice(0, max), ...ordered.filter((e) => e.tone !== 'warn').slice(0, room)]);
  return { shown: ordered.filter((e) => keep.has(e)), hidden: ordered.length - keep.size };
}

// ── the hero's day ────────────────────────────────────────────────────────────

export interface DayView {
  /** Quiet tone (leave, holiday, rest day): nothing is asked of the employee today. */
  calm: boolean;
  pill: { text: string; tone: 'live' | 'bad' | 'plain' };
  figure: { kind: 'live' } | { kind: 'text'; text: string };
  line: string;
  facts: Array<{ value: string; label: string }>;
  footnote?: string;
  /** The employee is on approved leave today (drives the next-step wording). */
  onLeave: boolean;
}

const OFF_STATUSES = new Set(['OnLeave', 'Leave']);
const QUIET_STATUSES = new Set(['PublicHoliday', 'RestDay']);

/** Today's state for the hero, from the dashboard's own fields only. */
export function deriveDay(i: {
  t: Translate; fx: MyDayFormat; dashboard: EssDashboard; shiftName: string | null; today: string; nowMs: number;
  leaveName: (id: string | null, stored: string) => string;
}): DayView {
  const { t, fx, dashboard: d, today, nowMs } = i;
  const a = d.attendanceToday;
  const leave = d.nextApprovedLeave;
  const status = a ? normaliseEnumValue(a.status) : '';
  const start = dateOnly(leave?.startDate);
  const end = dateOnly(leave?.endDate);
  const onLeave = (!!start && !!end && start <= today && today <= end) || OFF_STATUSES.has(status);
  const worked = t('{hours} h {minutes} min', splitMinutes(a?.totalWorkedMinutes ?? 0));

  const facts: DayView['facts'] = [];
  if (!onLeave && d.attendanceTodaySource === 'processed' && a?.firstInUtc) {
    facts.push({ value: a.lateMinutes > 0 ? t('Late by {minutes} min', { minutes: a.lateMinutes }) : t('On time'), label: t('Arrival') });
  }
  if (!onLeave && i.shiftName) facts.push({ value: i.shiftName, label: t("Today's shift") });
  if (d.overtimeHoursThisMonth > 0) facts.push({ value: t('{hours} h', { hours: fx.number(d.overtimeHoursThisMonth) }), label: t('Approved overtime this month') });
  const footnote = d.attendanceTodaySource === 'raw' ? t('Provisional: lateness and status are confirmed when HR processes the day.') : undefined;
  const base = { facts, footnote, onLeave };

  if (onLeave) {
    return {
      ...base, calm: true, pill: { text: t('On leave today'), tone: 'plain' },
      figure: { kind: 'text', text: leave ? i.leaveName(null, leave.leaveTypeName) : enumLabel(t, 'AttendanceStatus', a?.status) },
      line: leave && start && end && start <= today && today <= end
        ? t('Leave from {start} to {end}', { start: fx.date(leave.startDate, 'dayMonth'), end: fx.date(leave.endDate, 'dayMonth') })
        : t('No work is expected today.'),
    };
  }
  if (QUIET_STATUSES.has(status)) {
    const label = enumLabel(t, 'AttendanceStatus', a!.status);
    return { ...base, calm: true, pill: { text: label, tone: 'plain' }, figure: { kind: 'text', text: label }, line: t('No work is expected today.') };
  }
  if (!a) {
    return {
      ...base, calm: false, pill: { text: t('No attendance record for today'), tone: 'plain' },
      figure: { kind: 'text', text: t('Not in yet') },
      line: t('Nothing is recorded yet today. Hours start counting from your first punch.'),
    };
  }
  if (a.missingPunch) {
    return {
      ...base, calm: false, pill: { text: t('A punch is missing'), tone: 'bad' },
      figure: { kind: 'text', text: a.firstInUtc ? t('In at {time}, no clock-out', { time: fx.time(a.firstInUtc) }) : t('Incomplete') },
      line: t("Today's hours can't be counted until the missing punch is added."),
    };
  }
  if (isClockedIn(a)) {
    const pill = { text: t('Clocked in at {time}', { time: fx.time(a.firstInUtc!) }), tone: 'live' as const };
    if (liveWorkedSeconds(a, nowMs) !== null) {
      return { ...base, calm: false, pill, figure: { kind: 'live' }, line: t('Hours worked today, counting since your {time} punch', { time: fx.time(a.firstInUtc!) }) };
    }
    return a.totalWorkedMinutes > 0
      ? { ...base, calm: false, pill, figure: { kind: 'text', text: worked }, line: t('Hours in finished periods. The current period is added when you clock out.') }
      : { ...base, calm: false, pill, figure: { kind: 'text', text: t('Still clocked in') }, line: t('This punch is from an earlier time. Hours are counted when you clock out.') };
  }
  const outAt = a.lastOutUtc ? fx.time(a.lastOutUtc) : null;
  return {
    ...base, calm: false,
    pill: { text: outAt ? t('Clocked out at {time}', { time: outAt }) : enumLabel(t, 'AttendanceStatus', a.status), tone: 'plain' },
    figure: { kind: 'text', text: worked }, line: t('Hours worked today'),
  };
}

// ── "Your next step" ──────────────────────────────────────────────────────────

/** A title built from a sentence and user-entered values, so the page can isolate each value (`<bdi>`). */
export interface TitleMessage { message: string; values: Record<string, string> }

export interface AttentionItem {
  id: string;
  tone: 'rose' | 'amber' | 'blue';
  title: string;
  titleMessage?: TitleMessage;
  detail: string;
  action?: { label: string; href: string };
}

export interface NextStep {
  calm: boolean;
  eyebrow: string;
  title: string;
  titleMessage?: TitleMessage;
  why: string;
  /** Null when the employee has nothing to press (a read-only login with an item that needs HR). */
  cta: { label: string; href?: string; retry?: boolean } | null;
}

/**
 * The one thing the employee should do, first match wins: a missing punch, HR's unread reply, a fresh
 * leave decision, an expiring document, an HR task. With none of them, an honest "nothing waiting"
 * (or a "could not check" when a source failed, never a false all-clear).
 */
export function buildNextStep(i: {
  t: Translate; fx: MyDayFormat; dashboard: EssDashboard; canWrite: boolean; today: string;
  unseenReplies: EssHrRequest[]; decidedLeave: TrackerItem[]; openRequests: number; sourceFailed: boolean; onLeave: boolean;
}): { step: NextStep; attention: AttentionItem[] } {
  const { t, fx, dashboard: d, canWrite } = i;
  const attention: AttentionItem[] = [];
  const a = d.attendanceToday;
  if (a?.missingPunch) {
    attention.push({
      id: 'missing-punch', tone: 'rose', title: t('A punch is missing from today'), detail: t('Ask HR to correct it so the day is counted.'),
      action: canWrite ? { label: t('Ask for a correction'), href: `${ESS_REQUESTS_PATH}?subject=${encodeURIComponent(t('Attendance correction for {date}', { date: fx.date(a.workDate) }))}` } : undefined,
    });
  }
  if (i.unseenReplies.length === 1) {
    const r = i.unseenReplies[0];
    attention.push({
      id: `reply-${r.id}`, tone: 'blue', title: t('HR replied to “{subject}”', { subject: r.subject }),
      titleMessage: { message: 'HR replied to “{subject}”', values: { subject: r.subject } },
      detail: t('{category}, raised on {date}', { category: t(r.categoryName), date: fx.date(r.createdAtUtc) }),
      action: { label: t('Read the reply'), href: `${ESS_REQUESTS_PATH}?open=${encodeURIComponent(r.id)}` },
    });
  } else if (i.unseenReplies.length > 1) {
    attention.push({
      id: 'replies', tone: 'blue', title: t('HR replied to {count} of your requests', { count: i.unseenReplies.length }),
      detail: t('Open each request to read the reply.'), action: { label: t('Read the replies'), href: ESS_REQUESTS_PATH },
    });
  }
  for (const l of i.decidedLeave) {
    attention.push({
      id: l.id, tone: l.tone === 'rose' ? 'rose' : 'blue',
      title: l.tone === 'rose' ? t('Your leave request was not approved') : t('Your leave request is approved'),
      detail: l.title, action: { label: t('Open my leave'), href: ESS_LEAVE_PATH },
    });
  }
  for (const doc of d.documentAlerts) {
    attention.push({
      id: `doc-${doc.id}`, tone: 'amber', title: t('{document} is expiring', { document: t(doc.documentType) }),
      titleMessage: { message: '{document} is expiring', values: { document: t(doc.documentType) } },
      detail: doc.expiryDate ? t('Expires on {date}', { date: fx.date(doc.expiryDate) }) : t('Expiry date not set'),
      action: canWrite ? { label: t('Tell HR'), href: ESS_REQUESTS_PATH } : undefined,
    });
  }
  for (const item of d.actionItems) {
    attention.push({
      id: `task-${item.id}`, tone: 'amber', title: item.title,
      detail: item.dueAtUtc ? t('Due on {date}', { date: fx.date(item.dueAtUtc) }) : t('Asked of you by HR'),
      action: item.category === 'EmployeeProfileCompletion' ? { label: t('Complete my details'), href: '/ess/onboarding' } : undefined,
    });
  }

  const lead = attention.find((x) => x.action) ?? attention[0];
  if (lead) {
    return { attention, step: { calm: false, eyebrow: t('Your next step'), title: lead.title, titleMessage: lead.titleMessage, why: lead.detail, cta: lead.action ? { label: lead.action.label, href: lead.action.href } : null } };
  }
  if (i.sourceFailed) {
    return {
      attention,
      step: { calm: false, eyebrow: t('Your next step'), title: t('We could not check everything'), why: t('Some of your requests could not be loaded, so replies from HR may be missing here.'), cta: { label: t('Retry'), retry: true } },
    };
  }
  if (i.onLeave) {
    return {
      attention,
      step: { calm: true, eyebrow: t('Nothing needs you'), title: t('Enjoy your time off'), why: t('Requests and pay continue as normal. We will show anything new here.'), cta: { label: t('Open my leave'), href: ESS_LEAVE_PATH } },
    };
  }
  return {
    attention,
    step: {
      calm: true, eyebrow: t('All caught up'), title: t('Nothing is waiting for you'),
      why: i.openRequests > 0
        ? t('{count, plural, one {# request is still being handled. Its answer will show here.} other {# requests are still being handled. Their answers will show here.}}', { count: i.openRequests })
        : t('You have no open requests.'),
      cta: canWrite ? { label: t('Request leave'), href: ESS_LEAVE_PATH } : { label: t('Open my payslips'), href: ESS_PAYSLIPS_PATH },
    },
  };
}
