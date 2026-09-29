/**
 * Pure derivations for the HR Command Center. No React, no fetching — everything the
 * dashboard shows is computed here from the /api/dashboard/full payload plus the open
 * rules-engine findings, so the rules are testable and stated in one place.
 *
 * Doctrine carried over from the old page: a surface that cannot know something says so.
 * "No attendance captured today" is not "0% attendance", and a missing payroll run is not
 * a SAR 0 payroll.
 */

import type {
  ActivityFeedItem,
  ApprovalQueueItem,
  DashboardAlert,
  DashboardFull,
} from '../../api/dashboard';
import type { AIInsight } from '../../api/intelligence';

// ── Formatting ────────────────────────────────────────────────────────────────

export function fmtMoney(n: number, currency = 'SAR'): string {
  if (Math.abs(n) >= 1_000_000) return `${currency} ${(n / 1_000_000).toFixed(2)}M`;
  if (Math.abs(n) >= 1_000) return `${currency} ${(n / 1_000).toFixed(1)}K`;
  return `${currency} ${Math.round(n).toLocaleString()}`;
}

export function timeAgo(iso: string, now = Date.now()): string {
  const diff = now - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 2) return 'Just now';
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  if (days === 1) return 'Yesterday';
  if (days < 7) return `${days}d ago`;
  return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
}

function ageHours(iso: string, now = Date.now()): number {
  return (now - new Date(iso).getTime()) / 3_600_000;
}

