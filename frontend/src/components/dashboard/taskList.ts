import type { DashboardFull } from '../../api/dashboard';
import { msg } from '../../i18n/translations';

export type TaskTone = 'critical' | 'review';

export interface DashboardTask {
  id: 'approvals-overdue' | 'approvals' | 'payroll-validation' | 'docs-expired' | 'docs-expiring';
  tone: TaskTone;
  count: number;
  /** English dictionary key with {count}; the caller translates it. */
  title: string;
  detail: string;
  to: string;
  cta: string;
}

export interface TaskInputs {
  data: DashboardFull | null;
  /** Payroll validation errors for the current month's runs; null = not asked or unavailable. */
  payrollValidationErrors: number | null;
  now?: number;
}

/**
 * "Needs your attention": what the caller can act on today, from numbers the dashboard and payroll
 * overview already return (the dashboard is scoped to the caller's people on the server). Each task
 * is a count with one link that resolves it. Nothing is invented: a figure the API did not give is
 * a task that is not shown, never a zero.
 */
export function buildTasks({ data, payrollValidationErrors, now = Date.now() }: TaskInputs): DashboardTask[] {
  const tasks: DashboardTask[] = [];
  if (data) {
    const pending = data.overview.pendingApprovals ?? 0;
    const overdue = (data.overview.approvalQueue ?? []).filter(a => a.dueAtUtc && Date.parse(a.dueAtUtc) < now).length;
    if (overdue > 0) tasks.push({
      id: 'approvals-overdue', tone: 'critical', count: overdue,
      title: msg('{count, plural, one {# approval is past its due date} other {# approvals are past their due date}}'),
      detail: msg('Requests waiting on a decision after the time allowed'),
      to: '/approvals', cta: msg('Decide now'),
    });
    if (pending > 0) tasks.push({
      id: 'approvals', tone: 'review', count: pending,
      title: msg('{count, plural, one {# approval waiting for you} other {# approvals waiting for you}}'),
      detail: msg('Leave, overtime, changes and other requests that need a decision'),
      to: '/approvals', cta: msg('Open approvals'),
    });
  }
  if (payrollValidationErrors != null && payrollValidationErrors > 0) tasks.push({
    id: 'payroll-validation', tone: 'critical', count: payrollValidationErrors,
    title: msg('{count, plural, one {# payroll validation error} other {# payroll validation errors}}'),
    detail: msg('Blocking errors stop this month’s payroll from being approved'),
    to: '/payroll', cta: msg('Fix payroll errors'),
  });
  if (data) {
    const k = data.kpis;
    if ((k.expiredDocuments ?? 0) > 0) tasks.push({
      id: 'docs-expired', tone: 'critical', count: k.expiredDocuments,
      title: msg('{count, plural, one {# document has expired} other {# documents have expired}}'),
      detail: msg('A renewed copy is needed'),
      to: '/compliance?tab=employee-documents', cta: msg('Review expired documents'),
    });
    if ((k.expiringDocuments ?? 0) > 0) tasks.push({
      id: 'docs-expiring', tone: 'review', count: k.expiringDocuments,
      title: msg('{count, plural, one {# document expires within 60 days} other {# documents expire within 60 days}}'),
      detail: msg('Renew before they lapse'),
      to: '/compliance?tab=employee-documents', cta: msg('Review expiring documents'),
    });
  }
  // Critical first, then by how many people it touches; stable otherwise.
  return tasks
    .map((task, index) => ({ task, index }))
    .sort((a, b) => (a.task.tone === b.task.tone ? b.task.count - a.task.count || a.index - b.index : a.task.tone === 'critical' ? -1 : 1))
    .map(({ task }) => task);
}