export function ageLabel(hours: number): string {
  if (hours < 1) return 'under an hour';
  if (hours < 24) return `${Math.floor(hours)} h`;
  const d = Math.floor(hours / 24);
  return `${d} ${d === 1 ? 'day' : 'days'}`;
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

/** "EmployeeChangeRequest" → "Employee change request". */
function splitPascal(s: string): string {
  const spaced = s.replace(/[._]/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').trim().toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

// ── Approval requests ─────────────────────────────────────────────────────────

/** The approval queue's `module` is the backing entity name, not a product module. */
const ENTITY_LABEL: Record<string, string> = {
  EmployeeChangeRequest: 'Profile change',
  LeaveRequest: 'Leave request',
  AttendanceCorrection: 'Attendance correction',
  AttendanceCorrectionRequest: 'Attendance correction',
  AttendanceRegularizationRequest: 'Attendance correction',
  OvertimeRequest: 'Overtime request',
  PayrollRun: 'Payroll run',
  LoanRequest: 'Loan request',
  LoanApplication: 'Loan request',
  ExpenseClaim: 'Expense claim',
  HrLetterRequest: 'HR letter',
  OffboardingCase: 'Offboarding',
  Payroll: 'Payroll',
  Leave: 'Leave request',
  Attendance: 'Attendance',
  HR: 'HR request',
};

export function entityLabel(module: string): string {
  return ENTITY_LABEL[module] ?? splitPascal(module);
}

/** Pulls "EMP-SA-MGT-2026-0001 Abdulrahman Al-Saud" out of a title such as
 *  "Employee change approval - EMP-SA-MGT-2026-0001 Abdulrahman Al-Saud". Used only when
 *  the API does not send employee fields itself. */
export function parseApprovalSubject(title: string): { code: string | null; name: string | null; kind: string | null } {
  const m = title.match(/\b([A-Z]{2,5}(?:-[A-Z0-9]{2,8}){1,5})\s+(.+?)\s*$/);
  if (m) return { code: m[1], name: m[2], kind: null };
  // "Annual Leave - Raj Krishnamurthy" / "Annual Leave — Raj Krishnamurthy"
  const d = title.match(/^(.+?)\s+[-\u2013\u2014]\s+(.+)$/);
  if (d) return { code: null, name: d[2].trim(), kind: d[1].trim() };
  return { code: null, name: null, kind: null };
}

export interface ApprovalGroup {
  key: string;
  employeeName: string | null;
  employeeCode: string | null;
  employeeId: number | null;
  items: ApprovalQueueItem[];
  /** "4 profile changes", "Leave request", "Profile change + leave request". */
  summary: string;
  oldestAtUtc: string;
  oldestAgeHours: number;
}

/** One card per person. Four separate profile-change requests for the same employee are
 *  one decision context for the approver, so they read as one row. */
export function groupApprovals(queue: ApprovalQueueItem[], now = Date.now()): ApprovalGroup[] {
  const groups = new Map<string, ApprovalGroup>();
  for (const item of queue) {
    const parsed = parseApprovalSubject(item.title);
    const name = item.employeeName ?? parsed.name;
    const code = item.employeeCode ?? parsed.code;
    const key = item.employeeId != null ? `id:${item.employeeId}` : code ? `code:${code}` : `item:${item.id}`;
    const existing = groups.get(key);
    if (existing) {
      existing.items.push(item);
      if (item.createdAtUtc < existing.oldestAtUtc) existing.oldestAtUtc = item.createdAtUtc;
    } else {
      groups.set(key, {
        key,
        employeeName: name,
        employeeCode: code,
        employeeId: item.employeeId ?? null,
        items: [item],
        summary: '',
        oldestAtUtc: item.createdAtUtc,
        oldestAgeHours: 0,
      });
    }
  }
  const out = [...groups.values()];
  for (const g of out) {
    const byKind = new Map<string, number>();
    for (const i of g.items) {
      const parsedKind = parseApprovalSubject(i.title).kind;
      const k = parsedKind ? parsedKind.charAt(0) + parsedKind.slice(1).toLowerCase() : entityLabel(i.module);
      byKind.set(k, (byKind.get(k) ?? 0) + 1);
    }
    g.summary = [...byKind.entries()]
      .map(([k, n], idx) => {
        const label = idx === 0 ? k : k.toLowerCase();
        return n > 1 ? `${n} ${label.toLowerCase()}s` : label;
      })
      .join(' + ');
    g.summary = g.summary.charAt(0).toUpperCase() + g.summary.slice(1);
    g.oldestAgeHours = ageHours(g.oldestAtUtc, now);
  }
  return out.sort((a, b) => a.oldestAtUtc.localeCompare(b.oldestAtUtc));
}

export interface AgingBucket { key: string; label: string; count: number; tone: 'ok' | 'warn' | 'late' | 'overdue' }

/** How long requests have waited — the one number that predicts an approvals problem. */
export function approvalAging(queue: ApprovalQueueItem[], now = Date.now()): AgingBucket[] {
  const b: AgingBucket[] = [
    { key: 'd0', label: '< 1 day', count: 0, tone: 'ok' },
    { key: 'd1', label: '1–3 days', count: 0, tone: 'warn' },
    { key: 'd3', label: '3–7 days', count: 0, tone: 'late' },
    { key: 'd7', label: '7+ days', count: 0, tone: 'overdue' },
  ];
  for (const q of queue) {
    const h = ageHours(q.createdAtUtc, now);
    b[h < 24 ? 0 : h < 72 ? 1 : h < 168 ? 2 : 3].count += 1;
  }
  return b;
}

// ── Rules-engine findings ─────────────────────────────────────────────────────

function normSeverity(s: string | undefined): 'Critical' | 'Warning' | 'Info' {
  const l = (s ?? '').toLowerCase();
  return l === 'critical' ? 'Critical' : l === 'warning' ? 'Warning' : 'Info';
}

/** The engine re-fires a rule roughly daily and never supersedes the older row, so the open
 *  list can hold the same finding several times. Keep the newest per rule. */
export function dedupeInsights(insights: AIInsight[]): AIInsight[] {
  const newest = new Map<string, AIInsight>();
  for (const i of insights) {
    const key = `${i.insightType || i.title}|${i.employeeId ?? ''}`;
    const cur = newest.get(key);
    if (!cur || i.createdAtUtc > cur.createdAtUtc) newest.set(key, i);
  }
  return [...newest.values()];
}

const INSIGHT_ROUTE: Record<string, string> = {
  MissingSalarySetup: '/payroll',
  InactiveSalaryStructure: '/payroll',
  PayrollVariance: '/payroll',
  OvertimeAnomaly: '/overtime',
  LeaveAccumulationRisk: '/leave',
  HeadcountTurnover: '/people',
  VisaExpiryRisk: '/compliance',
};

const INSIGHT_CTA: Record<string, string> = {
  MissingSalarySetup: 'Open salary setup',
  InactiveSalaryStructure: 'Open salary structures',
  PayrollVariance: 'Review payroll variance',
  OvertimeAnomaly: 'Review overtime',
  LeaveAccumulationRisk: 'Review leave balances',
  HeadcountTurnover: 'Review turnover',
  VisaExpiryRisk: 'Review visa expiries',
};

/** Where the records behind a rules finding live. */
export function insightRoute(insightType: string): string {
  return INSIGHT_ROUTE[insightType] ?? '/ai-assistant';
}

// ── Compliance deadlines ──────────────────────────────────────────────────────

export interface ComplianceDeadline {
  key: string;
  kind: string;
  employeeName: string | null;
  status: 'expired' | 'expiring';
  /** Human date, with year when the API provides an ISO date. */
  dateLabel: string | null;
  daysRemaining: number | null;
  count: number;
  severity: 'Critical' | 'Warning' | 'Info';
}

/** Accepts both the enriched alert shape and the legacy "Passport Number expired 01 Jan"
 *  title-only shape; identical legacy rows (no employee attached) collapse with a count. */
export function complianceDeadlines(alerts: DashboardAlert[]): ComplianceDeadline[] {
  const rows = new Map<string, ComplianceDeadline>();
  for (const a of alerts) {
    // The API's `kind` is a raw field key (iqama_number); the title carries the human label.
    let kind: string | null = null;
    let status: 'expired' | 'expiring' =
      a.daysRemaining != null ? (a.daysRemaining < 0 ? 'expired' : 'expiring') : /expired/i.test(a.title) ? 'expired' : 'expiring';
    let dateLabel: string | null = null;
    if (a.expiryDate) {
      const d = new Date(a.expiryDate);
      if (!Number.isNaN(d.getTime())) dateLabel = d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
    }
    if (!kind || !dateLabel) {
      const m = a.title.match(/^(.*?)\s+(expired|expires|expiring)\b\s*(?:on\s+|in\s+)?(.*)$/i);
      if (m) {
        kind = kind ?? m[1];
        if (!dateLabel && m[3]) dateLabel = m[3];
        if (a.daysRemaining == null) status = /expired/i.test(m[2]) ? 'expired' : 'expiring';
      }
    }
    kind = kind ?? (a.kind ? splitPascal(a.kind) : a.title);
    const key = `${a.kind ?? kind}|${a.employeeId ?? a.employeeName ?? ''}|${dateLabel ?? ''}|${status}`;
    const cur = rows.get(key);
    if (cur) cur.count += 1;
    else rows.set(key, {
      key,
      kind,
      employeeName: a.employeeName ?? null,
      status,
      dateLabel,
      daysRemaining: a.daysRemaining ?? null,
      count: 1,
      severity: normSeverity(a.severity),
    });
  }
  return [...rows.values()].sort((a, b) =>
    (a.status === 'expired' ? 0 : 1) - (b.status === 'expired' ? 0 : 1)
    || (a.daysRemaining ?? -9999) - (b.daysRemaining ?? -9999));
}

// ── Trend metrics ────────────────────────────────────────────────────────────

export interface KpiTile {
  key: string;
  label: string;
  value: string;
  /** Muted value (e.g. "No run yet") renders at body weight instead of display size. */
  muted?: boolean;
  sub: string;
  to: string;
  series?: Array<number | null>;
  seriesLabel?: string;
  /** Category labels under the chart (months, departments). */
  labels?: string[];
  chart?: 'area' | 'bars';
  /** Formats a single data point for its hover title. */
  format?: (v: number) => string;
}

// ── Workforce Pulse ───────────────────────────────────────────────────────────

export type PulseState = 'ready' | 'attention' | 'blocked' | 'unknown';

export interface PulseSegment {
  key: 'workforce' | 'attendance' | 'approvals' | 'payroll' | 'compliance';
  label: string;
  state: PulseState;
  value: string;
  detail: string;
  to: string;
  /** What the drill-down opens, stated plainly ("Open attendance"). */
  cta: string;
  /** When this figure is from: "Today, 05:58", "Sep 2026 run", "Rules check 2h ago". */
  asOf?: string;
  /** One clause for the summary sentence when this segment is not ready. */
  reason?: string;
}

const MONTH_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const PAYROLL_DONE = /approved|paid|locked|final|closed|posted|disbursed|completed/i;
const PAYROLL_IN_FLIGHT = /draft|calculat|process|review|pending|submitted/i;

export interface PulseInput {
  data: DashboardFull | null;
  insights: AIInsight[] | null;
  /** Hour of day in the tenant timezone. */
  tenantHour: number;
  now?: Date;
  payrollEnabled: boolean;
  /** "05:58" — when the dashboard payload was loaded (the API caches it for up to 60 s). */
  asOfTime?: string | null;
}

/** Hour the working day is treated as started, for "pre-shift" vs "not captured". */
export const SHIFT_START_HOUR = 8;

export type TodayAttendance =
  | { kind: 'unavailable' }
  | { kind: 'pre-shift' }
  | { kind: 'not-captured' }
  | { kind: 'counted'; present: number; expected: number; onLeave: number; absent: number; rate: number };

/** Today's attendance, one state only. Pre-shift, not captured, unavailable and zero differ. */
export function todayAttendance(data: DashboardFull | null, hour: number): TodayAttendance {
  if (!data) return { kind: 'unavailable' };
  if (!attendanceCaptured(data)) return hour < SHIFT_START_HOUR ? { kind: 'pre-shift' } : { kind: 'not-captured' };
  const s = data.summary;
  const expected = Math.max(0, s.activeEmployees - s.onLeave);
  return { kind: 'counted', present: s.presentToday, expected, onLeave: s.onLeave, absent: s.absent, rate: expected ? Math.round((s.presentToday / expected) * 100) : 0 };
}

export function attendanceCaptured(data: DashboardFull): boolean {
  const s = data.summary;
  if (typeof s.attendanceRecordsToday === 'number') return s.attendanceRecordsToday > 0;
  // Older API: no capture signal. Treat an all-zero day as not captured rather than as a
  // workforce that stayed home.
  return s.presentToday + s.onLeave + s.absent > 0;
}

export function buildPulse({ data, insights, tenantHour, now = new Date(), payrollEnabled, asOfTime }: PulseInput): PulseSegment[] {
  const asOfNow = asOfTime ? `As of ${asOfTime}` : undefined;
  if (!data) {
    const unknown = (key: PulseSegment['key'], label: string, to: string, cta: string): PulseSegment =>
      ({ key, label, state: 'unknown', value: 'Unavailable', detail: 'Dashboard data could not be loaded', to, cta });
    return [
      unknown('workforce', 'Workforce', '/people', 'Open people'),
      unknown('attendance', 'Attendance', '/attendance', 'Open attendance'),
      unknown('approvals', 'Approvals', '/approvals', 'Open approvals'),
      ...(payrollEnabled ? [unknown('payroll', 'Payroll', '/payroll', 'Open payroll')] : []),
      unknown('compliance', 'Compliance', '/compliance', 'Open compliance'),
    ];
  }
  const s = data.summary;
  const o = data.overview;
  const k = data.kpis;
  const open = insights ? dedupeInsights(insights) : [];

  // Workforce
  const workforce: PulseSegment = {
    key: 'workforce',
    label: 'Workforce',
    to: '/people',
    cta: 'Open people',
    asOf: asOfNow,
    value: `${s.activeEmployees.toLocaleString()} active`,
    detail: `${s.totalEmployees.toLocaleString()} on record, ${o.newJoinersThisMonth} joined this month`,
    state: s.activeEmployees === 0 ? 'blocked' : 'ready',
    reason: s.activeEmployees === 0 ? 'no active employees' : undefined,
  };

  // Attendance: TODAY only. Monthly rates live in Insights, labelled with their period.
  const today = todayAttendance(data, tenantHour);
  const attBase = { key: 'attendance' as const, label: 'Attendance', to: '/attendance', cta: 'Open attendance', asOf: asOfNow ? `Today, ${asOfTime}` : 'Today' };
  let attendance: PulseSegment;
  if (today.kind === 'pre-shift') {
    attendance = { ...attBase, state: 'unknown', value: 'Pre-shift', detail: 'Working day not started, no punches yet' };
  } else if (today.kind === 'not-captured') {
    attendance = { ...attBase, state: 'unknown', value: 'Not captured', detail: 'No punches recorded today' };
  } else if (today.kind === 'unavailable') {
    attendance = { ...attBase, state: 'unknown', value: 'Unavailable', detail: 'Could not be loaded' };
  } else {
    const late = tenantHour >= 11;
    const state: PulseState =
      today.rate >= 90 ? (k.attendanceExceptions > 0 ? 'attention' : 'ready')
      : today.rate >= 70 || !late ? 'attention' : 'blocked';
    attendance = {
      ...attBase, state,
      value: `${today.present.toLocaleString()} of ${today.expected.toLocaleString()} in`,
      detail: `${today.rate}% present, ${today.onLeave} on leave${k.attendanceExceptions ? `, ${k.attendanceExceptions} exceptions` : ''}`,
      reason: state === 'ready' ? undefined : `attendance today at ${today.rate}%`,
    };
  }

  // Approvals
  const oldest = o.approvalQueue.reduce<string | null>((m, i) => (!m || i.createdAtUtc < m ? i.createdAtUtc : m), null);
  const oldestH = oldest ? ageHours(oldest, now.getTime()) : 0;
  const approvals: PulseSegment = {
    key: 'approvals', label: 'Approvals', to: '/approvals', cta: 'Review approvals', asOf: asOfNow,
    state: o.pendingApprovals === 0 ? 'ready' : oldestH >= 72 ? 'blocked' : 'attention',
    value: o.pendingApprovals === 0 ? 'Clear' : `${o.pendingApprovals} waiting`,
    detail: o.pendingApprovals === 0 ? 'Nothing waiting for a decision' : oldest ? `Oldest waiting ${ageLabel(oldestH)}` : 'Awaiting decision',
    reason: o.pendingApprovals === 0 ? undefined : `${plural(o.pendingApprovals, 'approval')} waiting${oldestH >= 72 ? ', oldest over 3 days' : ''}`,
  };

  // Payroll readiness
  const segments: PulseSegment[] = [workforce, attendance, approvals];
  if (payrollEnabled) {
    const p = o.payrollSummary;
    // Must match the API's PeriodLabel ("MMM yyyy", invariant culture). Intl's en-GB says
    // "Sept", so the short names are spelled out rather than formatted.
    const monthLabel = `${MONTH_SHORT[now.getMonth()]} ${now.getFullYear()}`;
    const monthName = now.toLocaleDateString('en-US', { month: 'long' });
    const salaryGap = open.find((i) => i.insightType === 'MissingSalarySetup');
    const gapCount = salaryGap ? Number((salaryGap.title.match(/^(\d+)/) ?? [])[1] ?? NaN) : NaN;
    const currentRun = p && p.periodLabel === monthLabel ? p : null;
    let payroll: PulseSegment;
    if (currentRun && PAYROLL_DONE.test(currentRun.status)) {
      payroll = { key: 'payroll', label: 'Payroll', to: '/payroll', cta: 'Open payroll', asOf: `${currentRun.periodLabel} run`, state: 'ready',
        value: fmtMoney(currentRun.totalNet).replace(/^SAR /, ''), detail: `SAR net, ${currentRun.periodLabel}, ${currentRun.status.toLowerCase()}` };
    } else if (salaryGap) {
      payroll = { key: 'payroll', label: 'Payroll', to: '/payroll', cta: 'Open salary setup', asOf: `Rules check ${timeAgo(salaryGap.createdAtUtc, now.getTime()).toLowerCase()}`, state: 'blocked',
        value: Number.isFinite(gapCount) ? `${gapCount} without salary` : 'Salary gaps',
        detail: 'Excluded from payroll until set up',
        reason: Number.isFinite(gapCount) ? `${plural(gapCount, 'employee')} without a salary` : 'employees without a salary' };
    } else if (currentRun && PAYROLL_IN_FLIGHT.test(currentRun.status)) {
      payroll = { key: 'payroll', label: 'Payroll', to: '/payroll', cta: 'Open payroll run', asOf: `${currentRun.periodLabel} run`, state: 'attention',
        value: currentRun.status, detail: `${currentRun.periodLabel} run in progress`,
        reason: `${monthName} payroll still ${currentRun.status.toLowerCase()}` };
    } else {
      const late = now.getDate() >= 20;
      payroll = { key: 'payroll', label: 'Payroll', to: '/payroll', cta: 'Open payroll', asOf: p ? `Last run ${p.periodLabel}` : undefined, state: late ? 'attention' : 'ready',
        value: 'Not run', detail: p ? `Last run ${p.periodLabel}` : 'No payroll run yet',
        reason: late ? `${monthName} payroll not run` : undefined };
    }
    segments.push(payroll);
  }

  // Compliance — two sources, named separately so the numbers can be reconciled.
  const critical = o.complianceCriticalTotal ?? o.alerts.filter((a) => normSeverity(a.severity) === 'Critical').length;
  const records = o.complianceAlertsTotal ?? o.alerts.length;
  const docGaps = k.expiredDocuments + k.missingDocuments;
  const complianceState: PulseState =
    critical > 0 || docGaps > 0 ? 'blocked' : records > 0 || k.expiringDocuments > 0 ? 'attention' : 'ready';
  const parts: string[] = [];
  if (critical > 0) parts.push(plural(critical, 'expired record'));
  // kpis.missingDocuments counts EMPLOYEES missing at least one required document type.
  if (k.missingDocuments > 0) parts.push(`${plural(k.missingDocuments, 'employee')} missing required documents`);
  if (k.expiredDocuments > 0) parts.push(plural(k.expiredDocuments, 'expired document'));
  const expiringTotal = Math.max(0, records - critical) + k.expiringDocuments;
  if (expiringTotal > 0) parts.push(`${expiringTotal} expiring soon`);
  const headline = critical > 0 ? `${critical} expired` : k.missingDocuments > 0 ? `${k.missingDocuments} need docs` : k.expiredDocuments > 0 ? `${k.expiredDocuments} expired` : `${expiringTotal} expiring`;
  segments.push({
    key: 'compliance', label: 'Compliance', to: critical > 0 || expiringTotal > 0 ? '/compliance' : '/compliance?tab=employee-documents', cta: 'Open compliance', asOf: asOfNow, state: complianceState,
    value: complianceState === 'ready' ? 'In order' : headline,
    detail: complianceState === 'ready'
      ? 'No expiries in 60 days'
      : parts.length > 1 ? parts.slice(1).join(', ') : critical > 0 ? 'Records past their expiry date' : k.missingDocuments > 0 ? (k.missingDocuments === 1 ? 'Employee missing a required document' : 'Employees missing a required document') : 'Due for renewal within 60 days',
    reason: complianceState === 'ready' ? undefined : parts.join(', '),
  });

  return segments;
}

export function pulseSummary(segments: PulseSegment[]): { tone: PulseState; text: string } {
  const blocked = segments.filter((s) => s.state === 'blocked');
  const attention = segments.filter((s) => s.state === 'attention');
  if (segments.every((s) => s.state === 'unknown')) return { tone: 'unknown', text: 'Workforce status is unavailable right now.' };
  if (blocked.length) {
    return {
      tone: 'blocked',
      text: `${blocked.length === 1 ? '1 area is' : `${blocked.length} areas are`} blocked: ${blocked.map((s) => s.reason ?? s.label.toLowerCase()).join('; ')}.`,
    };
  }
  if (attention.length) {
    return { tone: 'attention', text: `Running, with ${attention.map((s) => s.reason ?? s.label.toLowerCase()).join('; ')}.` };
  }
  return { tone: 'ready', text: 'Everything that can be checked is in order.' };
}

// ── Needs attention ───────────────────────────────────────────────────────────

export interface AttentionItem {
  id: string;
  severity: 'critical' | 'warning';
  title: string;
  detail: string;
  /** Where the number comes from, so a reader can judge it. */
  source: string;
  to: string;
  cta: string;
}

export function buildAttention(data: DashboardFull | null, insights: AIInsight[] | null, now = Date.now()): AttentionItem[] {
  const items: AttentionItem[] = [];
  if (data) {
    const o = data.overview;
    const k = data.kpis;
    const critical = o.complianceCriticalTotal ?? o.alerts.filter((a) => normSeverity(a.severity) === 'Critical').length;
    if (critical > 0) items.push({
      id: 'compliance-expired', severity: 'critical',
      title: `${plural(critical, 'compliance record')} expired`,
      detail: 'Iqama, passport, visa or permit dates on employee records',
      source: 'Employee records', to: '/compliance', cta: 'Review expired records',
    });
    if (k.expiredDocuments > 0) items.push({
      id: 'docs-expired', severity: 'critical',
      title: `${plural(k.expiredDocuments, 'uploaded document')} expired`,
      detail: 'Past their expiry date; a renewed copy is needed',
      source: 'Employee documents', to: '/compliance?tab=employee-documents', cta: 'Review expired documents',
    });
    if (k.missingDocuments > 0) items.push({
      id: 'docs-missing', severity: 'critical',
      title: `${plural(k.missingDocuments, 'employee')} missing required documents`,
      detail: 'Each is missing at least one document type the policy requires',
      source: 'Employee documents', to: '/compliance?tab=employee-documents', cta: 'Review missing documents',
    });
    if (k.attendanceExceptions > 0) items.push({
      id: 'att-exceptions', severity: 'critical',
      title: `${plural(k.attendanceExceptions, 'attendance exception')}`,
      detail: 'Missed punches or anomalies awaiting review',
      source: 'Attendance', to: '/attendance', cta: 'Review exceptions',
    });
    if (k.expiringDocuments > 0) items.push({
      id: 'docs-expiring', severity: 'warning',
      title: `${plural(k.expiringDocuments, 'uploaded document')} expiring within 60 days`,
      detail: 'Renew before they lapse',
      source: 'Employee documents', to: '/compliance?tab=employee-documents', cta: 'Review expiring documents',
    });
    if (k.pendingAttendanceCorrections > 0) items.push({
      id: 'att-corrections', severity: 'warning',
      title: `${plural(k.pendingAttendanceCorrections, 'attendance correction')} pending`,
      detail: 'Employees asked for a punch to be corrected',
      source: 'Attendance', to: '/attendance', cta: 'Review corrections',
    });
    if (k.pendingLeaveRequests > 0) items.push({
      id: 'leave-pending', severity: 'warning',
      title: `${plural(k.pendingLeaveRequests, 'leave request')} pending`,
      detail: 'Waiting for a decision',
      source: 'Leave', to: '/leave', cta: 'Review leave requests',
    });
  }
  for (const i of dedupeInsights(insights ?? [])) {
    const sev = normSeverity(i.severity);
    if (sev === 'Info') continue;
    items.push({
      id: `insight-${i.insightType}-${i.id}`,
      severity: sev === 'Critical' ? 'critical' : 'warning',
      title: i.title.replace(/employee\(s\)/g, 'employees'),
      detail: i.summary,
      source: `Rules check · ${timeAgo(i.createdAtUtc, now).toLowerCase()} · all companies`,
      to: insightRoute(i.insightType),
      cta: INSIGHT_CTA[i.insightType] ?? 'Open findings',
    });
  }
  return items.sort((a, b) => (a.severity === b.severity ? 0 : a.severity === 'critical' ? -1 : 1));
}

// ── Activity ─────────────────────────────────────────────────────────────────

const ACTION_PHRASE: Array<[RegExp, string]> = [
  [/attendance\.raw_event\.created/i, 'Punch recorded'],
  [/attendance\.import\.completed|import completed/i, 'Attendance import completed'],
  [/attendance\.processed|processed/i, 'Attendance processed'],
  [/payroll\.run\.created/i, 'Payroll run created'],
  [/payroll\.run\.approved/i, 'Payroll run approved'],
  [/leave\.request\.approved/i, 'Leave approved'],
  [/leave\.request\.rejected/i, 'Leave rejected'],
  [/leave\.request\.submitted|leave\.request\.created/i, 'Leave requested'],
];

export function activitySentence(action: string): string {
  for (const [re, phrase] of ACTION_PHRASE) if (re.test(action)) return phrase;
  const words = action.replace(/[._]/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim().toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

export interface ActivityRow {
  key: string;
  module: string;
  sentence: string;
  actor: string;
  occurredAt: string;
  count: number;
}

/** Collapses runs of identical events (e.g. a device sync writing 40 punches) into one row. */
export function collapseActivity(feed: ActivityFeedItem[]): ActivityRow[] {
  const rows: ActivityRow[] = [];
  for (const item of feed) {
    const sentence = activitySentence(item.action);
    const actor = !item.actor || item.actor === 'System' ? 'Automated' : item.actor;
    const prev = rows[rows.length - 1];
    if (prev && prev.sentence === sentence && prev.actor === actor && prev.module === item.module
      && Math.abs(new Date(prev.occurredAt).getTime() - new Date(item.occurredAt).getTime()) < 30 * 60_000) {
      prev.count += 1;
      continue;
    }
    rows.push({ key: `${item.occurredAt}-${rows.length}`, module: item.module, sentence, actor, occurredAt: item.occurredAt, count: 1 });
  }
  return rows;
}

// ── Tenant clock ─────────────────────────────────────────────────────────────

export function tenantHour(timeZone?: string, now = new Date()): number {
  try {
    const h = new Intl.DateTimeFormat('en-GB', { hour: 'numeric', hourCycle: 'h23', timeZone }).format(now);
    const n = Number(h);
    return Number.isFinite(n) ? n : now.getHours();
  } catch {
    return now.getHours();
  }
}
